using Microsoft.Win32;
using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using FatzBombMedia.Player.ViewModels;

namespace FatzBombMedia.Player;

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

    public MainWindow()
    {
        InitializeComponent();

        _standardMediaMargin = MediaViewportBorder.Margin;
        _standardMediaBorderThickness = MediaViewportBorder.BorderThickness;
        _standardMediaPadding = MediaViewportBorder.Padding;
        _standardMediaCornerRadius = MediaViewportBorder.CornerRadius;
        _standardBottomChromeMargin = BottomChrome.Margin;
        _standardBottomChromeVerticalAlignment = BottomChrome.VerticalAlignment;

        _fullScreenChromeTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2.5)
        };
        _fullScreenChromeTimer.Tick += FullScreenChromeTimer_Tick;

        _viewModel = new MainWindowViewModel();
        DataContext = _viewModel;
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
        UpdateSeekBarVisual();

    }

    private async void OpenButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Open media file",
            Filter = _viewModel.Player?.OpenFileDialogFilter ?? "All files|*.*"
        };

        if (dialog.ShowDialog(this) == true)
        {
            try
            {
                await _viewModel.LoadMediaAsync(dialog.FileName);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Load failed", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private void MediaViewport_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2) return;

        ToggleFullScreen();
        e.Handled = true;
    }

    private void FullScreenButton_Click(object sender, RoutedEventArgs e)
    {
        ToggleFullScreen();
        e.Handled = true;
    }

    private void SeekBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_viewModel.CanSeek) return;

        _isSeekBarDragging = true;
        _viewModel.BeginSeekPreview();
        SetSeekPreviewFromPoint(e.GetPosition(SeekBar).X);
        SeekBar.CaptureMouse();
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
        if (e.Key == Key.Escape && _isFullScreen)
        {
            ExitFullScreen();
            e.Handled = true;
            return;
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
        Grid.SetRowSpan(BottomChrome, 2);
        BottomChrome.VerticalAlignment = VerticalAlignment.Bottom;
        BottomChrome.Margin = new Thickness(18, 0, 18, 16);
        Panel.SetZIndex(BottomChrome, 50);

        PlayerControlsPanel.Background = (System.Windows.Media.Brush)FindResource("PanelOverlayBrush");
        ShowFullScreenChrome(restartTimer: true);
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

        PlayerControlsPanel.Background = (System.Windows.Media.Brush)FindResource("PanelBrush");

        WindowState = WindowState.Normal;
        WindowStyle = _restoreWindowStyle;
        ResizeMode = _restoreResizeMode;
        Topmost = _restoreTopmost;
        WindowState = _restoreWindowState;
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
        _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
        _viewModel.Dispose();
        base.OnClosed(e);
    }
}
