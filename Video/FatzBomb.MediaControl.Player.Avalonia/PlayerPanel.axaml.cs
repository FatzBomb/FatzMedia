using System.ComponentModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using FatzBomb.MediaControl.Avalonia;
using FatzBomb.MediaControl.Player.Shared;
using FatzBomb.MediaControl.Player.Shared.Hosting;
using FatzBomb.MediaControl.Player.Shared.ViewModels;

namespace FatzBomb.MediaControl.Player.Avalonia;

/// <summary>
/// One independent player: a shared <see cref="PlayerViewModel"/> (its own engine, transport,
/// effects, flyouts and log, bound in XAML) over this panel's <c>MediaSurface</c>. The code-behind
/// keeps only view behaviour: the seek slider's drag, the second screen, full screen, file drops,
/// the surface's presentation path and the per-player frame rate.
/// </summary>
public partial class PlayerPanel : UserControl
{
    private static readonly TimeSpan FullScreenChromeDelay = TimeSpan.FromSeconds(2.5);

    private readonly int _index;
    private readonly DispatcherTimer _chromeTimer = new() { Interval = FullScreenChromeDelay };
    private MediaControlPlayer? _framePlayer;
    private long _frames;
    private long _lastFrameTimestamp = Stopwatch.GetTimestamp();
    private bool _isDragging;
    private bool _isUpdatingSeek;
    private bool _isUpdatingSecondScreen;
    private bool _isFullScreen;
    private SecondaryPresentationWindow? _secondScreen;
    private bool _disposed;

    // Designer/XAML loader constructor.
    public PlayerPanel()
        : this(1, new RendererOption(RendererKind.Software, true, string.Empty), [], new PlayerStartupOptions())
    {
    }

