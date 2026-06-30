using FatzBombMedia.MediaControl.Core.Abstractions;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace FatzBombMedia.MediaControl.Wpf;

public sealed class MediaSurface : Grid
{
    public const string DefaultSecondarySurfaceId = "secondary";
    private const int WM_ENTERSIZEMOVE = 0x0231;
    private const int WM_EXITSIZEMOVE = 0x0232;

    private readonly NativeHwndSurfaceHost _surfaceHost;
    private IMediaControlSession? _attachedPrimarySession;
    private IMediaControlSession? _eventSession;
    private Window? _ownerWindow;
    private HwndSource? _ownerHwndSource;
    private bool _primaryAttached;
    private bool _layoutRefreshQueued;
    private bool _primarySurfaceLiveResize;
    private volatile bool _showPrimarySurfaceAfterPresentation;
    private SecondaryPresentationWindow? _secondaryWindow;
    private OverlayWindow? _overlayWindow;

    public MediaSurface()
    {
        Background = Brushes.Black;
        ClipToBounds = true;
        _surfaceHost = new NativeHwndSurfaceHost();
        _ = Children.Add(_surfaceHost);

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
            return false;

        if (_secondaryWindow is { IsVisible: true })
        {
            _ = _secondaryWindow.Activate();
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
            return false;

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
            return;

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
        surface.DetachSessionEvents(e.OldValue as IMediaControlSession);
        surface.DetachPrimarySurface(e.OldValue as IMediaControlSession);
        if (e.NewValue is IMediaControlSession session)
        {
            surface.AttachSessionEvents(session);
            session.VideoDisplayMode = surface.VideoDisplayMode;
        }

        surface.AttachOrResizePrimarySurface();
        surface.UpdateNativeSurfaceVisibility();
    }

    private static void OnVideoDisplayModeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var surface = (MediaSurface)d;
        if (surface.Player is IMediaControlSession session && e.NewValue is VideoDisplayMode mode)
            session.VideoDisplayMode = mode;
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
        AttachOwnerWindowHook();
        AttachSessionEvents(Player);
        AttachOrResizePrimarySurface();
        UpdateNativeSurfaceVisibility();
        UpdateOverlayWindow();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        DetachOwnerWindowHook();
        DetachSessionEvents(Player);
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
            return;

        var pixelWidth = Math.Max(1, _surfaceHost.NativeClientWidth);
        var pixelHeight = Math.Max(1, _surfaceHost.NativeClientHeight);
        DpiScale dpi = VisualTreeHelper.GetDpi(this);

        if (_primaryAttached && ReferenceEquals(session, _attachedPrimarySession))
        {
            _ = session.ResizePresentationSurface(MediaPresentationSurfaceDescriptor.PrimarySurfaceId, pixelWidth, pixelHeight);
            ApplyPrimarySurfaceLiveResize(session);
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
        if (_primaryAttached)
            ApplyPrimarySurfaceLiveResize(session);

        UpdateNativeSurfaceVisibility();
    }

    private void DetachPrimarySurface(IMediaControlSession? session)
    {
        if (!_primaryAttached || session == null)
            return;

        if (_primarySurfaceLiveResize)
            _ = session.SetPresentationSurfaceLiveResize(MediaPresentationSurfaceDescriptor.PrimarySurfaceId, false);

        _ = session.DetachPresentationSurface(MediaPresentationSurfaceDescriptor.PrimarySurfaceId);
        _primaryAttached = false;
        _attachedPrimarySession = null;
        UpdateNativeSurfaceVisibility();
    }

    private void AttachSessionEvents(IMediaControlSession? session)
    {
        if (session == null || ReferenceEquals(session, _eventSession))
            return;

        DetachSessionEvents(_eventSession);
        _eventSession = session;
        session.MediaStarted += OnSessionMediaStarted;
        session.MediaStopped += OnSessionMediaStopped;
        session.MediaEnded += OnSessionMediaEnded;
        session.PresentationFramePresented += OnSessionPresentationFramePresented;
    }

    private void DetachSessionEvents(IMediaControlSession? session)
    {
        if (session == null || !ReferenceEquals(session, _eventSession))
            return;

        session.MediaStarted -= OnSessionMediaStarted;
        session.MediaStopped -= OnSessionMediaStopped;
        session.MediaEnded -= OnSessionMediaEnded;
        session.PresentationFramePresented -= OnSessionPresentationFramePresented;
        _showPrimarySurfaceAfterPresentation = false;
        _eventSession = null;
    }

    private void OnSessionMediaStarted(object? sender, EventArgs e)
    {
        _showPrimarySurfaceAfterPresentation = true;
        _ = Dispatcher.BeginInvoke(UpdateNativeSurfaceVisibility);
    }

    private void OnSessionMediaStopped(object? sender, EventArgs e)
    {
        _showPrimarySurfaceAfterPresentation = false;
        _ = Dispatcher.BeginInvoke(UpdateNativeSurfaceVisibility);
    }

    private void OnSessionMediaEnded(object? sender, EventArgs e)
    {
        _showPrimarySurfaceAfterPresentation = false;
        _ = Dispatcher.BeginInvoke(UpdateNativeSurfaceVisibility);
    }

    private void OnSessionPresentationFramePresented(object? sender, EventArgs e)
    {
        if (!_showPrimarySurfaceAfterPresentation)
            return;

        _showPrimarySurfaceAfterPresentation = false;
        _ = Dispatcher.BeginInvoke(UpdateNativeSurfaceVisibility);
    }

    private void UpdateNativeSurfaceVisibility()
    {
        IMediaControlSession? session = Player;
        var shouldShow = _primaryAttached
            && session is { State: MediaPlayerState.Playing or MediaPlayerState.Paused }
            && !_showPrimarySurfaceAfterPresentation;

        if (shouldShow)
        {
            if (_surfaceHost.Visibility != Visibility.Visible)
                _surfaceHost.Visibility = Visibility.Visible;

            _surfaceHost.SetNativeVisible(true);
            return;
        }

        _surfaceHost.SetNativeVisible(false);
        if (_surfaceHost.Visibility != Visibility.Hidden)
            _surfaceHost.Visibility = Visibility.Hidden;
    }

    private void AttachOwnerWindowHook()
    {
        Window? window = Window.GetWindow(this);
        if (window == null)
            return;

        if (!ReferenceEquals(window, _ownerWindow))
        {
            DetachOwnerWindowHook();
            _ownerWindow = window;
            _ownerWindow.SourceInitialized += OnOwnerWindowSourceInitialized;
            _ownerWindow.Closed += OnOwnerWindowClosed;
        }

        AttachOwnerHwndSource(window);
    }

    private void AttachOwnerHwndSource(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
            return;

        HwndSource? source = HwndSource.FromHwnd(hwnd);
        if (source == null || ReferenceEquals(source, _ownerHwndSource))
            return;

        _ownerHwndSource?.RemoveHook(OnOwnerWindowMessage);
        _ownerHwndSource = source;
        _ownerHwndSource.AddHook(OnOwnerWindowMessage);
    }

    private void DetachOwnerWindowHook()
    {
        SetPrimarySurfaceLiveResize(false);

        if (_ownerHwndSource != null)
        {
            _ownerHwndSource.RemoveHook(OnOwnerWindowMessage);
            _ownerHwndSource = null;
        }

        if (_ownerWindow != null)
        {
            _ownerWindow.SourceInitialized -= OnOwnerWindowSourceInitialized;
            _ownerWindow.Closed -= OnOwnerWindowClosed;
            _ownerWindow = null;
        }
    }

    private void OnOwnerWindowSourceInitialized(object? sender, EventArgs e)
    {
        if (sender is Window window)
            AttachOwnerHwndSource(window);
    }

    private void OnOwnerWindowClosed(object? sender, EventArgs e) => DetachOwnerWindowHook();

    private IntPtr OnOwnerWindowMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_ENTERSIZEMOVE)
        {
            SetPrimarySurfaceLiveResize(true);
        }
        else if (msg == WM_EXITSIZEMOVE)
        {
            SetPrimarySurfaceLiveResize(false);
            RefreshPresentationLayout();
        }

