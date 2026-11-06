using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using FatzBomb.MediaControl.Player.Hosting;
using FatzBomb.MediaControl.Player.Shared.Hosting;
using FatzBomb.MediaControl.Player.Shared.Presentation;
using FatzBomb.MediaControl.Player.Shared.ViewModels;
using FatzBomb.MediaControl.Player.ViewModels;
using FatzBomb.MediaControl.Wpf;

namespace FatzBomb.MediaControl.Player;

/// <summary>
/// One independent WPF player (Stage 046A slice 3: <see cref="MainWindow"/> shows 1-4 of them,
/// <c>--players=</c>): its own <see cref="MainWindowViewModel"/> over this view's
/// <c>MediaSurface</c>. The code-behind keeps view behaviour only: the seek bar, file drops, the
/// full-screen layout (the window owns the window state), the second screen and the stats line's
/// surface description. The chrome rule, the stats line and the screen choice are shared with the
/// Avalonia and WinForms players (Player.Shared).
/// </summary>
public partial class PlayerView : UserControl, IPlayerSlot
{
    private const double SeekKeyStepSeconds = 5;
    private const double SeekKeyPageSeconds = 10;

    private readonly int _index;
    private readonly bool _atStartup;
    private readonly FullScreenChromeController _chrome;
    private readonly Thickness _standardMediaMargin;
    private readonly Thickness _standardMediaBorderThickness;
    private readonly Thickness _standardMediaPadding;
    private readonly CornerRadius _standardMediaCornerRadius;
    private readonly Thickness _standardBottomChromeMargin;
    private readonly VerticalAlignment _standardBottomChromeVerticalAlignment;
    private readonly Panel _bottomChromeWindowedParent;
    private readonly int _bottomChromeWindowedIndex;
    private Grid? _fullScreenOverlayRoot;
    private SecondaryPresentationWindow? _secondScreen;
    // Composited bitmap writes on the UI thread (Stage 051A, 047A's before numbers).
    private readonly UiThreadCostMeter _surfaceCost = new();
    private readonly UiThreadCostMeter _secondScreenCost = new();
    private bool _isSeekBarDragging;
    private bool _disposed;

    /// <param name="owner">The window that owns this player's dialogs.</param>
    /// <param name="index">The player's number (1-based).</param>
    /// <param name="atStartup">
    /// Whether the players are the window's first ones: then player 1 gets the startup media and its
    /// <c>--second-screen=</c>; players recreated from the toolbar start empty.
    /// </param>
    internal PlayerView(Window owner, int index, bool atStartup)
    {
        _index = index;
        _atStartup = atStartup;
        InitializeComponent();
        // A click on the picture makes this the focused player, so F11 shows this one (ENG-044): the
        // viewport itself cannot take focus, and the native video window passes clicks through.
        Focusable = true;
        FocusVisualStyle = null;

        // Stage 051A: the smoke script reads this line from the debug log.
        MediaSurface.FirstFrameReady += (_, _) => ViewModel.AddLogEntry(
            MediaDiagnosticLevel.Info,
            "Surface",
            $"{ViewModel.Label} first frame ready: {ViewModel.LoadedMediaText} via {MediaSurfacePresentationPathText.Describe(MediaSurface.PresentationPath)}");

        _standardMediaMargin = MediaViewportBorder.Margin;
        _standardMediaBorderThickness = MediaViewportBorder.BorderThickness;
        _standardMediaPadding = MediaViewportBorder.Padding;
        _standardMediaCornerRadius = MediaViewportBorder.CornerRadius;
        _standardBottomChromeMargin = BottomChrome.Margin;
        _standardBottomChromeVerticalAlignment = BottomChrome.VerticalAlignment;
        _bottomChromeWindowedParent = (Panel)BottomChrome.Parent;
        _bottomChromeWindowedIndex = _bottomChromeWindowedParent.Children.IndexOf(BottomChrome);

        ViewModel = new MainWindowViewModel(
            owner,
            index,
            App.StartupOptions.ForBuild(index, atStartup));
        DataContext = ViewModel;
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        // Keep the controls while a flyout is open, a drag is on or the pointer is on them.
        _chrome = new FullScreenChromeController(
            new WpfPlayerDispatcher(),
            ShowChrome,
            () => BottomChrome.IsMouseOver || ViewModel.ActiveFlyout != PlayerFlyout.None || _isSeekBarDragging);
        FlyoutPanel.SecondaryWindowToggleRequested += FlyoutPanel_SecondaryWindowToggleRequested;
        UpdateSeekBarVisual();
        UpdateSecondaryWindowCheckBox();
    }

