using FatzBomb.MediaControl.Core.Abstractions;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace FatzBomb.MediaControl.Wpf;

public sealed partial class MediaSurface : Grid
{
    public const string DefaultSecondarySurfaceId = "secondary";
    private const int WM_ENTERSIZEMOVE = 0x0231;
    private const int WM_EXITSIZEMOVE = 0x0232;

    private readonly NativeHwndSurfaceHost _surfaceHost;
    private readonly ContentPresenter _overlayPresenter;
    private readonly bool _isDesignMode;
    private WpfCompositedSurfaceHost? _compositedHost;
    private IMediaControlSession? _attachedPrimarySession;
    private IMediaControlSession? _eventSession;
    private Window? _ownerWindow;
    private HwndSource? _ownerHwndSource;
    private bool _primaryAttached;
    private bool _layoutRefreshQueued;
    private bool _primarySurfaceLiveResize;
    private volatile bool _showPrimarySurfaceAfterPresentation;
    private SecondaryPresentationWindow? _secondaryWindow;
    private MediaOverlayWindow? _overlayWindow;
    private WpfPresentationMode _appliedPresentationMode = WpfPresentationMode.NativeHwndOverlay;

    public MediaSurface()
    {
        Background = Brushes.Black;
        ClipToBounds = true;
        _isDesignMode = DesignerProperties.GetIsInDesignMode(this);
        _surfaceHost = new NativeHwndSurfaceHost();
        _overlayPresenter = new ContentPresenter
        {
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false
        };
        if (_isDesignMode)
        {
            _ = Children.Add(new Border
            {
                Background = Brushes.Black,
                Child = new TextBlock
                {
                    Text = nameof(MediaSurface),
                    Foreground = Brushes.Gray,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            });
            return;
        }

        _ = Children.Add(_surfaceHost);
        _ = Children.Add(_overlayPresenter);
        InitializeAutoHide();

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
        typeof(WpfPresentationMode),
        typeof(MediaSurface),
        new PropertyMetadata(
            WpfPresentationMode.NativeHwndOverlay,
            OnPresentationModeChanged,
            CoercePresentationMode));

    public WpfPresentationMode PresentationMode
    {
        get => (WpfPresentationMode)GetValue(PresentationModeProperty);
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
        if (surface._isDesignMode)
            return;

        surface.DetachSessionEvents(e.OldValue as IMediaControlSession);
        surface.DetachPrimarySurface(e.OldValue as IMediaControlSession);
        surface.OnAutoHideSessionChanged(e.NewValue as IMediaControlSession);
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

    private static object CoercePresentationMode(DependencyObject d, object baseValue)
    {
        var surface = (MediaSurface)d;
        if (surface._isDesignMode)
            return baseValue;

        if (surface.Player is { IsOpen: true })
        {
            System.Diagnostics.Debug.WriteLine(
                $"[{nameof(MediaSurface)}] PresentationMode change deferred until media is closed.");
            return surface._appliedPresentationMode;
        }

        return baseValue;
    }

    private static void OnPresentationModeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var surface = (MediaSurface)d;
        if (surface._isDesignMode)
            return;

        surface._appliedPresentationMode = (WpfPresentationMode)e.NewValue;
        surface.DetachPrimarySurface(surface._attachedPrimarySession);
        surface.ConfigurePresentationHost();
        surface.UpdateOverlayWindow();
        surface.AttachOrResizePrimarySurface();
    }

    private static void OnOverlayContentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var surface = (MediaSurface)d;
        surface._overlayPresenter.Content = e.NewValue;
        surface._overlayPresenter.Visibility = surface.PresentationMode == WpfPresentationMode.WpfComposited
            && e.NewValue != null
                ? Visibility.Visible
                : Visibility.Collapsed;
        surface.UpdateOverlayWindow();
    }

    private static void OnOverlayHitTestVisibleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var surface = (MediaSurface)d;
        surface._overlayPresenter.IsHitTestVisible = e.NewValue is true;
        surface.UpdateOverlayWindow();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        AttachOwnerWindowHook();
        ConfigurePresentationHost();
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
        DisposeCompositedHost();
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
        if (!IsLoaded || session == null)
            return;

        MediaNativeSurfaceDescriptor nativeSurface;
        int pixelWidth;
        int pixelHeight;
        if (PresentationMode == WpfPresentationMode.WpfComposited)
        {
            WpfCompositedSurfaceHost host = EnsureCompositedHost();
            pixelWidth = Math.Max(1, (int)Math.Round(ActualWidth * VisualTreeHelper.GetDpi(this).DpiScaleX));
            pixelHeight = Math.Max(1, (int)Math.Round(ActualHeight * VisualTreeHelper.GetDpi(this).DpiScaleY));
            nativeSurface = host.CreateSurfaceDescriptor();
        }
        else
        {
            if (_surfaceHost.WindowHandle == IntPtr.Zero)
                return;

            pixelWidth = Math.Max(1, _surfaceHost.NativeClientWidth);
            pixelHeight = Math.Max(1, _surfaceHost.NativeClientHeight);
            nativeSurface = MediaNativeSurfaceDescriptor.Win32Hwnd(_surfaceHost.WindowHandle);
        }

