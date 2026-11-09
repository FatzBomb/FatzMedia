using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using FatzMedia.Video.Avalonia;
using FatzMedia.Video.Sample.Player.Shared;
using FatzMedia.Video.Sample.Player.Shared.Hosting;
using FatzMedia.Video.Sample.Player.Shared.Presentation;
using FatzMedia.Video.Sample.Player.Shared.ViewModels;

namespace FatzMedia.Video.Sample.Player.Avalonia;

/// <summary>
/// One independent player: a shared <see cref="PlayerViewModel"/> (its own engine, transport,
/// effects, flyouts and log, bound in XAML) over this panel's <c>MediaSurface</c>. The code-behind
/// keeps only view behaviour: the seek slider's drag, the second screen, full screen, file drops
/// and the surface's presentation path. The full-screen chrome rule, the stats line and the
/// second-screen placement are shared with the other players (Player.Shared, Stage 046A slice 3).
/// </summary>
public partial class PlayerPanel : UserControl, IPlayerSlot
{
    readonly int _index;
    readonly bool _atStartup;
    readonly FullScreenChromeController _chrome;
    bool _isDragging;
    bool _isUpdatingSeek;
    bool _isUpdatingSecondScreen;
    SecondaryPresentationWindow? _secondScreen;
    bool _disposed;

    // Designer/XAML loader constructor.
    public PlayerPanel()
        : this(1, atStartup: false, new RendererOption(RendererKind.Software, true, string.Empty), [], new PlayerStartupOptions(), [])
    {
    }

    /// <param name="atStartup">
    /// Whether this panel is part of the window's first build: only then does player 1 open the
    /// <c>--second-screen=</c> screen (rebuilt players start empty, as in the WPF and WinForms players).
    /// </param>
    internal PlayerPanel(int index, bool atStartup, RendererOption renderer, IReadOnlyList<string> nativePaths, PlayerStartupOptions options, IReadOnlyList<string> startupWarnings)
    {
        _index = index;
        _atStartup = atStartup;
        InitializeComponent();
        // A click on the picture makes this the focused player, so F11 shows this one (ENG-044).
        Focusable = true;

        var shell = new AvaloniaPlayerShell(this);
        ViewModel = new PlayerViewModel(
            options,
            new PlayerHost(AvaloniaPlayerDispatcher.Instance, new AvaloniaPlayerEngineFactory(renderer, nativePaths, Label, startupWarnings), shell, shell, App.LogFilePath))
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
        Surface.PreferGpuSharedSurface = !StartupOptions.Current.PreferFrameSinkSurface;
        Surface.AutoHide = StartupOptions.Current.AutoHideSurfaces;
        Surface.PresentationPathChanged += OnPresentationPathChanged;
        Surface.Tapped += OnSurfaceTapped;
        Surface.DoubleTapped += OnSurfaceDoubleTapped;
        SecondScreenBox.IsCheckedChanged += OnSecondScreenChanged;
        FullScreenButton.Click += OnFullScreenClick;

        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
        PointerMoved += OnPanelPointerMoved;
        // Keep the controls while a flyout is open, a drag is on or the pointer is on them.
        _chrome = new FullScreenChromeController(
            AvaloniaPlayerDispatcher.Instance,
            ShowChrome,
            () => ViewModel.ActiveFlyout != PlayerFlyout.None || _isDragging || IsPointerOverChrome());
    }

    /// <summary>Gets this player's view model.</summary>
    public PlayerViewModel ViewModel { get; }

    /// <summary>Raised when the user asks for full screen (double-click, button); the window owns the layout.</summary>
    public event EventHandler? FullScreenToggleRequested;

    /// <inheritdoc />
    public event EventHandler? RendererDeviceLost
    {
        add => ViewModel.RendererDeviceLost += value;
        remove => ViewModel.RendererDeviceLost -= value;
    }

    string Label => $"P{_index}";

    /// <summary>Creates the engine and loads the startup media; then, in the first build, opens player 1's second screen when <c>--second-screen=</c> asked.</summary>
    public async Task InitializeAsync()
    {
        await ViewModel.StartAsync();

        // --second-screen=<n> opens player 1's second screen at startup.
        if (StartupOptions.Current.OpensSecondScreenAtStartup(_index, _atStartup) && ViewModel.Player is not null && !_disposed)
            SecondScreenBox.IsChecked = true;
    }