    /// <summary>Gets this player's view model.</summary>
    internal MainWindowViewModel ViewModel { get; }

    /// <summary>Raised when the user asks for full screen (double-click, button); the window owns the window state.</summary>
    public event EventHandler? FullScreenToggleRequested;

    /// <inheritdoc />
    public event EventHandler? RendererDeviceLost
    {
        add => ViewModel.RendererDeviceLost += value;
        remove => ViewModel.RendererDeviceLost -= value;
    }

    /// <summary>Creates the engine and loads the startup media; then, at startup, opens player 1's second screen when <c>--second-screen=</c> asked.</summary>
    public async Task InitializeAsync()
    {
        await ViewModel.StartAsync();
        if (App.StartupOptions.OpensSecondScreenAtStartup(_index, _atStartup) && ViewModel.Player is not null && !_disposed)
            OpenSecondScreen();
    }

    /// <summary>Lets go of the surfaces and disposes the engine off the UI thread.</summary>
    public async Task DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        _chrome.Dispose();
        FlyoutPanel.SecondaryWindowToggleRequested -= FlyoutPanel_SecondaryWindowToggleRequested;
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        CloseSecondScreen();
        // The surface lets go of the engine before it is disposed.
        MediaSurface.Player = null;
        await ViewModel.DisposeAsync();
    }

    /// <summary>Refreshes the stats line with the window's process reading.</summary>
    public void UpdateStatus(ProcessSample process)
    {
        var surface = DescribeSurface(MediaSurface, _surfaceCost);
        ViewModel.UpdateStats(process, PlayerStatsLine.WithSecondScreen(surface, _secondScreen is { } window ? DescribeSurface(window.Surface, _secondScreenCost) : null));
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

    private static string DescribeSurface(MediaSurface surface, UiThreadCostMeter cost)
    {
        DpiScale dpi = VisualTreeHelper.GetDpi(surface);
        var description = PlayerStatsLine.DescribeSurface(
            (int)Math.Round(surface.ActualWidth * dpi.DpiScaleX),
            (int)Math.Round(surface.ActualHeight * dpi.DpiScaleY),
            surface.PresentationPath,
            surface.IsPresentationSuspended);
        (double, double)? reading = cost.Sample(surface.CompositedUiThreadTime, surface.CompositedFramesPublished);
        return surface.PresentationPath == MediaSurfacePresentationPath.FrameSink
            ? PlayerStatsLine.WithUiThreadCost(description, reading)
            : description;
    }

    private void Panel_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) && ViewModel.CanOpen ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void Panel_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        // LoadMediaAsync reports a failure in the status line and never throws.
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
            await ViewModel.LoadMediaAsync(files[0]);
    }

    private void MediaViewport_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _ = Focus();
        if (e.ClickCount == 1)
            ViewModel.CloseFlyout();

        if (e.ClickCount != 2) return;

        FullScreenToggleRequested?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    private void FullScreenButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.CloseFlyout();
        FullScreenToggleRequested?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    // --- seek bar ---

    private void SeekBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _ = SeekBar.Focus();
        if (!ViewModel.CanSeek) return;

        _isSeekBarDragging = true;
        ViewModel.BeginSeekPreview();
        SetSeekPreviewFromPoint(e.GetPosition(SeekBar).X);
        _ = SeekBar.CaptureMouse();
        e.Handled = true;
    }

    private void SeekBar_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isSeekBarDragging || !ViewModel.CanSeek) return;

        SetSeekPreviewFromPoint(e.GetPosition(SeekBar).X);
        e.Handled = true;
    }

    private void SeekBar_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isSeekBarDragging) return;

        _isSeekBarDragging = false;
        SeekBar.ReleaseMouseCapture();
        ViewModel.EndSeekPreview();
        e.Handled = true;
    }

    private void SeekBar_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (!_isSeekBarDragging) return;

        _isSeekBarDragging = false;
        ViewModel.CancelSeekPreview();
    }

    /// <summary>
    /// Stage 046A slice 3, as the Avalonia and WinForms seek bars: once focused (a click), the arrow
    /// keys step 5 s, Page Up / Page Down 10 s, Home / End go to the start / end. One seek per key.
    /// </summary>
    private void SeekBar_KeyDown(object sender, KeyEventArgs e)
    {
        if (!ViewModel.CanSeek || _isSeekBarDragging)
            return;

        var position = ViewModel.SeekPositionSeconds;
        double? target = e.Key switch
        {
            Key.Left or Key.Down => position - SeekKeyStepSeconds,
            Key.Right or Key.Up => position + SeekKeyStepSeconds,
            Key.PageDown => position - SeekKeyPageSeconds,
            Key.PageUp => position + SeekKeyPageSeconds,
            Key.Home => 0,
            Key.End => ViewModel.DurationSeconds,
            _ => null
        };
        if (target is not { } seconds)
            return;

        ViewModel.SeekTo(Math.Clamp(seconds, 0, ViewModel.DurationSeconds));
        e.Handled = true;
    }

    private void SeekBar_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        SeekCanvas.Width = SeekBar.ActualWidth;
        UpdateSeekBarVisual();
    }

    private void SetSeekPreviewFromPoint(double x)
    {
        var width = SeekBar.ActualWidth;
        if (width <= 0 || ViewModel.DurationSeconds <= 0) return;

        var percent = Math.Clamp(x / width, 0, 1);
        ViewModel.SeekPositionSeconds = ViewModel.DurationSeconds * percent;
        UpdateSeekBarVisual();
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainWindowViewModel.SeekPositionSeconds)
                or nameof(MainWindowViewModel.PositionSeconds)
                or nameof(MainWindowViewModel.DurationSeconds)
                or nameof(MainWindowViewModel.CanSeek):
                UpdateSeekBarVisual();
                break;
            // The second screen presents the way the main surface does (ENG-016): reopen it in the new mode.
            case nameof(MainWindowViewModel.PresentationMode) when _secondScreen is not null:
                CloseSecondScreen();
                OpenSecondScreen();
                break;
            case nameof(MainWindowViewModel.AutoHideSurfaces) when _secondScreen is { } window:
                window.Surface.AutoHide = ViewModel.AutoHideSurfaces;
                break;
        }
    }

    private void UpdateSeekBarVisual()
    {
        var width = SeekBar.ActualWidth;
        if (width <= 0 || ViewModel.DurationSeconds <= 0)
        {
            SeekProgress.Width = 0;
            Canvas.SetLeft(SeekThumb, 0);
            return;
        }

        var position = Math.Clamp(ViewModel.SeekPositionSeconds, 0, ViewModel.DurationSeconds);
        var x = width * (position / ViewModel.DurationSeconds);

        SeekProgress.Width = x;
        Canvas.SetLeft(SeekThumb, Math.Clamp(x - (SeekThumb.Width / 2), 0, Math.Max(0, width - SeekThumb.Width)));
    }

    // --- full screen ---

    private void Panel_MouseMove(object sender, MouseEventArgs e) => ReportPointer(this, e);

    private void ReportPointer(Visual relativeTo, MouseEventArgs e)
    {
        if (PresentationSource.FromVisual(relativeTo) is null)
            return;

        // Screen coordinates: the same for this view and the full-screen overlay window.
        Point screen = relativeTo.PointToScreen(e.GetPosition((IInputElement)relativeTo));
        _chrome.PointerMoved(screen.X, screen.Y);
    }

    /// <summary>
    /// Enters or leaves this view's full-screen layout: the picture fills the view, and the controls
    /// move into the surface's overlay window (native HWND presentation is above the WPF visual
    /// tree) and auto-hide with the cursor.
    /// </summary>
    public void SetFullScreen(bool fullScreen)
    {
        ViewModel.CloseFlyout();
        if (fullScreen)
            EnterFullScreenLayout();
        else
            ExitFullScreenLayout();
    }

    private void EnterFullScreenLayout()
    {
        Grid.SetRow(MediaViewportBorder, 0);
        Grid.SetRowSpan(MediaViewportBorder, 2);
        MediaViewportBorder.Margin = new Thickness(0);
        MediaViewportBorder.BorderThickness = new Thickness(0);
        MediaViewportBorder.Padding = new Thickness(0);
        MediaViewportBorder.CornerRadius = new CornerRadius(0);

        Grid.SetRow(BottomChrome, 0);
        Grid.SetRowSpan(BottomChrome, 1);
        BottomChrome.VerticalAlignment = VerticalAlignment.Bottom;
        BottomChrome.Margin = new Thickness(18, 0, 18, 16);
        Panel.SetZIndex(BottomChrome, 50);

        PlayerControlsPanel.Background = (Brush)FindResource("PanelOverlayBrush");
        AttachBottomChromeToFullScreenOverlay();
        _chrome.Enter();
        QueueMediaSurfaceLayoutRefresh();
    }

    private void ExitFullScreenLayout()
    {
        _chrome.Exit();
        DetachBottomChromeFromFullScreenOverlay();

        Grid.SetRow(MediaViewportBorder, 0);
        Grid.SetRowSpan(MediaViewportBorder, 1);
        MediaViewportBorder.Margin = _standardMediaMargin;
        MediaViewportBorder.BorderThickness = _standardMediaBorderThickness;
        MediaViewportBorder.Padding = _standardMediaPadding;
        MediaViewportBorder.CornerRadius = _standardMediaCornerRadius;

        Grid.SetRow(BottomChrome, 1);
        Grid.SetRowSpan(BottomChrome, 1);
        BottomChrome.VerticalAlignment = _standardBottomChromeVerticalAlignment;
        BottomChrome.Margin = _standardBottomChromeMargin;
        Panel.SetZIndex(BottomChrome, 10);

        PlayerControlsPanel.Background = (Brush)FindResource("PanelBrush");
        QueueMediaSurfaceLayoutRefresh();
    }

    private void AttachBottomChromeToFullScreenOverlay()
    {
        // Native HWND presentation is above the normal WPF visual tree. In full screen, the
        // controls must move into MediaSurface's owned transparent overlay window;
        // otherwise they are still present but hidden behind the native renderer.
        _fullScreenOverlayRoot ??= CreateFullScreenOverlayRoot();
        _fullScreenOverlayRoot.DataContext = DataContext;

        if (BottomChrome.Parent is Panel currentPanel)
            currentPanel.Children.Remove(BottomChrome);

        if (!_fullScreenOverlayRoot.Children.Contains(BottomChrome))
            _ = _fullScreenOverlayRoot.Children.Add(BottomChrome);

        MediaSurface.OverlayContent = _fullScreenOverlayRoot;
        MediaSurface.OverlayHitTestVisible = true;
    }

    private void DetachBottomChromeFromFullScreenOverlay()
    {
        MediaSurface.OverlayContent = null;
        MediaSurface.OverlayHitTestVisible = false;

        if (_fullScreenOverlayRoot?.Children.Contains(BottomChrome) == true)
            _fullScreenOverlayRoot.Children.Remove(BottomChrome);

        if (!_bottomChromeWindowedParent.Children.Contains(BottomChrome))
        {
            var insertIndex = Math.Clamp(_bottomChromeWindowedIndex, 0, _bottomChromeWindowedParent.Children.Count);
            _bottomChromeWindowedParent.Children.Insert(insertIndex, BottomChrome);
        }
    }

    private Grid CreateFullScreenOverlayRoot()
    {
        var root = new Grid
        {
            Background = Brushes.Transparent,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            ClipToBounds = true
        };

        root.MouseMove += (_, e) => ReportPointer(root, e);
        root.MouseLeftButtonDown += FullScreenOverlayRoot_MouseLeftButtonDown;
        root.KeyDown += FullScreenOverlayRoot_KeyDown;
        return root;
    }

    // The overlay is its own window and takes the keyboard once its controls are clicked (the seek
    // bar focuses itself), so MainWindow no longer sees Escape or F11: handle them here the same way.
    private void FullScreenOverlayRoot_KeyDown(object sender, KeyEventArgs e)
    {
        if (!_chrome.IsFullScreen)
            return;

        if (e.Key == Key.Escape && CloseFlyout())
        {
            e.Handled = true;
        }
        else if (e.Key is Key.Escape or Key.F11)
        {
            FullScreenToggleRequested?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        }
    }

    private void FullScreenOverlayRoot_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_chrome.IsFullScreen) return;

        var onControls = IsDescendantOf(e.OriginalSource as DependencyObject, BottomChrome);
        if (e.ClickCount == 1 && !onControls)
            ViewModel.CloseFlyout();

        if (e.ClickCount != 2 || onControls) return;

        FullScreenToggleRequested?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    private static bool IsDescendantOf(DependencyObject? current, DependencyObject ancestor)
    {
        while (current is not null)
        {
            if (ReferenceEquals(current, ancestor)) return true;
            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    private void QueueMediaSurfaceLayoutRefresh()
    {
        _ = Dispatcher.BeginInvoke(new Action(MediaSurface.RefreshPresentationLayout), DispatcherPriority.Render);
        _ = Dispatcher.BeginInvoke(new Action(MediaSurface.RefreshPresentationLayout), DispatcherPriority.ApplicationIdle);
    }

    // Called by the shared chrome controller, only on a change.
    private void ShowChrome(bool visible)
    {
        BottomChrome.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        BottomChrome.Opacity = visible ? 1 : 0;
        BottomChrome.IsHitTestVisible = visible;
        Cursor = visible ? null : Cursors.None;
        if (_fullScreenOverlayRoot is { } root)
            root.Cursor = visible ? null : Cursors.None;
    }

    // --- second screen ---

    private void FlyoutPanel_SecondaryWindowToggleRequested(object? sender, EventArgs e)
    {
        if (_secondScreen is not null)
            CloseSecondScreen();
        else
            OpenSecondScreen();
    }

    private void UpdateSecondaryWindowCheckBox() => FlyoutPanel.IsSecondaryOutputOpen = _secondScreen is not null;

    /// <summary>
    /// Opens this player's second screen (the same session, projector role) where the shared rule
    /// puts it (<see cref="PlayerScreenChooser"/>), in the main surface's presentation mode.
    /// </summary>
    private void OpenSecondScreen()
    {
        if (ViewModel.Player is not { } player || _secondScreen is not null || Window.GetWindow(this) is not { } owner)
        {
            UpdateSecondaryWindowCheckBox();
            return;
        }

        IReadOnlyList<PresentationScreen> screens = PresentationScreen.All;
        PresentationScreen? current = PresentationScreen.FromWindow(owner);
        PlayerScreenChoice choice = PlayerScreenChooser.Choose(
            [.. screens.Select(static screen => new PlayerScreen(screen.DeviceName, ToBounds(screen.Bounds), screen.IsPrimary))],
            current is null ? null : ToBounds(current.Bounds),
            App.StartupOptions.SecondScreen);

        var window = new SecondaryPresentationWindow(
            player,
            $"second-screen-{_index}",
            MediaPresentationSurfaceRole.Projector,
            ViewModel.PresentationMode)
        {
            Title = $"Player {_index} - second screen",
            ShowInTaskbar = false,
        };
        window.Surface.AutoHide = ViewModel.AutoHideSurfaces;
        window.Closed += SecondScreen_Closed;
        _secondScreen = window;
        ViewModel.AddLogEntry(MediaDiagnosticLevel.Info, "Surface", $"{ViewModel.Label} second screen opens {choice.Where}");
        window.ShowOnScreen(choice.ScreenIndex is { } index ? screens[index] : null, choice.FullScreen, owner);
        UpdateSecondaryWindowCheckBox();
    }

    private static PlayerScreenBounds ToBounds(Int32Rect rect) => new(rect.X, rect.Y, rect.Width, rect.Height);

    private void CloseSecondScreen()
    {
        if (_secondScreen is not { } window)
            return;

        _secondScreen = null;
        window.Closed -= SecondScreen_Closed;
        window.Close();
        UpdateSecondaryWindowCheckBox();
    }

    // The user closed the window.
    private void SecondScreen_Closed(object? sender, EventArgs e)
    {
        if (_secondScreen is not { } window || !ReferenceEquals(sender, window))
            return;

        window.Closed -= SecondScreen_Closed;
        _secondScreen = null;
        UpdateSecondaryWindowCheckBox();
    }
}