        DpiScale dpi = VisualTreeHelper.GetDpi(this);

        if (_primaryAttached && ReferenceEquals(session, _attachedPrimarySession))
        {
            _ = session.ResizePresentationSurface(MediaPresentationSurfaceDescriptor.PrimarySurfaceId, pixelWidth, pixelHeight);
            ApplyPrimarySurfaceLiveResize(session);
            return;
        }

        DetachPrimarySurface(_attachedPrimarySession);
        _primaryAttached = session.AttachPresentationSurface(new MediaPresentationSurfaceDescriptor(
            MediaPresentationSurfaceDescriptor.PrimarySurfaceId,
            MediaPresentationSurfaceRole.Primary,
            nativeSurface,
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
        session.MediaLoaded += OnAutoHideMediaLoaded;
        session.MediaClosed += OnAutoHideMediaClosed;
        session.MediaPaused += OnAutoHideMediaPaused;
        session.MediaLooped += OnAutoHideMediaLooped;
        session.MediaError += OnAutoHideMediaError;
        session.PresentationFramePresented += OnSessionPresentationFramePresented;
    }

    private void DetachSessionEvents(IMediaControlSession? session)
    {
        if (session == null || !ReferenceEquals(session, _eventSession))
            return;

        session.MediaStarted -= OnSessionMediaStarted;
        session.MediaStopped -= OnSessionMediaStopped;
        session.MediaEnded -= OnSessionMediaEnded;
        session.MediaLoaded -= OnAutoHideMediaLoaded;
        session.MediaClosed -= OnAutoHideMediaClosed;
        session.MediaPaused -= OnAutoHideMediaPaused;
        session.MediaLooped -= OnAutoHideMediaLooped;
        session.MediaError -= OnAutoHideMediaError;
        session.PresentationFramePresented -= OnSessionPresentationFramePresented;
        _showPrimarySurfaceAfterPresentation = false;
        _eventSession = null;
    }

    private void OnSessionMediaStarted(object? sender, EventArgs e)
    {
        QueueAutoHideTransportSignal(static policy => policy.MediaStarted());
        _showPrimarySurfaceAfterPresentation = !IsAutoHideEnabled
            && !IsLoopingVisualPlayback(sender as IMediaControlSession ?? _eventSession);
        _ = Dispatcher.BeginInvoke(UpdateNativeSurfaceVisibility);
    }

    private void OnSessionMediaStopped(object? sender, EventArgs e)
    {
        var session = sender as IMediaControlSession ?? _eventSession;
        if (!IsStaleTerminalEvent(session))
            QueueAutoHideHidden(SurfaceAutoHideHideReason.Stopped);
        _showPrimarySurfaceAfterPresentation = false;
        _ = Dispatcher.BeginInvoke(UpdateNativeSurfaceVisibility);
    }

    private void OnSessionMediaEnded(object? sender, EventArgs e)
    {
        var session = sender as IMediaControlSession ?? _eventSession;
        var isLooping = IsLoopingPlayback(session);
        if (!IsStaleTerminalEvent(session))
            QueueAutoHideMediaEnded(isLooping);
        if (isLooping)
            return;

        _showPrimarySurfaceAfterPresentation = false;
        _ = Dispatcher.BeginInvoke(UpdateNativeSurfaceVisibility);
    }

    private void OnSessionPresentationFramePresented(object? sender, EventArgs e)
    {
        QueueAutoHideFramePresented();

        // This event is raised from Core's native present/render thread, NOT the WPF UI thread.
        // Do not touch the Player dependency property here; read the plain, thread-safe _eventSession
        // reference instead. All UI-affine work is marshaled to the UI thread below.
        if (!_showPrimarySurfaceAfterPresentation && !HasStoppedStandaloneVisual(_eventSession))
            return;

        _showPrimarySurfaceAfterPresentation = false;
        _ = Dispatcher.BeginInvoke(UpdateNativeSurfaceVisibility);
    }

    private void UpdateNativeSurfaceVisibility()
    {
        IMediaControlSession? session = Player;
        var shouldShow = _primaryAttached
            && (session is { State: MediaPlayerState.Playing or MediaPlayerState.Paused } || HasStoppedStandaloneVisual(session))
            && !_showPrimarySurfaceAfterPresentation
            && (!AutoHide || _autoHidePolicy.IsContentVisible || IsAutoHidePresentationArmed);

        if (shouldShow)
        {
            if (PresentationMode == WpfPresentationMode.WpfComposited)
            {
                if (_compositedHost != null)
                    _compositedHost.Visibility = Visibility.Visible;
            }
            else
            {
                if (_surfaceHost.Visibility != Visibility.Visible)
                    _surfaceHost.Visibility = Visibility.Visible;

                _surfaceHost.SetNativeVisible(true);
            }
            return;
        }

        if (PresentationMode == WpfPresentationMode.NativeHwndOverlay)
        {
            _surfaceHost.SetNativeVisible(false);
            if (_surfaceHost.Visibility != Visibility.Hidden)
                _surfaceHost.Visibility = Visibility.Hidden;
        }

        if (_compositedHost != null)
            _compositedHost.Visibility = Visibility.Hidden;
    }

    // A standalone visual without an audio stream (image, or video/CDG with no audio) is presented to
    // the swap chain by Core on load but never enters Playing/Paused, so show the native surface for it.
    // The Stopped gate keeps this from re-dispatching on every presented frame during no-audio playback.
    private static bool HasStandaloneVisual(IMediaControlSession? session) =>
        session is { IsOpen: true }
        && !session.Content.HasFlag(MediaContent.Audio)
        && (session.Content.HasFlag(MediaContent.Image)
            || session.Content.HasFlag(MediaContent.Video)
            || session.Content.HasFlag(MediaContent.Cdg));

    private static bool HasStoppedStandaloneVisual(IMediaControlSession? session) =>
        session is { State: MediaPlayerState.Stopped } && HasStandaloneVisual(session);

    private static bool IsLoopingVisualPlayback(IMediaControlSession? session) =>
        session is { IsOpen: true, LoopMedia: true }
        && (session.Content.HasFlag(MediaContent.Video)
            || session.Content.HasFlag(MediaContent.Cdg)
            || session.Content.HasFlag(MediaContent.Image));

    private static bool IsLoopingPlayback(IMediaControlSession? session) =>
        session is { IsOpen: true, LoopMedia: true };

    private bool IsStaleTerminalEvent(IMediaControlSession? session) =>
        IsAutoHideEnabled
        && session is { State: MediaPlayerState.Playing or MediaPlayerState.Paused or MediaPlayerState.Buffering };

    private void AttachOwnerWindowHook()
    {
        var window = Window.GetWindow(this);
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

        var source = HwndSource.FromHwnd(hwnd);
        if (source == null || ReferenceEquals(source, _ownerHwndSource))
            return;

        _ownerHwndSource?.RemoveHook(OnOwnerWindowMessage);
        _ownerHwndSource = source;
        _ownerHwndSource.AddHook(OnOwnerWindowMessage);
    }

    private void DetachOwnerWindowHook()
    {
        SetPrimarySurfaceLiveResize(false);
        _ownerHwndSource?.RemoveHook(OnOwnerWindowMessage);
        _ownerHwndSource = null;

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
        if (!IsLoaded || OverlayContent == null || PresentationMode != WpfPresentationMode.NativeHwndOverlay)
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

    private void ConfigurePresentationHost()
    {
        if (PresentationMode == WpfPresentationMode.WpfComposited)
        {
            _surfaceHost.SetNativeVisible(false);
            Children.Remove(_surfaceHost);
            WpfCompositedSurfaceHost host = EnsureCompositedHost();
            if (!Children.Contains(host))
                Children.Insert(0, host);

            _overlayPresenter.Content = OverlayContent;
            _overlayPresenter.IsHitTestVisible = OverlayHitTestVisible;
            _overlayPresenter.Visibility = OverlayContent == null ? Visibility.Collapsed : Visibility.Visible;
            CloseOverlayWindow();
            return;
        }

        DisposeCompositedHost();
        if (!Children.Contains(_surfaceHost))
            Children.Insert(0, _surfaceHost);

        _overlayPresenter.Visibility = Visibility.Collapsed;
        _overlayPresenter.Content = null;
    }

    private WpfCompositedSurfaceHost EnsureCompositedHost()
    {
        _compositedHost ??= new WpfCompositedSurfaceHost();
        return _compositedHost;
    }

    private void DisposeCompositedHost()
    {
        if (_compositedHost == null)
            return;

        Children.Remove(_compositedHost);
        _compositedHost.Dispose();
        _compositedHost = null;
    }

    private MediaOverlayWindow CreateOverlayWindow()
    {
        var window = new MediaOverlayWindow
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
        MediaOverlayWindow window = _overlayWindow;
        _overlayWindow = null;
        window.Close();
    }
}
