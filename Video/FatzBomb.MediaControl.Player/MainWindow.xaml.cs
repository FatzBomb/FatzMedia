using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using FatzBomb.MediaControl.Player.Shared.ViewModels;
using FatzBomb.MediaControl.Player.ViewModels;

namespace FatzBomb.MediaControl.Player;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel;
    private readonly DispatcherTimer _fullScreenChromeTimer;

    private bool _isFullScreen;
    private bool _isSeekBarDragging;
    private WindowState _restoreWindowState;
    private WindowStyle _restoreWindowStyle;
    private ResizeMode _restoreResizeMode;
    private bool _restoreTopmost;

    private readonly Thickness _standardMediaMargin;
    private readonly Thickness _standardMediaBorderThickness;
    private readonly Thickness _standardMediaPadding;
    private readonly CornerRadius _standardMediaCornerRadius;
    private readonly Thickness _standardBottomChromeMargin;
    private readonly VerticalAlignment _standardBottomChromeVerticalAlignment;
    private readonly Panel _bottomChromeWindowedParent;
    private readonly int _bottomChromeWindowedIndex;
    private Grid? _fullScreenOverlayRoot;

    public MainWindow()
    {
        InitializeComponent();

        _standardMediaMargin = MediaViewportBorder.Margin;
        _standardMediaBorderThickness = MediaViewportBorder.BorderThickness;
        _standardMediaPadding = MediaViewportBorder.Padding;
        _standardMediaCornerRadius = MediaViewportBorder.CornerRadius;
        _standardBottomChromeMargin = BottomChrome.Margin;
        _standardBottomChromeVerticalAlignment = BottomChrome.VerticalAlignment;
        _bottomChromeWindowedParent = (Panel)BottomChrome.Parent;
        _bottomChromeWindowedIndex = _bottomChromeWindowedParent.Children.IndexOf(BottomChrome);

        _fullScreenChromeTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2.5)
        };
        _fullScreenChromeTimer.Tick += FullScreenChromeTimer_Tick;

        _viewModel = new MainWindowViewModel(this);
        DataContext = _viewModel;
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
        FlyoutPanel.SecondaryWindowToggleRequested += FlyoutPanel_SecondaryWindowToggleRequested;
        MediaSurface.SecondaryOutputsChanged += MediaSurface_SecondaryOutputsChanged;
        UpdateSeekBarVisual();
        UpdateSecondaryWindowCheckBox();

        // Creates the engine, then loads the startup media (if any).
        _ = _viewModel.StartAsync();
    }

    private void Window_DragEnter(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
            try
            {
                await _viewModel.LoadMediaAsync(files[0]);
            }
            catch (Exception ex)
            {
                _ = MessageBox.Show(this, ex.Message, "Load failed", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        e.Handled = true;
    }

    private void MediaViewport_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 1)
            _viewModel.CloseFlyout();

        if (e.ClickCount != 2) return;

        ToggleFullScreen();
        e.Handled = true;
    }

    private void FullScreenButton_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.CloseFlyout();
        ToggleFullScreen();
        e.Handled = true;
    }

    private void FlyoutPanel_SecondaryWindowToggleRequested(object? sender, EventArgs e)
    {
        if (MediaSurface.IsSecondaryOutputOpen())
            _ = MediaSurface.CloseSecondaryOutput();
        else
            MediaSurface.ShowSecondaryOutput();

        UpdateSecondaryWindowCheckBox();
    }

    private void MediaSurface_SecondaryOutputsChanged(object? sender, EventArgs e) => UpdateSecondaryWindowCheckBox();

    private void UpdateSecondaryWindowCheckBox()
    {
        if (FlyoutPanel == null)
            return;

        FlyoutPanel.IsSecondaryOutputOpen = MediaSurface.IsSecondaryOutputOpen();
    }

    private void SeekBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_viewModel.CanSeek) return;

        _isSeekBarDragging = true;
        _viewModel.BeginSeekPreview();
        SetSeekPreviewFromPoint(e.GetPosition(SeekBar).X);
        _ = SeekBar.CaptureMouse();
        e.Handled = true;
    }

    private void SeekBar_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isSeekBarDragging || !_viewModel.CanSeek) return;

        SetSeekPreviewFromPoint(e.GetPosition(SeekBar).X);
        e.Handled = true;
    }

    private void SeekBar_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isSeekBarDragging) return;

        _isSeekBarDragging = false;
        SeekBar.ReleaseMouseCapture();

        _viewModel.EndSeekPreview();

        e.Handled = true;
    }

    private void SeekBar_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (!_isSeekBarDragging) return;

        _isSeekBarDragging = false;
        _viewModel.CancelSeekPreview();
    }

    private void SeekBar_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        SeekCanvas.Width = SeekBar.ActualWidth;
        UpdateSeekBarVisual();
    }

    private void SetSeekPreviewFromPoint(double x)
    {
        var width = SeekBar.ActualWidth;
        if (width <= 0 || _viewModel.DurationSeconds <= 0) return;

        var percent = Math.Clamp(x / width, 0, 1);
        _viewModel.SeekPositionSeconds = _viewModel.DurationSeconds * percent;
        UpdateSeekBarVisual();
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainWindowViewModel.SeekPositionSeconds)
            or nameof(MainWindowViewModel.PositionSeconds)
            or nameof(MainWindowViewModel.DurationSeconds)
            or nameof(MainWindowViewModel.CanSeek))
        {
            UpdateSeekBarVisual();
        }
    }

    private void UpdateSeekBarVisual()
    {
        var width = SeekBar.ActualWidth;
        if (width <= 0 || _viewModel.DurationSeconds <= 0)
        {
            SeekProgress.Width = 0;
            Canvas.SetLeft(SeekThumb, 0);
            return;
        }

        var position = Math.Clamp(_viewModel.SeekPositionSeconds, 0, _viewModel.DurationSeconds);
        var percent = position / _viewModel.DurationSeconds;
        var x = width * percent;

        SeekProgress.Width = x;
        Canvas.SetLeft(SeekThumb, Math.Clamp(x - (SeekThumb.Width / 2), 0, Math.Max(0, width - SeekThumb.Width)));
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (_viewModel.ActiveFlyout != PlayerFlyout.None)
            {
                _viewModel.CloseFlyout();
                e.Handled = true;
                return;
            }

            if (_isFullScreen)
            {
                ExitFullScreen();
                e.Handled = true;
                return;
            }
        }

        if (e.Key == Key.F11)
        {
            ToggleFullScreen();
            e.Handled = true;
        }
    }

    private void Window_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isFullScreen) return;
        ShowFullScreenChrome(restartTimer: true);
    }

    private void ToggleFullScreen()
    {
        _viewModel.CloseFlyout();

        if (_isFullScreen)
            ExitFullScreen();
        else
            EnterFullScreen();
    }

    private void EnterFullScreen()
    {
        if (_isFullScreen) return;

        _restoreWindowState = WindowState == WindowState.Minimized ? WindowState.Normal : WindowState;
        _restoreWindowStyle = WindowStyle;
        _restoreResizeMode = ResizeMode;
        _restoreTopmost = Topmost;

        _isFullScreen = true;

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        WindowState = WindowState.Maximized;

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
        ShowFullScreenChrome(restartTimer: true);
        QueueMediaSurfaceLayoutRefresh();
    }

    private void ExitFullScreen()
    {
        if (!_isFullScreen) return;

        _fullScreenChromeTimer.Stop();
        _isFullScreen = false;

        Cursor = Cursors.Arrow;
        BottomChrome.Visibility = Visibility.Visible;
        BottomChrome.Opacity = 1;
        BottomChrome.IsHitTestVisible = true;
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

        WindowState = WindowState.Normal;
        WindowStyle = _restoreWindowStyle;
        ResizeMode = _restoreResizeMode;
        Topmost = _restoreTopmost;
        WindowState = _restoreWindowState;
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
        {
            currentPanel.Children.Remove(BottomChrome);
        }

        if (!_fullScreenOverlayRoot.Children.Contains(BottomChrome))
        {
            _ = _fullScreenOverlayRoot.Children.Add(BottomChrome);
        }

        MediaSurface.OverlayContent = _fullScreenOverlayRoot;
        MediaSurface.OverlayHitTestVisible = true;
    }

    private void DetachBottomChromeFromFullScreenOverlay()
    {
        MediaSurface.OverlayContent = null;
        MediaSurface.OverlayHitTestVisible = false;

        if (_fullScreenOverlayRoot?.Children.Contains(BottomChrome) == true)
        {
            _fullScreenOverlayRoot.Children.Remove(BottomChrome);
        }

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

        root.MouseMove += FullScreenOverlayRoot_MouseMove;
        root.MouseLeftButtonDown += FullScreenOverlayRoot_MouseLeftButtonDown;
        return root;
    }

    private void FullScreenOverlayRoot_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isFullScreen) return;
        ShowFullScreenChrome(restartTimer: true);
    }

    private void FullScreenOverlayRoot_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_isFullScreen) return;

        if (e.ClickCount == 1 && !IsDescendantOf(e.OriginalSource as DependencyObject, BottomChrome))
            _viewModel.CloseFlyout();

        if (e.ClickCount != 2) return;
        if (IsDescendantOf(e.OriginalSource as DependencyObject, BottomChrome)) return;

        ToggleFullScreen();
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

    private void ShowFullScreenChrome(bool restartTimer)
    {
        BottomChrome.Visibility = Visibility.Visible;
        BottomChrome.Opacity = 1;
        BottomChrome.IsHitTestVisible = true;
        Cursor = Cursors.Arrow;

        if (!restartTimer) return;

        _fullScreenChromeTimer.Stop();
        _fullScreenChromeTimer.Start();
    }

    private void FullScreenChromeTimer_Tick(object? sender, EventArgs e)
    {
        if (!_isFullScreen) return;
        if (BottomChrome.IsMouseOver) return;

        _fullScreenChromeTimer.Stop();
        BottomChrome.Opacity = 0;
        BottomChrome.Visibility = Visibility.Collapsed;
        BottomChrome.IsHitTestVisible = false;
        Cursor = Cursors.None;
    }

    protected override void OnClosed(EventArgs e)
    {
        _fullScreenChromeTimer.Stop();
        FlyoutPanel.SecondaryWindowToggleRequested -= FlyoutPanel_SecondaryWindowToggleRequested;
        MediaSurface.SecondaryOutputsChanged -= MediaSurface_SecondaryOutputsChanged;
        _ = MediaSurface.CloseSecondaryOutput();
        _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
        _viewModel.Dispose();
        base.OnClosed(e);
    }
}
