using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace FatzBombMedia.MediaControl.Wpf;

public sealed class MediaSurface : Grid
{
    public const string DefaultSecondarySurfaceId = "secondary";

    private readonly NativeHwndSurfaceHost _surfaceHost;
    private IMediaControlSession? _attachedPrimarySession;
    private bool _primaryAttached;
    private bool _layoutRefreshQueued;
    private SecondaryPresentationWindow? _secondaryWindow;
    private OverlayWindow? _overlayWindow;

    public MediaSurface()
    {
        Background = Brushes.Black;
        ClipToBounds = true;
        _surfaceHost = new NativeHwndSurfaceHost();
        Children.Add(_surfaceHost);

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += OnSizeChanged;
        _surfaceHost.NativeHandleChanged += OnNativeSurfaceChanged;
        _surfaceHost.NativeBoundsChanged += OnNativeSurfaceChanged;
    }

    public static readonly DependencyProperty PlayerProperty = DependencyProperty.Register(
        nameof(Player),
        typeof(IMediaControlSession),
        typeof(MediaSurface),
        new PropertyMetadata(null, OnPlayerChanged));

    public IMediaControlSession? Player
    {
        get => (IMediaControlSession?)GetValue(PlayerProperty);
        set => SetValue(PlayerProperty, value);
    }

    public static readonly DependencyProperty VideoDisplayModeProperty = DependencyProperty.Register(
        nameof(VideoDisplayMode),
        typeof(VideoDisplayMode),
        typeof(MediaSurface),
        new PropertyMetadata(VideoDisplayMode.Zoom, OnVideoDisplayModeChanged));

    public VideoDisplayMode VideoDisplayMode
    {
        get => (VideoDisplayMode)GetValue(VideoDisplayModeProperty);
        set => SetValue(VideoDisplayModeProperty, value);
    }

    public static readonly DependencyProperty BitmapScalingModeProperty = DependencyProperty.Register(
        nameof(BitmapScalingMode),
        typeof(BitmapScalingMode),
        typeof(MediaSurface),
        new PropertyMetadata(BitmapScalingMode.HighQuality));

    public BitmapScalingMode BitmapScalingMode
    {
        get => (BitmapScalingMode)GetValue(BitmapScalingModeProperty);
        set => SetValue(BitmapScalingModeProperty, value);
    }

    public static readonly DependencyProperty PresentationModeProperty = DependencyProperty.Register(
        nameof(PresentationMode),
        typeof(MediaPresentationMode),
        typeof(MediaSurface),
        new PropertyMetadata(MediaPresentationMode.NativeHwndOverlay, OnPresentationModeChanged));

    public MediaPresentationMode PresentationMode
    {
        get => (MediaPresentationMode)GetValue(PresentationModeProperty);
        set => SetValue(PresentationModeProperty, value);
    }

    public static readonly DependencyProperty OverlayContentProperty = DependencyProperty.Register(
        nameof(OverlayContent),
        typeof(object),
        typeof(MediaSurface),
        new PropertyMetadata(null, OnOverlayContentChanged));

    public object? OverlayContent
    {
        get => GetValue(OverlayContentProperty);
        set => SetValue(OverlayContentProperty, value);
    }

    public static readonly DependencyProperty OverlayHitTestVisibleProperty = DependencyProperty.Register(
        nameof(OverlayHitTestVisible),
        typeof(bool),
        typeof(MediaSurface),
        new PropertyMetadata(false, OnOverlayHitTestVisibleChanged));

    public bool OverlayHitTestVisible
    {
        get => (bool)GetValue(OverlayHitTestVisibleProperty);
        set => SetValue(OverlayHitTestVisibleProperty, value);
    }

    public event EventHandler? SecondaryOutputsChanged;