    internal PlayerPanel(int index, RendererOption renderer, IReadOnlyList<string> nativePaths, PlayerStartupOptions options)
    {
        _index = index;
        InitializeComponent();

        var shell = new AvaloniaPlayerShell(this);
        ViewModel = new PlayerViewModel(
            options,
            new PlayerHost(AvaloniaPlayerDispatcher.Instance, new AvaloniaPlayerEngineFactory(renderer, nativePaths, Label), shell, shell, App.LogFilePath))
        {
            Label = Label,
            // The log is the most used flyout here (several players side by side): open at start.
            ActiveFlyout = PlayerFlyout.Log,
        };
        DataContext = ViewModel;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;

        // A drag scrubs: pressed begins it, every move seeks, released ends it. A key steps once.
        SeekSlider.AddHandler(PointerPressedEvent, OnSeekPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        SeekSlider.AddHandler(PointerReleasedEvent, OnSeekReleased, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        SeekSlider.PointerCaptureLost += OnSeekCaptureLost;
        SeekSlider.ValueChanged += OnSeekValueChanged;

        // --surface=framesink forces the readback path for comparison (Stage 027A slice 2).
        Surface.PreferGpuSharedSurface = StartupOptions.Current.PreferGpuSurface;
        Surface.PresentationPathChanged += OnPresentationPathChanged;
        Surface.Tapped += OnSurfaceTapped;
        Surface.DoubleTapped += OnSurfaceDoubleTapped;
        SecondScreenBox.IsCheckedChanged += OnSecondScreenChanged;
        FullScreenButton.Click += OnFullScreenClick;

        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
        PointerMoved += OnPanelPointerMoved;
        _chromeTimer.Tick += OnChromeTimerTick;
    }

    /// <summary>Gets this player's view model.</summary>
    public PlayerViewModel ViewModel { get; }

    /// <summary>Raised when the user asks for full screen (double-click, button); the window owns the layout.</summary>
    public event EventHandler? FullScreenToggleRequested;

    private string Label => $"P{_index}";

    /// <summary>Creates the engine and loads the startup media; then opens player 1's second screen when <c>--second-screen=</c> asked.</summary>
    internal async Task InitializeAsync()
    {
        await ViewModel.StartAsync();
        _lastFrameTimestamp = Stopwatch.GetTimestamp();

        // --second-screen=<n> opens player 1's second screen at startup.
        if (_index == 1 && StartupOptions.Current.SecondScreen is not null && ViewModel.Player is not null && !_disposed)
            SecondScreenBox.IsChecked = true;
    }

    internal async Task DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        _chromeTimer.Stop();
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        CloseSecondScreen();
        TrackFrames(null);
        // The surface lets go of the engine before it is disposed.
        Surface.Player = null;
        // Dispose waits for admitted media operations: the view model does it off the UI thread.
        await ViewModel.DisposeAsync();
    }

    internal void UpdateStatus(ProcessSample process)
    {
        if (ViewModel.Player is null)
            return;

        var now = Stopwatch.GetTimestamp();
        var elapsed = Math.Max(Stopwatch.GetElapsedTime(_lastFrameTimestamp, now).TotalSeconds, 0.001);
        _lastFrameTimestamp = now;
        var fps = Interlocked.Exchange(ref _frames, 0) / elapsed;

        PixelSize size = Surface.AttachedPixelSize;
        StatsText.Text =
            $"{Label} {ViewModel.PlaybackState} | {fps:F1} fps | CPU {process.CpuPercent:F0}% ({process.CoresUsed:F2} of {Environment.ProcessorCount} cores) | " +
            $"managed {process.ManagedMegabytes:F0} MB, working set {process.WorkingSetMegabytes:F0} MB (whole process) | surface {size.Width}x{size.Height} " +
            $"via {DescribePath(Surface.PresentationPath)}{DescribeSecondScreen()}";
    }

    /// <summary>Enters or leaves full screen: only the picture, with the controls shown while the pointer moves.</summary>
    internal void SetFullScreen(bool fullScreen)
    {
        _isFullScreen = fullScreen;
        ViewModel.CloseFlyout();
        Frame.Margin = fullScreen ? default : new Thickness(2);
        Frame.Padding = fullScreen ? default : new Thickness(6);
        Frame.BorderThickness = fullScreen ? default : new Thickness(1);
        ShowChrome(true);
        if (fullScreen)
            _chromeTimer.Start();
        else
            _chromeTimer.Stop();
    }

    /// <summary>Escape: closes the open flyout first; returns whether it did.</summary>
    internal bool CloseFlyout()
    {
        if (ViewModel.ActiveFlyout == PlayerFlyout.None)
            return false;

        ViewModel.CloseFlyout();
        return true;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PlayerViewModel.Player):
                TrackFrames(ViewModel.Player);
                break;
            case nameof(PlayerViewModel.SeekPositionSeconds):
            case nameof(PlayerViewModel.DurationSeconds):
                UpdateSeekSlider();
                break;
        }
    }

    private void TrackFrames(MediaControlPlayer? player)
    {
        if (_framePlayer is { } previous)
            previous.PresentationFramePresented -= OnFramePresented;
        _framePlayer = player;
        if (player is not null)
            player.PresentationFramePresented += OnFramePresented;
    }

    // Raised on the render thread.
    private void OnFramePresented(object? sender, EventArgs e) => Interlocked.Increment(ref _frames);

    private void UpdateSeekSlider()
    {
        _isUpdatingSeek = true;
        try
        {
            SeekSlider.Maximum = Math.Max(ViewModel.DurationSeconds, 1);
            if (!_isDragging)
                SeekSlider.Value = Math.Clamp(ViewModel.SeekPositionSeconds, 0, SeekSlider.Maximum);
        }
        finally
        {
            _isUpdatingSeek = false;
        }
    }

    private void OnSeekPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_isDragging || !ViewModel.CanSeek)
            return;

        _isDragging = true;
        ViewModel.BeginSeekPreview();
    }

    private void OnSeekReleased(object? sender, PointerReleasedEventArgs e) => EndDrag(cancelled: false);

    private void OnSeekCaptureLost(object? sender, PointerCaptureLostEventArgs e) => EndDrag(cancelled: true);

    private void EndDrag(bool cancelled)
    {
        if (!_isDragging)
            return;

        _isDragging = false;
        if (cancelled)
            ViewModel.CancelSeekPreview();
        else
            ViewModel.EndSeekPreview();
    }

    private void OnSeekValueChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (_isUpdatingSeek)
            return;

        // The pointer moved the thumb (a drag seek), or a key stepped it (one seek).
        if (_isDragging)
            ViewModel.SeekPositionSeconds = e.NewValue;
        else
            ViewModel.SeekTo(e.NewValue);
    }

    // A click on the picture closes the open flyout, as in the WPF player.
    private void OnSurfaceTapped(object? sender, TappedEventArgs e) => ViewModel.CloseFlyout();

    private void OnSurfaceDoubleTapped(object? sender, TappedEventArgs e)
    {
        FullScreenToggleRequested?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    private void OnFullScreenClick(object? sender, RoutedEventArgs e) => FullScreenToggleRequested?.Invoke(this, EventArgs.Empty);

    private void OnPanelPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isFullScreen)
            return;

        ShowChrome(true);
        _chromeTimer.Stop();
        _chromeTimer.Start();
    }

    private void OnChromeTimerTick(object? sender, EventArgs e)
    {
        _chromeTimer.Stop();
        // Keep the controls while a flyout is open or the pointer is on them.
        if (_isFullScreen && ViewModel.ActiveFlyout == PlayerFlyout.None && !_isDragging && !IsPointerOverChrome())
            ShowChrome(false);
    }

    private bool IsPointerOverChrome() =>
        TopBar.IsPointerOver || SeekRow.IsPointerOver || TransportRow.IsPointerOver || FlyoutHost.IsPointerOver;

    private void ShowChrome(bool visible)
    {
        TopBar.IsVisible = visible;
        SeekRow.IsVisible = visible;
        TransportRow.IsVisible = visible;
        FlyoutHost.IsVisible = visible;
        StatusLine.IsVisible = visible;
        StatsText.IsVisible = visible;
        Cursor = visible ? Cursor.Default : new Cursor(StandardCursorType.None);
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.Contains(DataFormat.File) && ViewModel.CanOpen ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        if (e.DataTransfer.TryGetFiles() is not { Length: > 0 } items || items[0].TryGetLocalPath() is not { } path)
            return;

        // LoadMediaAsync reports a failure in the status line and never throws.
        await ViewModel.LoadMediaAsync(path);
    }

    // Raised on the UI thread. The surface also raises an Info diagnostic for every MediaSurface.
    private void OnPresentationPathChanged(object? sender, EventArgs e)
    {
        if (Surface.PresentationPath != MediaSurfacePresentationPath.None)
        {
            ViewModel.AddLogEntry(
                MediaDiagnosticLevel.Info,
                "Surface",
                $"{Label} presents via {DescribePath(Surface.PresentationPath)}{(Surface.PresentationPathReason is { } reason ? $" ({reason})" : string.Empty)}");
        }
    }

    private string DescribeSecondScreen()
    {
        if (_secondScreen?.Surface is not { } surface)
            return string.Empty;

        PixelSize size = surface.AttachedPixelSize;
        return $" | 2nd screen {size.Width}x{size.Height} via {DescribePath(surface.PresentationPath)}{(surface.IsPresentationSuspended ? " (cleared: Audio mode)" : string.Empty)}";
    }

    /// <summary>Opens or closes this player's second screen (Stage 027A slice 5).</summary>
    private void OnSecondScreenChanged(object? sender, RoutedEventArgs e)
    {
        if (_isUpdatingSecondScreen)
            return;

        if (SecondScreenBox.IsChecked == true)
            OpenSecondScreen();
        else
            CloseSecondScreen();
    }

    private void OpenSecondScreen()
    {
        if (ViewModel.Player is not { } player || _secondScreen is not null || TopLevel.GetTopLevel(this) is not Window owner)
        {
            SetSecondScreenBox(false);
            return;
        }

        (Screen? screen, var fullScreen, var where) = ChooseSecondScreen(owner);
        var window = new SecondaryPresentationWindow(player, $"second-screen-{_index}") { Title = $"Player {_index} - second screen" };
        window.Surface.PreferGpuSharedSurface = StartupOptions.Current.PreferGpuSurface;
        window.Surface.PresentationPathChanged += OnSecondScreenPathChanged;
        window.Closed += OnSecondScreenClosed;
        _secondScreen = window;
        ViewModel.AddLogEntry(MediaDiagnosticLevel.Info, "Surface", $"{Label} second screen opens {where}");
        window.ShowOnScreen(screen, fullScreen, owner);
    }

    /// <summary>
    /// <c>--second-screen=&lt;n&gt;</c> when that screen exists, else the first screen that is not the
    /// main window's, full screen; with one screen, a window beside the player.
    /// </summary>
    private static (Screen? Screen, bool FullScreen, string Where) ChooseSecondScreen(Window owner)
    {
        IReadOnlyList<Screen> screens = owner.Screens.All;
        if (StartupOptions.Current.SecondScreen is { } number && number <= screens.Count)
            return (screens[number - 1], true, $"full screen on screen {number} of {screens.Count} ({screens[number - 1].DisplayName})");

        Screen? current = owner.Screens.ScreenFromWindow(owner);
        for (var i = 0; i < screens.Count; i++)
        {
            if (!Equals(screens[i], current))
                return (screens[i], true, $"full screen on screen {i + 1} of {screens.Count} ({screens[i].DisplayName})");
        }

        return (current, false, $"in a window (one screen{(StartupOptions.Current.SecondScreen is { } missing ? $"; screen {missing} does not exist" : string.Empty)})");
    }

    private void CloseSecondScreen()
    {
        SecondaryPresentationWindow? window = _secondScreen;
        if (window is null)
            return;

        _secondScreen = null;
        window.Closed -= OnSecondScreenClosed;
        window.Surface.PresentationPathChanged -= OnSecondScreenPathChanged;
        window.Close();
        SetSecondScreenBox(false);
    }

    // The user closed the window.
    private void OnSecondScreenClosed(object? sender, EventArgs e)
    {
        if (_secondScreen is not { } window || !ReferenceEquals(sender, window))
            return;

        window.Surface.PresentationPathChanged -= OnSecondScreenPathChanged;
        window.Closed -= OnSecondScreenClosed;
        _secondScreen = null;
        SetSecondScreenBox(false);
    }

    private void SetSecondScreenBox(bool isChecked)
    {
        _isUpdatingSecondScreen = true;
        try
        {
            SecondScreenBox.IsChecked = isChecked;
        }
        finally
        {
            _isUpdatingSecondScreen = false;
        }
    }

    private void OnSecondScreenPathChanged(object? sender, EventArgs e)
    {
        if (sender is MediaSurface { PresentationPath: not MediaSurfacePresentationPath.None } surface)
        {
            ViewModel.AddLogEntry(
                MediaDiagnosticLevel.Info,
                "Surface",
                $"{Label} second screen presents via {DescribePath(surface.PresentationPath)}{(surface.PresentationPathReason is { } reason ? $" ({reason})" : string.Empty)}");
        }
    }

    private static string DescribePath(MediaSurfacePresentationPath path) => path switch
    {
        MediaSurfacePresentationPath.SharedGpuImage => "shared GPU image",
        MediaSurfacePresentationPath.FrameSink => "frame sink",
        _ => "none"
    };
}