    public async Task DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        _chrome.Dispose();
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        CloseSecondScreen();
        // The surface lets go of the engine before it is disposed.
        Surface.Player = null;
        // Dispose waits for admitted media operations: the view model does it off the UI thread.
        await ViewModel.DisposeAsync();
    }

    public void UpdateStatus(ProcessSample process)
    {
        var size = Surface.AttachedPixelSize;
        var surface = PlayerStatsLine.DescribeSurface(size.Width, size.Height, Surface.PresentationPath, Surface.IsPresentationSuspended);
        ViewModel.UpdateStats(process, PlayerStatsLine.WithSecondScreen(surface, DescribeSecondScreen()));
    }

    /// <summary>Enters or leaves full screen: only the picture, with the controls shown while the pointer moves.</summary>
    public void SetFullScreen(bool fullScreen)
    {
        ViewModel.CloseFlyout();
        Frame.Margin = fullScreen ? default : new Thickness(2);
        Frame.Padding = fullScreen ? default : new Thickness(6);
        Frame.BorderThickness = fullScreen ? default : new Thickness(1);
        if (fullScreen)
            _chrome.Enter();
        else
            _chrome.Exit();
    }

    /// <summary>Gets whether a flyout is open (Escape closes it).</summary>
    public bool HasOpenFlyout => ViewModel.ActiveFlyout != PlayerFlyout.None;

    /// <summary>Escape: closes the open flyout first; returns whether it did.</summary>
    public bool CloseFlyout()
    {
        if (ViewModel.ActiveFlyout == PlayerFlyout.None)
            return false;

        ViewModel.CloseFlyout();
        return true;
    }

    void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PlayerViewModel.SeekPositionSeconds):
            case nameof(PlayerViewModel.DurationSeconds):
                UpdateSeekSlider();
                break;
        }
    }

    void UpdateSeekSlider()
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

    void OnSeekPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_isDragging || !ViewModel.CanSeek)
            return;

        _isDragging = true;
        ViewModel.BeginSeekPreview();
    }

    void OnSeekReleased(object? sender, PointerReleasedEventArgs e) => EndDrag(cancelled: false);

    void OnSeekCaptureLost(object? sender, PointerCaptureLostEventArgs e) => EndDrag(cancelled: true);

    void EndDrag(bool cancelled)
    {
        if (!_isDragging)
            return;

        _isDragging = false;
        if (cancelled)
            ViewModel.CancelSeekPreview();
        else
            ViewModel.EndSeekPreview();
    }

    void OnSeekValueChanged(object? sender, RangeBaseValueChangedEventArgs e)
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
    void OnSurfaceTapped(object? sender, TappedEventArgs e)
    {
        _ = Focus();
        ViewModel.CloseFlyout();
    }

    void OnSurfaceDoubleTapped(object? sender, TappedEventArgs e)
    {
        FullScreenToggleRequested?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    void OnFullScreenClick(object? sender, RoutedEventArgs e) => FullScreenToggleRequested?.Invoke(this, EventArgs.Empty);

    void OnPanelPointerMoved(object? sender, PointerEventArgs e)
    {
        // Window coordinates: the same for every element of this panel.
        if (TopLevel.GetTopLevel(this) is { } topLevel)
        {
            var position = e.GetPosition(topLevel);
            _chrome.PointerMoved(position.X, position.Y);
        }
    }

    bool IsPointerOverChrome() =>
        TopBar.IsPointerOver || SeekRow.IsPointerOver || TransportRow.IsPointerOver || FlyoutHost.IsPointerOver;

    void ShowChrome(bool visible)
    {
        TopBar.IsVisible = visible;
        SeekRow.IsVisible = visible;
        TransportRow.IsVisible = visible;
        FlyoutHost.IsVisible = visible;
        StatusLine.IsVisible = visible;
        StatsText.IsVisible = visible;
        Cursor = visible ? Cursor.Default : new Cursor(StandardCursorType.None);
    }

    void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.Contains(DataFormat.File) && ViewModel.CanOpen ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    async void OnDrop(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        if (e.DataTransfer.TryGetFiles() is not { Length: > 0 } items || items[0].TryGetLocalPath() is not { } path)
            return;

        // LoadMediaAsync reports a failure in the status line and never throws.
        await ViewModel.LoadMediaAsync(path);
    }

    // Raised on the UI thread. The surface also raises an Info diagnostic for every MediaSurface.
    void OnPresentationPathChanged(object? sender, EventArgs e)
    {
        if (Surface.PresentationPath != MediaSurfacePresentationPath.None)
        {
            ViewModel.AddLogEntry(
                MediaDiagnosticLevel.Info,
                "Surface",
                $"{Label} presents via {MediaSurfacePresentationPathText.Describe(Surface.PresentationPath)}{(Surface.PresentationPathReason is { } reason ? $" ({reason})" : string.Empty)}");
        }
    }

    string? DescribeSecondScreen()
    {
        if (_secondScreen?.Surface is not { } surface)
            return null;

        var size = surface.AttachedPixelSize;
        return PlayerStatsLine.DescribeSurface(size.Width, size.Height, surface.PresentationPath, surface.IsPresentationSuspended);
    }

    /// <summary>Opens or closes this player's second screen (Stage 027A slice 5).</summary>
    void OnSecondScreenChanged(object? sender, RoutedEventArgs e)
    {
        if (_isUpdatingSecondScreen)
            return;

        if (SecondScreenBox.IsChecked == true)
            OpenSecondScreen();
        else
            CloseSecondScreen();
    }

    void OpenSecondScreen()
    {
        if (ViewModel.Player is not { } player || _secondScreen is not null || TopLevel.GetTopLevel(this) is not Window owner)
        {
            SetSecondScreenBox(false);
            return;
        }

        (var screen, var fullScreen, var where) = ChooseSecondScreen(owner);
        var window = new SecondaryPresentationWindow(player, $"second-screen-{_index}") { Title = $"Player {_index} - second screen" };
        window.Surface.PreferGpuSharedSurface = !StartupOptions.Current.PreferFrameSinkSurface;
        window.Surface.AutoHide = StartupOptions.Current.AutoHideSurfaces;
        window.Surface.PresentationPathChanged += OnSecondScreenPathChanged;
        window.Closed += OnSecondScreenClosed;
        _secondScreen = window;
        ViewModel.AddLogEntry(MediaDiagnosticLevel.Info, "Surface", $"{Label} second screen opens {where}");
        window.ShowOnScreen(screen, fullScreen, owner);
    }

    /// <summary>The shared rule (<see cref="PlayerScreenChooser"/>) over Avalonia's screens.</summary>
    static (Screen? Screen, bool FullScreen, string Where) ChooseSecondScreen(Window owner)
    {
        var screens = owner.Screens.All;
        var ownerBounds = owner.Screens.ScreenFromWindow(owner)?.Bounds;
        var choice = PlayerScreenChooser.Choose(
            [.. screens.Select(static screen => new PlayerScreen(screen.DisplayName ?? string.Empty, ToBounds(screen.Bounds), screen.IsPrimary))],
            ownerBounds is { } bounds ? ToBounds(bounds) : null,
            StartupOptions.Current.SecondScreen);
        return (choice.ScreenIndex is { } index ? screens[index] : null, choice.FullScreen, choice.Where);
    }

    static PlayerScreenBounds ToBounds(PixelRect rect) => new(rect.X, rect.Y, rect.Width, rect.Height);

    void CloseSecondScreen()
    {
        var window = _secondScreen;
        if (window is null)
            return;

        _secondScreen = null;
        window.Closed -= OnSecondScreenClosed;
        window.Surface.PresentationPathChanged -= OnSecondScreenPathChanged;
        window.Close();
        SetSecondScreenBox(false);
    }

    // The user closed the window.
    void OnSecondScreenClosed(object? sender, EventArgs e)
    {
        if (_secondScreen is not { } window || !ReferenceEquals(sender, window))
            return;

        window.Surface.PresentationPathChanged -= OnSecondScreenPathChanged;
        window.Closed -= OnSecondScreenClosed;
        _secondScreen = null;
        SetSecondScreenBox(false);
    }

    void SetSecondScreenBox(bool isChecked)
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

    void OnSecondScreenPathChanged(object? sender, EventArgs e)
    {
        if (sender is MediaSurface { PresentationPath: not MediaSurfacePresentationPath.None } surface)
        {
            ViewModel.AddLogEntry(
                MediaDiagnosticLevel.Info,
                "Surface",
                $"{Label} second screen presents via {MediaSurfacePresentationPathText.Describe(surface.PresentationPath)}{(surface.PresentationPathReason is { } reason ? $" ({reason})" : string.Empty)}");
        }
    }
}
