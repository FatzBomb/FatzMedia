using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using FatzMedia.Video.Core.Abstractions;

namespace FatzMedia.Video.Wpf;

public sealed partial class MediaSurface : Grid
{
    public const string DefaultSecondarySurfaceId = "secondary";
    const int WM_MOVE = 0x0003;
    const int WM_ENTERSIZEMOVE = 0x0231;
    const int WM_EXITSIZEMOVE = 0x0232;

    readonly NativeHwndSurfaceHost _surfaceHost;
    readonly ContentPresenter _overlayPresenter;
    readonly bool _isDesignMode;
    WpfCompositedSurfaceHost? _compositedHost;
    IPlayerSession? _attachedPrimarySession;
    IPlayerSession? _eventSession;
    Window? _ownerWindow;
    HwndSource? _ownerHwndSource;
    bool _primaryAttached;
    string _attachedSurfaceId = MediaPresentationSurfaceDescriptor.PrimarySurfaceId;
    MediaPresentationSurfaceRole _attachedRole = MediaPresentationSurfaceRole.Primary;
    bool _layoutRefreshQueued;
    bool _primarySurfaceLiveResize;
    volatile bool _showPrimarySurfaceAfterPresentation;
    SecondaryPresentationWindow? _secondaryWindow;
    MediaOverlayWindow? _overlayWindow;
    WpfPresentationMode _appliedPresentationMode = WpfPresentationMode.NativeHwndOverlay;

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
        ApplyContentState(forceVisibilityWrite: true);

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        // A collapsed surface (or a collapsed parent) stays loaded: the overlay window, a separate
        // top-level window, must follow visibility too (ENG-039 W1).
        IsVisibleChanged += OnIsVisibleChanged;
        SizeChanged += OnSizeChanged;
        _surfaceHost.NativeHandleChanged += OnNativeSurfaceChanged;
        _surfaceHost.NativeBoundsChanged += OnNativeSurfaceChanged;
    }

    public static readonly DependencyProperty PlayerProperty = DependencyProperty.Register(
        nameof(Player),
        typeof(IPlayerSession),
        typeof(MediaSurface),
        new PropertyMetadata(null, OnPlayerChanged));

    public IPlayerSession? Player
    {
        get => (IPlayerSession?)GetValue(PlayerProperty);
        set => SetValue(PlayerProperty, value);
    }

    public static readonly DependencyProperty VideoDisplayModeProperty = DependencyProperty.Register(
        nameof(VideoDisplayMode),
        typeof(VideoDisplayMode),
        typeof(MediaSurface),
        new PropertyMetadata(VideoDisplayMode.Zoom, OnVideoDisplayModeChanged));

    /// <summary>
    /// Gets or sets how primary video maps into the surface (forwarded to the session by a primary
    /// surface only, so a second screen does not change it).
    /// </summary>
    public VideoDisplayMode VideoDisplayMode
    {
        get => (VideoDisplayMode)GetValue(VideoDisplayModeProperty);
        set => SetValue(VideoDisplayModeProperty, value);
    }

    public static readonly DependencyProperty RoleProperty = DependencyProperty.Register(
        nameof(Role),
        typeof(MediaPresentationSurfaceRole),
        typeof(MediaSurface),
        new PropertyMetadata(MediaPresentationSurfaceRole.Primary, OnSurfaceIdentityChanged));

    /// <summary>
    /// Gets or sets the surface's role (default <see cref="MediaPresentationSurfaceRole.Primary"/>).
    /// Use <see cref="MediaPresentationSurfaceRole.Projector"/> for a second screen of a session
    /// that already has a primary surface (Stage 046A). A change re-attaches the surface.
    /// </summary>
    public MediaPresentationSurfaceRole Role
    {
        get => (MediaPresentationSurfaceRole)GetValue(RoleProperty);
        set => SetValue(RoleProperty, value);
    }

    public static readonly DependencyProperty SurfaceIdProperty = DependencyProperty.Register(
        nameof(SurfaceId),
        typeof(string),
        typeof(MediaSurface),
        new PropertyMetadata(null, OnSurfaceIdentityChanged));

    /// <summary>
    /// Gets or sets the surface id sent to the session. Unset, a primary surface uses
    /// <see cref="MediaPresentationSurfaceDescriptor.PrimarySurfaceId"/> and any other role uses
    /// <see cref="DefaultSecondarySurfaceId"/>. Surfaces of one session need distinct ids. A change
    /// re-attaches the surface.
    /// </summary>
    public string? SurfaceId
    {
        get => (string?)GetValue(SurfaceIdProperty);
        set => SetValue(SurfaceIdProperty, value);
    }

    /// <summary>Gets the id this surface attaches with (<see cref="SurfaceId"/> or the role's default).</summary>
    public string EffectiveSurfaceId => MediaSurfaceBookkeeping.ResolveSurfaceId(SurfaceId, Role, DefaultSecondarySurfaceId);

    /// <summary>Gets whether this surface is attached to a session, in any role.</summary>
    public bool IsSurfaceAttached => _primaryAttached;

    /// <summary>Gets whether this surface is attached to a session as its primary surface.</summary>
    public bool IsPrimarySurfaceAttached => _primaryAttached && _attachedRole == MediaPresentationSurfaceRole.Primary;

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

    const string SecondaryOutputObsolete =
        "Open a SecondaryPresentationWindow with ShowOnScreen instead (the test players do). Removed in a later release (ENG-039).";

    [Obsolete(SecondaryOutputObsolete)]
    public event EventHandler? SecondaryOutputsChanged;

    [Obsolete(SecondaryOutputObsolete)]
    public bool ShowSecondaryOutput()
    {
        if (Player == null)
            return false;

        if (_secondaryWindow is { IsVisible: true })
        {
            _ = _secondaryWindow.Activate();
            return true;
        }

        OpenSecondaryWindow(Player, Rect.Empty, WindowState.Normal);
        return true;
    }

    void OpenSecondaryWindow(IPlayerSession session, Rect bounds, WindowState state)
    {
        // The secondary window presents the same way as this surface, so software mode, which
        // draws only into frame sinks, reaches it too (ENG-016).
        _secondaryWindow = new SecondaryPresentationWindow(
            session,
            DefaultSecondarySurfaceId,
            MediaPresentationSurfaceRole.Projector,
            _appliedPresentationMode)
        {
            Owner = Window.GetWindow(this),
            Title = "FatzMedia.Video Secondary Output"
        };

        // The second screen hides and shows like this surface.
        _secondaryWindow.Surface.AutoHide = AutoHide;
        if (!bounds.IsEmpty)
        {
            // Placed before it shows, so a reopened window neither jumps nor attaches at the
            // default size first.
            _secondaryWindow.WindowStartupLocation = WindowStartupLocation.Manual;
            _secondaryWindow.Left = bounds.Left;
            _secondaryWindow.Top = bounds.Top;
            _secondaryWindow.Width = bounds.Width;
            _secondaryWindow.Height = bounds.Height;
            _secondaryWindow.WindowState = state;
        }

        _secondaryWindow.Closed += OnSecondaryWindowClosed;
        _secondaryWindow.Show();
        SecondaryOutputsChanged?.Invoke(this, EventArgs.Empty);
    }

    [Obsolete(SecondaryOutputObsolete)]
    public bool CloseSecondaryOutput() => CloseSecondaryWindow();

    bool CloseSecondaryWindow()
    {
        if (_secondaryWindow == null)
            return false;

        _secondaryWindow.Closed -= OnSecondaryWindowClosed;
        var window = _secondaryWindow;
        _secondaryWindow = null;
        window.Close();
        SecondaryOutputsChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    [Obsolete(SecondaryOutputObsolete)]
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

    static void OnPlayerChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var surface = (MediaSurface)d;
        if (surface._isDesignMode)
            return;

        surface.DetachSessionEvents(e.OldValue as IPlayerSession);
        surface.DetachPrimarySurface(e.OldValue as IPlayerSession);
        surface._bookkeeping.ResetAttachRefusal();

        // The content tracker follows the Player property, not the load state, so a surface that
        // is unloaded and loaded again (a tab switch, a new parent) keeps its auto-hide state, as
        // in Stage 029C: a paused video stays visible without a new frame.
        surface._contentTracker.Session = e.NewValue as IPlayerSession;
        if (e.NewValue is IPlayerSession session)
        {
            surface.AttachSessionEvents(session);
            if (surface.Role == MediaPresentationSurfaceRole.Primary)
                session.VideoDisplayMode = surface.VideoDisplayMode;
        }

        surface.AttachOrResizePrimarySurface();
        surface.UpdateNativeSurfaceVisibility();
    }

    static void OnVideoDisplayModeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var surface = (MediaSurface)d;
        if (surface.Player is IPlayerSession session
            && surface.Role == MediaPresentationSurfaceRole.Primary
            && e.NewValue is VideoDisplayMode mode)
        {
            session.VideoDisplayMode = mode;
        }
    }

    static void OnSurfaceIdentityChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var surface = (MediaSurface)d;
        if (surface._isDesignMode)
            return;

        surface.DetachPrimarySurface(surface._attachedPrimarySession);
        if (e.Property == RoleProperty
            && e.NewValue is MediaPresentationSurfaceRole.Primary
            && surface.Player is IPlayerSession session)
        {
            session.VideoDisplayMode = surface.VideoDisplayMode;
        }

        surface.AttachOrResizePrimarySurface();
    }

    static object CoercePresentationMode(DependencyObject d, object baseValue)
    {
        var surface = (MediaSurface)d;
        if (surface._isDesignMode)
            return baseValue;

        if (surface.Player is { IsOpen: true } session)
        {
            if (!Equals(baseValue, surface._appliedPresentationMode))
            {
                DiagnosticRouter.Raise(
                    session,
                    MediaDiagnosticLevel.Info,
                    DiagnosticCategory,
                    $"{surface.DescribeSurface()}: PresentationMode {baseValue} is deferred until the media is closed.");
            }

            return surface._appliedPresentationMode;
        }

        return baseValue;
    }

    static void OnPresentationModeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var surface = (MediaSurface)d;
        if (surface._isDesignMode)
            return;

        surface._appliedPresentationMode = (WpfPresentationMode)e.NewValue;
        surface.DetachPrimarySurface(surface._attachedPrimarySession);
        surface.ConfigurePresentationHost();
        surface.UpdateOverlayWindow();
        surface.AttachOrResizePrimarySurface();
        surface.ReopenSecondaryOutputInAppliedMode();
    }

    /// <summary>
    /// The secondary window's surface payload follows the presentation mode (ENG-016), so an
    /// open secondary is reopened in the new mode, in the same place.
    /// </summary>
    void ReopenSecondaryOutputInAppliedMode()
    {
        if (Player is not { } session || _secondaryWindow is not { IsVisible: true } previous)
            return;

        var bounds = previous.RestoreBounds;
        var state = previous.WindowState;
        _ = CloseSecondaryWindow();
        OpenSecondaryWindow(session, bounds, state);
    }

    static void OnOverlayContentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var surface = (MediaSurface)d;
        surface._overlayPresenter.Content = e.NewValue;
        surface._overlayPresenter.Visibility = surface.PresentationMode == WpfPresentationMode.WpfComposited
            && e.NewValue != null
                ? Visibility.Visible
                : Visibility.Collapsed;
        surface.UpdateOverlayWindow();
    }

    static void OnOverlayHitTestVisibleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var surface = (MediaSurface)d;
        surface._overlayPresenter.IsHitTestVisible = e.NewValue is true;
        surface.UpdateOverlayWindow();
    }

    void OnLoaded(object sender, RoutedEventArgs e)
    {
        AttachOwnerWindowHook();
        ConfigurePresentationHost();
        AttachSessionEvents(Player);
        // The media may have closed while unloaded (no session events then): apply a deferred mode.
        CoerceValue(PresentationModeProperty);
        AttachOrResizePrimarySurface();
        UpdateNativeSurfaceVisibility();
        UpdateOverlayWindow();
    }

    void OnUnloaded(object sender, RoutedEventArgs e)
    {
        DetachOwnerWindowHook();
        DetachSessionEvents(Player);
        DetachPrimarySurface(_attachedPrimarySession);
        CloseOverlayWindow();
        DisposeCompositedHost();
    }

    void OnSizeChanged(object sender, SizeChangedEventArgs e) => RefreshPresentationLayout();

    void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e) => UpdateOverlayWindow();

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);

        // Moving to a monitor with another scale keeps the DIP size, so no SizeChanged follows:
        // the composited surface re-renders at the new pixel size (the HWND host reports its own
        // bounds change; refreshing it too is harmless).
        if (!_isDesignMode)
            RefreshPresentationLayout();
    }

    void OnNativeSurfaceChanged(object? sender, EventArgs e) => RefreshPresentationLayout();

    void OnSecondaryWindowClosed(object? sender, EventArgs e)
    {
        if (_secondaryWindow != null)
        {
            _secondaryWindow.Closed -= OnSecondaryWindowClosed;
            _secondaryWindow = null;
            SecondaryOutputsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    void AttachOrResizePrimarySurface()
    {
        var session = Player;
        if (!IsLoaded || session == null)
            return;

        MediaNativeSurfaceDescriptor nativeSurface;
        int pixelWidth;
        int pixelHeight;
        if (PresentationMode == WpfPresentationMode.WpfComposited)
        {
            var host = EnsureCompositedHost();
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

        var dpi = VisualTreeHelper.GetDpi(this);

        if (_primaryAttached && ReferenceEquals(session, _attachedPrimarySession))
        {
            _ = session.ResizePresentationSurface(_attachedSurfaceId, pixelWidth, pixelHeight);
            ApplyPrimarySurfaceLiveResize(session);
            return;
        }

        DetachPrimarySurface(_attachedPrimarySession);
        var surfaceId = EffectiveSurfaceId;
        var role = Role;
        _primaryAttached = session.AttachPresentationSurface(new MediaPresentationSurfaceDescriptor(
            surfaceId,
            role,
            nativeSurface,
            pixelWidth,
            pixelHeight,
            dpi.DpiScaleX,
            dpi.DpiScaleY));
        _attachedPrimarySession = _primaryAttached ? session : null;
        if (_primaryAttached)
        {
            _attachedSurfaceId = surfaceId;
            _attachedRole = role;
            ApplyPrimarySurfaceLiveResize(session);
        }

        ReportAttachResult(session, _primaryAttached);
        UpdateNativeSurfaceVisibility();
    }

    void DetachPrimarySurface(IPlayerSession? session)
    {
        if (!_primaryAttached || session == null)
            return;

        if (_primarySurfaceLiveResize)
            _ = session.SetPresentationSurfaceLiveResize(_attachedSurfaceId, false);

        _ = session.DetachPresentationSurface(_attachedSurfaceId);
        _primaryAttached = false;
        _attachedPrimarySession = null;
        SetPresentationPath(MediaSurfacePresentationPath.None, null, null);
        UpdateNativeSurfaceVisibility();
    }

    void AttachSessionEvents(IPlayerSession? session)
    {
        if (session == null || ReferenceEquals(session, _eventSession))
            return;

        DetachSessionEvents(_eventSession);
        _eventSession = session;
        session.MediaStarted += OnSessionMediaStarted;
        session.MediaStopped += OnSessionMediaStopped;
        session.MediaEnded += OnSessionMediaEnded;
        session.MediaClosed += OnSessionMediaClosed;
        session.PresentationFramePresented += OnSessionPresentationFramePresented;
    }

    void DetachSessionEvents(IPlayerSession? session)
    {
        if (session == null || !ReferenceEquals(session, _eventSession))
            return;

        session.MediaStarted -= OnSessionMediaStarted;
        session.MediaStopped -= OnSessionMediaStopped;
        session.MediaEnded -= OnSessionMediaEnded;
        session.MediaClosed -= OnSessionMediaClosed;
        session.PresentationFramePresented -= OnSessionPresentationFramePresented;
        _showPrimarySurfaceAfterPresentation = false;
        _eventSession = null;
    }

    void OnSessionMediaStarted(object? sender, EventArgs e)
    {
        _showPrimarySurfaceAfterPresentation = !_contentTracker.AutoHide
            && !IsLoopingVisualPlayback(sender as IPlayerSession ?? _eventSession);
        _ = Dispatcher.BeginInvoke(UpdateNativeSurfaceVisibility);
    }

    void OnSessionMediaStopped(object? sender, EventArgs e)
    {
        _showPrimarySurfaceAfterPresentation = false;
        _ = Dispatcher.BeginInvoke(UpdateNativeSurfaceVisibility);
    }

    void OnSessionMediaEnded(object? sender, EventArgs e)
    {
        if (IsLoopingPlayback(sender as IPlayerSession ?? _eventSession))
            return;

        _showPrimarySurfaceAfterPresentation = false;
        _ = Dispatcher.BeginInvoke(UpdateNativeSurfaceVisibility);
    }

    // A deferred PresentationMode applies once the media is closed.
    void OnSessionMediaClosed(object? sender, MediaClosedEventArgs e) =>
        _ = Dispatcher.BeginInvoke(new Action(() => CoerceValue(PresentationModeProperty)));

    void OnSessionPresentationFramePresented(object? sender, EventArgs e)
    {
        // This event is raised from Core's native present/render thread, NOT the WPF UI thread.
        // Do not touch the Player dependency property here; read the plain, thread-safe _eventSession
        // reference instead. All UI-affine work is marshaled to the UI thread below.
        if (!_showPrimarySurfaceAfterPresentation && !HasStoppedStandaloneVisual(_eventSession))
            return;

        _showPrimarySurfaceAfterPresentation = false;
        _ = Dispatcher.BeginInvoke(UpdateNativeSurfaceVisibility);
    }

    void UpdateNativeSurfaceVisibility()
    {
        var session = Player;
        var shouldShow = _primaryAttached
            && (session is { State: MediaPlayerState.Playing or MediaPlayerState.Paused } || HasStoppedStandaloneVisual(session))
            && !_showPrimarySurfaceAfterPresentation
            && _contentTracker.ShowsContent;

        if (shouldShow)
        {
            if (PresentationMode == WpfPresentationMode.WpfComposited)
            {
                _compositedHost?.Visibility = Visibility.Visible;
            }
            else
            {
                if (_surfaceHost.Visibility != Visibility.Visible)
                    _surfaceHost.Visibility = Visibility.Visible;

                _surfaceHost.SetNativeVisible(true);
            }

            RaiseFirstFrameReadyIfDue();
            return;
        }

        if (PresentationMode == WpfPresentationMode.NativeHwndOverlay)
        {
            _surfaceHost.SetNativeVisible(false);
            if (_surfaceHost.Visibility != Visibility.Hidden)
                _surfaceHost.Visibility = Visibility.Hidden;
        }

        _compositedHost?.Visibility = Visibility.Hidden;
    }

    // A standalone visual without an audio stream (image, or video/CDG with no audio) is presented to
    // the swap chain by Core on load but never enters Playing/Paused, so show the native surface for it.
    // The Stopped gate keeps this from re-dispatching on every presented frame during no-audio playback.
    static bool HasStandaloneVisual(IPlayerSession? session) =>
        session is { IsOpen: true }
        && !session.Content.HasFlag(MediaContent.Audio)
        && (session.Content.HasFlag(MediaContent.Image)
            || session.Content.HasFlag(MediaContent.Video)
            || session.Content.HasFlag(MediaContent.Cdg));

    static bool HasStoppedStandaloneVisual(IPlayerSession? session) =>
        session is { State: MediaPlayerState.Stopped } && HasStandaloneVisual(session);

    static bool IsLoopingVisualPlayback(IPlayerSession? session) =>
        session is { IsOpen: true, LoopMedia: true }
        && (session.Content.HasFlag(MediaContent.Video)
            || session.Content.HasFlag(MediaContent.Cdg)
            || session.Content.HasFlag(MediaContent.Image));

    static bool IsLoopingPlayback(IPlayerSession? session) =>
        session is { IsOpen: true, LoopMedia: true };

    void AttachOwnerWindowHook()
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

    void AttachOwnerHwndSource(Window window)
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

    void DetachOwnerWindowHook()
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

    void OnOwnerWindowSourceInitialized(object? sender, EventArgs e)
    {
        if (sender is Window window)
            AttachOwnerHwndSource(window);
    }

    void OnOwnerWindowClosed(object? sender, EventArgs e) => DetachOwnerWindowHook();

    IntPtr OnOwnerWindowMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_MOVE)
        {
            // The overlay is its own window: follow every move of the owner during a drag, not
            // only the layout refresh when the drag ends (owner's 046A smoke).
            if (_overlayWindow is { IsVisible: true } overlay)
                overlay.UpdateBounds(this);
        }
        else if (msg == WM_ENTERSIZEMOVE)
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

    void SetPrimarySurfaceLiveResize(bool isResizing)
    {
        if (_primarySurfaceLiveResize == isResizing)
            return;

        _primarySurfaceLiveResize = isResizing;
        if (_primaryAttached && _attachedPrimarySession != null)
        {
            _ = _attachedPrimarySession.SetPresentationSurfaceLiveResize(
                _attachedSurfaceId,
                isResizing);
        }
    }

    void ApplyPrimarySurfaceLiveResize(IPlayerSession session)
    {
        if (_primarySurfaceLiveResize)
        {
            _ = session.SetPresentationSurfaceLiveResize(
                _attachedSurfaceId,
                true);
        }
    }

    void UpdateOverlayWindow()
    {
        if (!IsLoaded || !IsVisible || OverlayContent == null || PresentationMode != WpfPresentationMode.NativeHwndOverlay)
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

    void ConfigurePresentationHost()
    {
        if (PresentationMode == WpfPresentationMode.WpfComposited)
        {
            _surfaceHost.SetNativeVisible(false);
            Children.Remove(_surfaceHost);
            var host = EnsureCompositedHost();
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

    WpfCompositedSurfaceHost EnsureCompositedHost()
    {
        _compositedHost ??= new WpfCompositedSurfaceHost(RaiseFirstFrameReadyIfDue);
        return _compositedHost;
    }

    void DisposeCompositedHost()
    {
        if (_compositedHost == null)
            return;

        Children.Remove(_compositedHost);
        RetireCompositedCounters(_compositedHost);
        _compositedHost.Dispose();
        _compositedHost = null;
    }

    MediaOverlayWindow CreateOverlayWindow()
    {
        var window = new MediaOverlayWindow
        {
            Owner = Window.GetWindow(this)
        };
        window.Closed += (_, _) => _overlayWindow = null;
        return window;
    }

    void CloseOverlayWindow()
    {
        if (_overlayWindow == null)
            return;

        _overlayWindow.SetContent(null);
        var window = _overlayWindow;
        _overlayWindow = null;
        window.Close();
    }
}