    public bool ShowSecondaryOutput()
    {
        if (Player == null)
        {
            return false;
        }

        if (_secondaryWindow is { IsVisible: true })
        {
            _secondaryWindow.Activate();
            return true;
        }

        _secondaryWindow = new SecondaryPresentationWindow(Player, DefaultSecondarySurfaceId)
        {
            Owner = Window.GetWindow(this),
            Title = "FatzBombMedia Secondary Output"
        };
        _secondaryWindow.Closed += OnSecondaryWindowClosed;
        _secondaryWindow.Show();
        SecondaryOutputsChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public bool CloseSecondaryOutput()
    {
        if (_secondaryWindow == null)
        {
            return false;
        }

        _secondaryWindow.Closed -= OnSecondaryWindowClosed;
        SecondaryPresentationWindow window = _secondaryWindow;
        _secondaryWindow = null;
        window.Close();
        SecondaryOutputsChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public bool IsSecondaryOutputOpen() => _secondaryWindow is { IsVisible: true };

    public void RefreshPresentationLayout()
    {
        if (_layoutRefreshQueued)
        {
            return;
        }

        _layoutRefreshQueued = true;
        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            _layoutRefreshQueued = false;
            AttachOrResizePrimarySurface();
            UpdateOverlayWindow();
        }));
    }

    private static void OnPlayerChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var surface = (MediaSurface)d;
        surface.DetachPrimarySurface(e.OldValue as IMediaControlSession);
        if (e.NewValue is IMediaControlSession session)
        {
            session.VideoDisplayMode = surface.VideoDisplayMode;
        }

        surface.AttachOrResizePrimarySurface();
    }

    private static void OnVideoDisplayModeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var surface = (MediaSurface)d;
        if (surface.Player is IMediaControlSession session && e.NewValue is VideoDisplayMode mode)
        {
            session.VideoDisplayMode = mode;
        }
    }

    private static void OnPresentationModeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var surface = (MediaSurface)d;
        surface.UpdateOverlayWindow();
        surface.AttachOrResizePrimarySurface();
    }

    private static void OnOverlayContentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var surface = (MediaSurface)d;
        surface.UpdateOverlayWindow();
    }

    private static void OnOverlayHitTestVisibleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var surface = (MediaSurface)d;
        surface.UpdateOverlayWindow();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        AttachOrResizePrimarySurface();
        UpdateOverlayWindow();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        DetachPrimarySurface(_attachedPrimarySession);
        CloseOverlayWindow();
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e) => RefreshPresentationLayout();

    private void OnNativeSurfaceChanged(object? sender, EventArgs e) => RefreshPresentationLayout();

    private void OnSecondaryWindowClosed(object? sender, EventArgs e)
    {
        if (_secondaryWindow != null)
        {
            _secondaryWindow.Closed -= OnSecondaryWindowClosed;
            _secondaryWindow = null;
            SecondaryOutputsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void AttachOrResizePrimarySurface()
    {
        IMediaControlSession? session = Player;
        if (!IsLoaded || session == null || _surfaceHost.WindowHandle == IntPtr.Zero)
        {
            return;
        }

        var pixelWidth = Math.Max(1, _surfaceHost.NativeClientWidth);
        var pixelHeight = Math.Max(1, _surfaceHost.NativeClientHeight);
        DpiScale dpi = VisualTreeHelper.GetDpi(this);

        if (_primaryAttached && ReferenceEquals(session, _attachedPrimarySession))
        {
            _ = session.ResizePresentationSurface(MediaPresentationSurfaceDescriptor.PrimarySurfaceId, pixelWidth, pixelHeight);
            return;
        }

        DetachPrimarySurface(_attachedPrimarySession);
        _primaryAttached = session.AttachPresentationSurface(MediaPresentationSurfaceDescriptor.NativeHwnd(
            MediaPresentationSurfaceDescriptor.PrimarySurfaceId,
            MediaPresentationSurfaceRole.Primary,
            _surfaceHost.WindowHandle,
            pixelWidth,
            pixelHeight,
            dpi.DpiScaleX,
            dpi.DpiScaleY));
        _attachedPrimarySession = _primaryAttached ? session : null;
    }

    private void DetachPrimarySurface(IMediaControlSession? session)
    {
        if (!_primaryAttached || session == null)
        {
            return;
        }

        _ = session.DetachPresentationSurface(MediaPresentationSurfaceDescriptor.PrimarySurfaceId);
        _primaryAttached = false;
        _attachedPrimarySession = null;
    }

    private void UpdateOverlayWindow()
    {
        if (!IsLoaded || OverlayContent == null || PresentationMode != MediaPresentationMode.NativeHwndOverlay)
        {
            CloseOverlayWindow();
            return;
        }

        _overlayWindow ??= CreateOverlayWindow();
        _overlayWindow.SetContent(OverlayContent);
        _overlayWindow.SetHitTestVisible(OverlayHitTestVisible);
        _overlayWindow.UpdateBounds(this);

        if (!_overlayWindow.IsVisible)
        {
            _overlayWindow.Show();
        }
    }

    private OverlayWindow CreateOverlayWindow()
    {
        var window = new OverlayWindow
        {
            Owner = Window.GetWindow(this)
        };
        window.Closed += (_, _) => _overlayWindow = null;
        return window;
    }

    private void CloseOverlayWindow()
    {
        if (_overlayWindow == null)
        {
            return;
        }

        _overlayWindow.SetContent(null);
        OverlayWindow window = _overlayWindow;
        _overlayWindow = null;
        window.Close();
    }

    private sealed class OverlayWindow : Window
    {
        private readonly ContentPresenter _contentPresenter = new();
        private bool _hitTestVisible;

        public OverlayWindow()
        {
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Content = _contentPresenter;
            IsHitTestVisible = false;
            Focusable = false;
        }

        public void SetContent(object? content) => _contentPresenter.Content = content;

        public void SetHitTestVisible(bool value)
        {
            _hitTestVisible = value;
            IsHitTestVisible = value;
            Focusable = value;
            ApplyHitTestStyle();
        }

        public void UpdateBounds(FrameworkElement owner)
        {
            if (!owner.IsLoaded || owner.ActualWidth <= 0 || owner.ActualHeight <= 0)
            {
                return;
            }

            Point topLeft = owner.PointToScreen(new Point(0, 0));
            PresentationSource? source = PresentationSource.FromVisual(owner);
            if (source?.CompositionTarget != null)
            {
                topLeft = source.CompositionTarget.TransformFromDevice.Transform(topLeft);
            }

            Left = topLeft.X;
            Top = topLeft.Y;
            Width = owner.ActualWidth;
            Height = owner.ActualHeight;
            ApplyHitTestStyle();
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            ApplyHitTestStyle();
        }

        private void ApplyHitTestStyle()
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero)
            {
                return;
            }

            int exStyle = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
            int updated = _hitTestVisible
                ? exStyle & ~NativeMethods.WS_EX_TRANSPARENT
                : exStyle | NativeMethods.WS_EX_TRANSPARENT;
            if (updated != exStyle)
            {
                _ = NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE, updated);
            }
        }
    }

    private sealed class NativeHwndSurfaceHost : HwndHost
    {
        private const int WS_CHILD = 0x40000000;
        private const int WS_VISIBLE = 0x10000000;
        private const int WS_CLIPSIBLINGS = 0x04000000;
        private const int WS_CLIPCHILDREN = 0x02000000;
        private const int WM_PAINT = 0x000F;
        private const int WM_ERASEBKGND = 0x0014;
        private const int WM_SIZE = 0x0005;
        private const int WM_WINDOWPOSCHANGED = 0x0047;

        private int _lastHostWidth;
        private int _lastHostHeight;
        private bool _boundsUpdateQueued;

        public IntPtr WindowHandle { get; private set; }

        public int NativeClientWidth => GetCurrentClientSize().width;

        public int NativeClientHeight => GetCurrentClientSize().height;

        public event EventHandler? NativeHandleChanged;

        public event EventHandler? NativeBoundsChanged;

        protected override HandleRef BuildWindowCore(HandleRef hwndParent)
        {
            (int hostWidth, int hostHeight) = GetCurrentClientSize();
            WindowHandle = NativeMethods.CreateWindowEx(
                0,
                "static",
                string.Empty,
                WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS | WS_CLIPCHILDREN,
                0,
                0,
                hostWidth,
                hostHeight,
                hwndParent.Handle,
                IntPtr.Zero,
                IntPtr.Zero,
                IntPtr.Zero);

            NativeHandleChanged?.Invoke(this, EventArgs.Empty);
            QueueBoundsChanged();
            return new HandleRef(this, WindowHandle);
        }

        protected override void DestroyWindowCore(HandleRef hwnd)
        {
            if (hwnd.Handle != IntPtr.Zero)
            {
                _ = NativeMethods.DestroyWindow(hwnd.Handle);
            }

            WindowHandle = IntPtr.Zero;
            NativeHandleChanged?.Invoke(this, EventArgs.Empty);
        }

        protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
        {
            base.OnRenderSizeChanged(sizeInfo);
            QueueBoundsChanged();
        }

        protected override IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_ERASEBKGND)
            {
                if (wParam != IntPtr.Zero)
                {
                    FillNativeBackground(hwnd, wParam);
                }

                handled = true;
                return new IntPtr(1);
            }

            if (msg == WM_PAINT)
            {
                if (NativeMethods.BeginPaint(hwnd, out NativeMethods.PAINTSTRUCT paint) != IntPtr.Zero)
                {
                    FillNativeBackground(hwnd, paint.Hdc);
                    _ = NativeMethods.EndPaint(hwnd, ref paint);
                }

                handled = true;
                return IntPtr.Zero;
            }

            if (msg is WM_SIZE or WM_WINDOWPOSCHANGED)
            {
                QueueBoundsChanged();
            }

            handled = false;
            return IntPtr.Zero;
        }

        private void QueueBoundsChanged()
        {
            if (WindowHandle == IntPtr.Zero || _boundsUpdateQueued)
            {
                return;
            }

            _boundsUpdateQueued = true;
            _ = Dispatcher.BeginInvoke(new Action(() =>
            {
                _boundsUpdateQueued = false;
                (int width, int height) = GetCurrentClientSize();
                if (width == _lastHostWidth && height == _lastHostHeight)
                {
                    return;
                }

                _lastHostWidth = width;
                _lastHostHeight = height;
                NativeBoundsChanged?.Invoke(this, EventArgs.Empty);
            }));
        }

        private (int width, int height) GetCurrentClientSize()
        {
            if (WindowHandle != IntPtr.Zero && NativeMethods.GetClientRect(WindowHandle, out NativeMethods.RECT rect))
            {
                return (
                    Math.Max(1, rect.Right - rect.Left),
                    Math.Max(1, rect.Bottom - rect.Top));
            }

            return (
                Math.Max(1, (int)Math.Round(ActualWidth)),
                Math.Max(1, (int)Math.Round(ActualHeight)));
        }

        private static void FillNativeBackground(IntPtr hwnd, IntPtr hdc)
        {
            if (hwnd == IntPtr.Zero || hdc == IntPtr.Zero || !NativeMethods.GetClientRect(hwnd, out NativeMethods.RECT rect))
            {
                return;
            }

            IntPtr brush = NativeMethods.CreateSolidBrush(0);
            if (brush == IntPtr.Zero)
            {
                return;
            }

            try
            {
                _ = NativeMethods.FillRect(hdc, ref rect, brush);
            }
            finally
            {
                _ = NativeMethods.DeleteObject(brush);
            }
        }
    }
}