        return IntPtr.Zero;
    }

    private void SetPrimarySurfaceLiveResize(bool isResizing)
    {
        if (_primarySurfaceLiveResize == isResizing)
            return;

        _primarySurfaceLiveResize = isResizing;
        if (_primaryAttached && _attachedPrimarySession != null)
        {
            _ = _attachedPrimarySession.SetPresentationSurfaceLiveResize(
                MediaPresentationSurfaceDescriptor.PrimarySurfaceId,
                isResizing);
        }
    }

    private void ApplyPrimarySurfaceLiveResize(IMediaControlSession session)
    {
        if (_primarySurfaceLiveResize)
        {
            _ = session.SetPresentationSurfaceLiveResize(
                MediaPresentationSurfaceDescriptor.PrimarySurfaceId,
                true);
        }
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
            _overlayWindow.Show();
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
            return;

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
                return;

            Point topLeft = owner.PointToScreen(new Point(0, 0));
            var source = PresentationSource.FromVisual(owner);
            if (source?.CompositionTarget != null)
                topLeft = source.CompositionTarget.TransformFromDevice.Transform(topLeft);

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
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero)
                return;

            var exStyle = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
            var updated = _hitTestVisible
                ? exStyle & ~NativeMethods.WS_EX_TRANSPARENT
                : exStyle | NativeMethods.WS_EX_TRANSPARENT;

            if (updated != exStyle)
                _ = NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE, updated);
        }
    }

}
