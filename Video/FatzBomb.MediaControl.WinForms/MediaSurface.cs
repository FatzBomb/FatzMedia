using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace FatzBomb.MediaControl.WinForms;

/// <summary>
/// WinForms media surface: a control whose child window is attached to the bound
/// <see cref="IMediaControlSession"/> as a presentation surface, the primary one by default (a
/// second screen sets <see cref="Role"/>). The host-selected renderer (for example Direct3D11)
/// presents into that child window; the control owns only window lifecycle, size, DPI, the
/// live-resize hint, and showing or hiding the child.
/// </summary>
/// <remarks>
/// <para>
/// When the renderer does not present into windows (the software renderer, or one that presents
/// only through frame sinks) or <see cref="PreferNativeWindow"/> is off, the surface attaches a
/// managed frame sink instead (Stage 046A): the renderer's final BGRA frames are copied to a bitmap
/// on the UI thread and drawn, double-buffered, in <see cref="OnPaint"/>.
/// <see cref="PresentationPath"/> says which path is used and why.
/// </para>
/// <para>
/// <see cref="OverlayContent"/> shows a host control above the video in a layered window owned by
/// the surface's form (like the WPF adapter's overlay window), on either path.
/// </para>
/// <para>
/// The child window is hidden, and the control shows its <see cref="Control.BackColor"/>, while
/// nothing should be seen (Stage 046A, the same rules as the WPF and Avalonia surfaces, decided by
/// Core's <see cref="MediaSurfaceContentTracker"/>): in Audio mode (nothing presents, so the last
/// frame would stay), and with <see cref="AutoHide"/> while no frame of the current media is shown.
/// The child window passes mouse input through to this control, so the control's mouse events
/// (and a host's double-click handler) work as on any control.
/// </para>
/// <para>
/// Session events arrive on background threads; the control marshals them itself. Its own
/// events (<see cref="ContentVisibilityChanged"/>, <see cref="PresentationPathChanged"/>) are
/// raised on the UI thread.
/// </para>
/// </remarks>
public class MediaSurface : Control
{
    /// <summary>The surface id a non-primary surface uses when <see cref="SurfaceId"/> is not set.</summary>
    public const string DefaultSecondarySurfaceId = "secondary";

    /// <summary>The diagnostic category of the surface's path decisions and attach problems.</summary>
    public const string DiagnosticCategory = "WinForms.MediaSurface";


    private readonly MediaSurfaceBookkeeping _bookkeeping = new(DiagnosticCategory);
    private readonly PresentationHost _host = new();
    private readonly MediaSurfaceContentTracker _contentTracker = new();
    private IMediaControlSession? _player;
    private IMediaControlSession? _attachedSession;
    private string? _attachedSurfaceId;
    private (int Width, int Height) _attachedPixelSize;
    private VideoDisplayMode _videoDisplayMode = VideoDisplayMode.Zoom;
    private MediaPresentationSurfaceRole _role = MediaPresentationSurfaceRole.Primary;
    private string? _surfaceId;
    private Form? _resizeForm;
    private bool _isLiveResizing;
    private int _contentStateQueued;
    private bool _isContentVisible;
    private bool _isPresentationSuspended;
    private bool _presentationWindowShown;
    private IMediaControlSession? _frameSession;
    private int _framePresentedSinceAttach;
    private bool _preferNativeWindow = true;
    private WinFormsFrameSink? _sink;
    private bool _framesShown;
    private Control? _overlayContent;
    private bool _overlayHitTestVisible;
    private MediaOverlayForm? _overlayForm;
    private readonly List<Control> _overlayAnchors = [];
    private bool _overlayAnchorsWatched;
    private SolidBrush? _backgroundBrush;

    public MediaSurface()
    {
        BackColor = Color.Black;

        // The control paints only its background; the renderer owns the child window's pixels.
        SetStyle(ControlStyles.Opaque | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint, true);
        SetStyle(ControlStyles.OptimizedDoubleBuffer, false);
        Controls.Add(_host);
        _contentTracker.Changed += OnContentTrackerChanged;
    }

    /// <summary>Raised on the UI thread when <see cref="IsContentVisible"/> changes.</summary>
    public event EventHandler? ContentVisibilityChanged;

    /// <summary>Raised on the UI thread when <see cref="PresentationPath"/> or its reason changes.</summary>
    public event EventHandler? PresentationPathChanged;

    /// <summary>Gets or sets the session this surface presents.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    [Browsable(false)]
    public IMediaControlSession? Player
    {
        get => _player;
        set
        {
            if (ReferenceEquals(_player, value))
                return;

            Detach();
            _player = value;
            _bookkeeping.ResetAttachRefusal();
            _contentTracker.Session = value;
            if (value is not null && _role == MediaPresentationSurfaceRole.Primary)
                value.VideoDisplayMode = _videoDisplayMode;
            AttachOrResize();
            UpdateContentState();
        }
    }

    /// <summary>
    /// Gets or sets how primary video maps into the surface (forwarded to the session by a primary
    /// surface only, so a second screen does not change it).
    /// </summary>
    [DefaultValue(VideoDisplayMode.Zoom)]
    public VideoDisplayMode VideoDisplayMode
    {
        get => _videoDisplayMode;
        set
        {
            _videoDisplayMode = value;
            if (_player is not null && _role == MediaPresentationSurfaceRole.Primary)
                _player.VideoDisplayMode = value;
        }
    }

    /// <summary>
    /// Gets or sets the surface's role (default <see cref="MediaPresentationSurfaceRole.Primary"/>).
    /// Use <see cref="MediaPresentationSurfaceRole.Projector"/> for a second screen of a session
    /// that already has a primary surface. A change re-attaches the surface.
    /// </summary>
    [DefaultValue(MediaPresentationSurfaceRole.Primary)]
    public MediaPresentationSurfaceRole Role
    {
        get => _role;
        set
        {
            if (_role == value)
                return;

            Detach();
            _role = value;
            if (_player is not null && value == MediaPresentationSurfaceRole.Primary)
                _player.VideoDisplayMode = _videoDisplayMode;
            AttachOrResize();
        }
    }

    /// <summary>
    /// Gets or sets the surface id sent to the session. Unset, a primary surface uses
    /// <see cref="MediaPresentationSurfaceDescriptor.PrimarySurfaceId"/> and any other role uses
    /// <see cref="DefaultSecondarySurfaceId"/>. Surfaces of one session need distinct ids. A change
    /// re-attaches the surface.
    /// </summary>
    [DefaultValue(null)]
    public string? SurfaceId
    {
        get => _surfaceId;
        set
        {
            if (string.Equals(_surfaceId, value, StringComparison.Ordinal))
                return;

            Detach();
            _surfaceId = value;
            AttachOrResize();
        }
    }

    /// <summary>
    /// Gets or sets whether the surface lets the renderer present into its child window (default
    /// <see langword="true"/>). When the renderer refuses the window, or this is off, the surface
    /// presents through a managed frame sink. A change re-attaches the surface.
    /// </summary>
    [DefaultValue(true)]
    public bool PreferNativeWindow
    {
        get => _preferNativeWindow;
        set
        {
            if (_preferNativeWindow == value)
                return;

            Detach();
            _preferNativeWindow = value;
            AttachOrResize();
        }
    }

    /// <summary>
    /// Gets or sets a host control shown above the video (default <see langword="null"/>), for
    /// example full-screen transport controls. It is shown in a borderless layered window owned
    /// by the surface's form, which follows the surface's position, size and visibility, on either
    /// presentation path. The window's background is a colour key, so a control with a
    /// <see cref="Color.Transparent"/> background shows the video around its children; it never
    /// takes the keyboard focus. The surface never disposes the control.
    /// </summary>
    [DefaultValue(null)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Control? OverlayContent
    {
        get => _overlayContent;
        set
        {
            if (ReferenceEquals(_overlayContent, value))
                return;

            _overlayForm?.SetContent(null);
            _overlayContent = value;
            UpdateOverlay();
        }
    }

    /// <summary>
    /// Gets or sets whether <see cref="OverlayContent"/> takes mouse input (default
    /// <see langword="false"/>: clicks pass through to the surface, as in the WPF adapter).
    /// </summary>
    [DefaultValue(false)]
    public bool OverlayHitTestVisible
    {
        get => _overlayHitTestVisible;
        set
        {
            if (_overlayHitTestVisible == value)
                return;

            _overlayHitTestVisible = value;
            _overlayForm?.SetHitTestVisible(value);
        }
    }

    /// <summary>
    /// Gets or sets whether the surface hides its frames (shows only <see cref="Control.BackColor"/>)
    /// while no frame of the current media is shown: before the first frame of a load, after stop,
    /// close, an error, or a natural end without looping (default <see langword="true"/>, as in the
    /// WPF and Avalonia surfaces). <see langword="false"/> shows whatever the renderer presented.
    /// </summary>
    [DefaultValue(true)]
    public bool AutoHide
    {
        get => _contentTracker.AutoHide;
        set
        {
            if (_contentTracker.AutoHide == value)
                return;

            _contentTracker.AutoHide = value;
            UpdateContentState();
        }
    }

    /// <summary>Gets whether auto-hide shows the content now (always false while <see cref="AutoHide"/> is off).</summary>
    [Browsable(false)]
    public bool IsContentVisible => _isContentVisible;

    /// <summary>
    /// Gets whether the surface shows only its background because nothing presents to it: the
    /// session is in <see cref="MediaPlayerMode.Audio"/> mode. The last video frame is not left on
    /// screen; frames show again once the session leaves Audio mode.
    /// </summary>
    [Browsable(false)]
    public bool IsPresentationSuspended => _isPresentationSuspended;

    /// <summary>
    /// Gets how frames reach the screen now: <see cref="MediaSurfacePresentationPath.NativeWindow"/>
    /// or <see cref="MediaSurfacePresentationPath.FrameSink"/> while attached,
    /// <see cref="MediaSurfacePresentationPath.None"/> otherwise.
    /// </summary>
    [Browsable(false)]
    public MediaSurfacePresentationPath PresentationPath => _bookkeeping.Path;

    /// <summary>Gets why the current path was chosen.</summary>
    [Browsable(false)]
    public string? PresentationPathReason => _bookkeeping.Reason;

    /// <summary>Gets the id this surface attaches with (<see cref="SurfaceId"/> or the role's default).</summary>
    [Browsable(false)]
    public string EffectiveSurfaceId => MediaSurfaceBookkeeping.ResolveSurfaceId(_surfaceId, _role, DefaultSecondarySurfaceId);

    /// <summary>Gets whether this surface is attached to a session, in any role.</summary>
    [Browsable(false)]
    public bool IsSurfaceAttached => _attachedSession is not null;

    /// <summary>Gets whether this surface is attached to a session as its primary surface.</summary>
    [Browsable(false)]
    public bool IsPrimarySurfaceAttached => _attachedSession is not null && _role == MediaPresentationSurfaceRole.Primary;

    /// <summary>The window the renderer presents into (tests).</summary>
    internal IntPtr PresentationHandle => _host.Handle;

    /// <summary>Whether the presentation window is shown now (tests).</summary>
    internal bool IsPresentationWindowVisible => _presentationWindowShown;

    /// <summary>The frame sink while the frame-sink path is used (tests).</summary>
    internal WinFormsFrameSink? FrameSink => _sink;

    /// <summary>Whether frame-sink frames are drawn now (tests).</summary>
    internal bool AreFramesShown => _framesShown;

    /// <summary>The overlay window while one exists (tests).</summary>
    internal Form? OverlayWindow => _overlayForm;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);

        // A callback queued on the previous handle is dropped when that handle goes, so the
        // coalescing flag would stay set and block every later background update.
        Volatile.Write(ref _contentStateQueued, 0);
        WatchFormResize();
        AttachOrResize();
        UpdateContentState();
        RewatchOverlayAnchors();
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        Detach();
        WatchFormResize(null);
        CloseOverlay();
        WatchOverlayAnchors(false);
        base.OnHandleDestroyed(e);
    }

    protected override void OnParentChanged(EventArgs e)
    {
        base.OnParentChanged(e);
        if (IsHandleCreated)
            WatchFormResize();
        RewatchOverlayAnchors();
    }

    protected override void OnClientSizeChanged(EventArgs e)
    {
        base.OnClientSizeChanged(e);
        AttachOrResize();
        UpdateOverlay();
    }

    protected override void OnLocationChanged(EventArgs e)
    {
        base.OnLocationChanged(e);
        UpdateOverlay();
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        UpdateOverlay();
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        AttachOrResize();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        // The child window is clipped out while it shows; this paints the background around and
        // instead of it. On the frame-sink path it is the frame's backdrop (frames may carry
        // alpha), painted into the same back buffer, so nothing flickers.
        Graphics graphics = e.Graphics;
        graphics.FillRectangle(GetBackgroundBrush(), ClientRectangle);

        if (!_framesShown || _sink?.Bitmap is not { } frame)
            return;

        // The renderer composed for this pixel size (letterboxing included); during a resize the
        // last frame is stretched until the next one arrives.
        var unscaled = frame.Width == ClientSize.Width && frame.Height == ClientSize.Height;
        graphics.CompositingMode = CompositingMode.SourceOver;
        graphics.InterpolationMode = unscaled ? InterpolationMode.NearestNeighbor : InterpolationMode.Bilinear;
        graphics.PixelOffsetMode = PixelOffsetMode.Half;
        graphics.DrawImage(frame, ClientRectangle, 0, 0, frame.Width, frame.Height, GraphicsUnit.Pixel);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Detach();
            WatchFormResize(null);
            CloseOverlay();
            WatchOverlayAnchors(false);
            _contentTracker.Changed -= OnContentTrackerChanged;
            _contentTracker.Dispose();
            _backgroundBrush?.Dispose();
            _backgroundBrush = null;
        }

        base.Dispose(disposing);
    }

    // On the frame-sink path OnPaint runs per frame: one brush, recoloured when BackColor changes,
    // rather than a new one each paint (ENG-039).
    private SolidBrush GetBackgroundBrush()
    {
        Color color = BackColor;
        if (_backgroundBrush is null)
            _backgroundBrush = new SolidBrush(color);
        else if (_backgroundBrush.Color != color)
            _backgroundBrush.Color = color;

        return _backgroundBrush;
    }

    private void AttachOrResize()
    {
        IMediaControlSession? session = _player;
        if (session is null || !IsHandleCreated || ClientSize.Width <= 0 || ClientSize.Height <= 0)
            return;

        // The child window covers the control; ClientSize is in device pixels for a DPI-aware
        // WinForms process.
        if (_host.Bounds != ClientRectangle)
            _host.Bounds = ClientRectangle;

        (int Width, int Height) pixelSize = (ClientSize.Width, ClientSize.Height);
        if (ReferenceEquals(session, _attachedSession))
        {
            if (pixelSize != _attachedPixelSize
                && session.ResizePresentationSurface(_attachedSurfaceId!, pixelSize.Width, pixelSize.Height))
            {
                _attachedPixelSize = pixelSize;
            }

            return;
        }

        Detach();
        var scale = DeviceDpi / 96.0;
        var surfaceId = EffectiveSurfaceId;

        var attached = false;
        if (_preferNativeWindow)
        {
            // The child window has no pixels of its own until the renderer presents into it, so it
            // stays hidden (the control paints BackColor) until a frame arrives, whatever AutoHide
            // says. Listen before attaching: the attach itself presents the current frame.
            WatchPresentedFrames(session);
            attached = session.AttachPresentationSurface(CreateDescriptor(
                surfaceId, MediaNativeSurfaceDescriptor.Win32Hwnd(_host.Handle), pixelSize, scale));
            if (attached)
            {
                OnAttached(session, surfaceId, pixelSize);
                SetPath(MediaSurfacePresentationPath.NativeWindow, "the renderer presents into the control's child window");
            }
            else
            {
                WatchPresentedFrames(null);
            }
        }

        if (!attached)
        {
            // Stage 046A: a renderer that does not present into windows (or a host that asked for
            // it) gets a frame sink; the surface draws its frames itself.
            WinFormsFrameSink? sink = null;
            Action publish = () => PublishFrame(sink!);
            sink = new WinFormsFrameSink(() => RequestFramePublish(sink!, publish));
            _sink = sink;
            attached = session.AttachPresentationSurface(CreateDescriptor(surfaceId, sink.CreateSurfaceDescriptor(), pixelSize, scale));
            if (attached)
            {
                OnAttached(session, surfaceId, pixelSize);
                SetStyle(ControlStyles.OptimizedDoubleBuffer, true);
                SetPath(
                    MediaSurfacePresentationPath.FrameSink,
                    _preferNativeWindow
                        ? "the renderer did not accept the window handle (it presents only through frame sinks)"
                        : "PreferNativeWindow is off");
            }
            else
            {
                _sink = null;
                sink.Dispose();
            }
        }

        if (!attached)
        {
            // Layout retries the attach on every resize: Core reports a refusal once per session.
            _bookkeeping.ReportAttachRefused(session, EffectiveSurfaceId);
        }

        UpdateContentState();
    }

    private MediaPresentationSurfaceDescriptor CreateDescriptor(
        string surfaceId,
        MediaNativeSurfaceDescriptor nativeSurface,
        (int Width, int Height) pixelSize,
        double scale) => new(surfaceId, _role, nativeSurface, pixelSize.Width, pixelSize.Height, scale, scale);

    private void OnAttached(IMediaControlSession session, string surfaceId, (int Width, int Height) pixelSize)
    {
        _attachedSession = session;
        _attachedSurfaceId = surfaceId;
        _attachedPixelSize = pixelSize;
        _bookkeeping.ResetAttachRefusal();
        if (_isLiveResizing)
            _ = session.SetPresentationSurfaceLiveResize(surfaceId, true);
    }

    private void Detach()
    {
        IMediaControlSession? session = _attachedSession;
        var surfaceId = _attachedSurfaceId;
        _attachedSession = null;
        _attachedSurfaceId = null;
        _attachedPixelSize = default;
        if (session is not null && surfaceId is not null)
        {
            if (_isLiveResizing)
                _ = session.SetPresentationSurfaceLiveResize(surfaceId, false);
            _ = session.DetachPresentationSurface(surfaceId);
        }

        // The renderer no longer calls the sink once the surface is detached.
        WinFormsFrameSink? sink = _sink;
        _sink = null;
        sink?.Dispose();
        if (_framesShown)
        {
            _framesShown = false;
            Invalidate();
        }

        SetStyle(ControlStyles.OptimizedDoubleBuffer, false);
        WatchPresentedFrames(null);
        SetPath(MediaSurfacePresentationPath.None, null);
        ShowPresentationWindow(false);
    }

    // The presentation thread, when a frame waits in the sink: publish it on the UI thread.
    private void RequestFramePublish(WinFormsFrameSink sink, Action publish)
    {
        if (!IsHandleCreated)
        {
            sink.CancelPublishRequest();
            return;
        }

        try
        {
            _ = BeginInvoke(publish);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            // The handle went away meanwhile; the surface detaches with it.
            sink.CancelPublishRequest();
        }
    }

    private void PublishFrame(WinFormsFrameSink sink)
    {
        if (!ReferenceEquals(sink, _sink) || !sink.Publish())
            return;

        if (!_framesShown)
            return;

        Invalidate();
        // Paint now: Windows sends WM_PAINT only when no other message waits, so while a seek
        // drag (mouse moves) or another player's publishes keep the UI thread busy, frames
        // stayed unshown (owner's 045A smoke: seeking one player stalled the other). Publishes
        // are coalesced (latest frame wins), so this paints at most once per published frame.
        Update();
    }

    private void ShowPresentationWindow(bool show)
    {
        if (_presentationWindowShown == show)
            return;

        _presentationWindowShown = show;
        _host.Visible = show;
        Invalidate();
    }

    private void WatchPresentedFrames(IMediaControlSession? session)
    {
        if (_frameSession is not null)
            _frameSession.PresentationFramePresented -= OnPresentationFramePresented;

        Volatile.Write(ref _framePresentedSinceAttach, 0);
        Volatile.Write(ref _frameSession, session);
        if (session is not null)
            session.PresentationFramePresented += OnPresentationFramePresented;
    }

    // The render thread, once per presented frame: only the first after an attach (or after Audio
    // mode) asks the UI thread to show the child window.
    private void OnPresentationFramePresented(object? sender, EventArgs e)
    {
        if (sender is not null && !ReferenceEquals(sender, Volatile.Read(ref _frameSession)))
            return;

        if (!_contentTracker.HasPresentation || Interlocked.Exchange(ref _framePresentedSinceAttach, 1) != 0)
            return;

        OnContentTrackerChanged(this, EventArgs.Empty);
    }

    private string DescribeSurface() => _bookkeeping.Describe(EffectiveSurfaceId);

    private void SetPath(MediaSurfacePresentationPath path, string? reason)
    {
        if (_bookkeeping.SetPath(path, reason, _attachedSession, EffectiveSurfaceId))
            PresentationPathChanged?.Invoke(this, EventArgs.Empty);
    }

    // Raised on the thread that changed the state (often Core's render or event thread): marshal,
    // coalesce, then read the tracker's current state.
    private void OnContentTrackerChanged(object? sender, EventArgs e)
    {
        // Without a handle there is nothing to show yet (and no thread to marshal to);
        // OnHandleCreated applies the current state.
        if (!IsHandleCreated)
            return;

        if (!InvokeRequired)
        {
            UpdateContentState();
            return;
        }

        if (Interlocked.Exchange(ref _contentStateQueued, 1) != 0)
            return;

        try
        {
            _ = BeginInvoke(() =>
            {
                Volatile.Write(ref _contentStateQueued, 0);
                UpdateContentState();
            });
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            // The handle went away meanwhile; OnHandleCreated applies the state again.
            Volatile.Write(ref _contentStateQueued, 0);
        }
    }

    private void UpdateContentState()
    {
        if (IsDisposed)
            return;

        IMediaControlSession? session = _contentTracker.Session;
        var suspended = session is not null && !_contentTracker.HasPresentation;
        if (suspended != _isPresentationSuspended)
        {
            _isPresentationSuspended = suspended;
            if (suspended)
            {
                // The renderer that drew into the child is gone; its replacement presents anew.
                Volatile.Write(ref _framePresentedSinceAttach, 0);
                _sink?.Clear();
                DiagnosticRouter.Raise(
                    session,
                    MediaDiagnosticLevel.Info,
                    DiagnosticCategory,
                    MediaSurfacePresentationPathText.DescribeAudioModeCleared(DescribeSurface()));
            }
        }

        // Hiding the child window shows the background instead of the frame it last kept.
        var showsContent = _contentTracker.ShowsContent;
        ShowPresentationWindow(
            _attachedSession is not null
            && PresentationPath == MediaSurfacePresentationPath.NativeWindow
            && Volatile.Read(ref _framePresentedSinceAttach) != 0
            && showsContent);

        // The frame-sink path draws only while content shows; hidden content is dropped, so a
        // later load shows its own first frame, not this one (as the Avalonia surface does).
        var framesShown = _sink is not null && PresentationPath == MediaSurfacePresentationPath.FrameSink && showsContent;
        if (framesShown != _framesShown)
        {
            if (!framesShown)
                _sink?.Clear();
            _framesShown = framesShown;
            Invalidate();
        }

        var isContentVisible = _contentTracker.IsContentVisible;
        if (isContentVisible != _isContentVisible)
        {
            _isContentVisible = isContentVisible;
            ContentVisibilityChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    // The renderer may skip work during a live resize (as the WPF surface tells it from
    // WM_ENTERSIZEMOVE / WM_EXITSIZEMOVE): follow the parent form's resize loop.
    private void WatchFormResize() => WatchFormResize(FindForm());

    private void WatchFormResize(Form? form)
    {
        if (ReferenceEquals(form, _resizeForm))
            return;

        if (_resizeForm is not null)
        {
            _resizeForm.ResizeBegin -= OnFormResizeBegin;
            _resizeForm.ResizeEnd -= OnFormResizeEnd;
        }

        SetLiveResize(false);
        _resizeForm = form;
        if (form is not null)
        {
            form.ResizeBegin += OnFormResizeBegin;
            form.ResizeEnd += OnFormResizeEnd;
        }
    }

    private void OnFormResizeBegin(object? sender, EventArgs e) => SetLiveResize(true);

    private void OnFormResizeEnd(object? sender, EventArgs e)
    {
        SetLiveResize(false);
        AttachOrResize();
    }

    private void SetLiveResize(bool isResizing)
    {
        if (_isLiveResizing == isResizing)
            return;

        _isLiveResizing = isResizing;
        if (_attachedSession is { } session && _attachedSurfaceId is { } surfaceId)
            _ = session.SetPresentationSurfaceLiveResize(surfaceId, isResizing);
    }

    private void UpdateOverlay()
    {
        Control? content = _overlayContent;
        if (content is null && _overlayForm is null && !_overlayAnchorsWatched)
            return;

        // The top-level form: an MDI child or an embedded form cannot own a window.
        Form? owner = IsHandleCreated && !IsDisposed ? TopLevelControl as Form : null;
        if (content is null || owner is null)
        {
            CloseOverlay();
            if (_overlayAnchorsWatched)
                WatchOverlayAnchors(false);
            return;
        }

        if (!_overlayAnchorsWatched)
            WatchOverlayAnchors(true);

        if (_overlayForm is null || _overlayForm.IsDisposed || !ReferenceEquals(_overlayForm.Owner, owner))
        {
            CloseOverlay();
            _overlayForm = new MediaOverlayForm { Owner = owner };
            _overlayForm.FormClosed += OnOverlayFormClosed;
            _overlayForm.DpiChanged += OnOverlayDpiChanged;
        }

        MediaOverlayForm overlay = _overlayForm;
        overlay.SetHitTestVisible(_overlayHitTestVisible);
        overlay.SetContent(content);
        var show = Visible
            && owner.Visible
            && owner.WindowState != FormWindowState.Minimized
            && ClientSize.Width > 0
            && ClientSize.Height > 0;
        if (!show)
        {
            if (overlay.Visible)
                overlay.Hide();
            return;
        }

        Rectangle bounds = RectangleToScreen(ClientRectangle);
        if (overlay.Bounds != bounds)
            overlay.Bounds = bounds;
        if (!overlay.Visible)
            overlay.Show();
    }

    private void CloseOverlay()
    {
        MediaOverlayForm? overlay = _overlayForm;
        if (overlay is null)
            return;

        _overlayForm = null;
        overlay.FormClosed -= OnOverlayFormClosed;
        overlay.DpiChanged -= OnOverlayDpiChanged;
        overlay.SetContent(null);
        overlay.Dispose();
    }

    // Closed by its owner closing (or by the user): it disposes itself and hands the content back.
    private void OnOverlayFormClosed(object? sender, FormClosedEventArgs e)
    {
        if (sender is not MediaOverlayForm overlay || !ReferenceEquals(overlay, _overlayForm))
            return;

        overlay.FormClosed -= OnOverlayFormClosed;
        overlay.DpiChanged -= OnOverlayDpiChanged;
        _overlayForm = null;
    }

    // A move to a monitor of another DPI makes the form take Windows' suggested bounds after this
    // event: put it back over the surface once that is done.
    private void OnOverlayDpiChanged(object? sender, DpiChangedEventArgs e)
    {
        if (!IsHandleCreated || IsDisposed)
            return;

        try
        {
            _ = BeginInvoke(UpdateOverlay);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            // The handle went away; the overlay closes with it.
        }
    }

    private void RewatchOverlayAnchors()
    {
        if (_overlayAnchorsWatched)
            WatchOverlayAnchors(false);

        UpdateOverlay();
    }

    // The overlay window follows the surface on screen: a move of any ancestor (the form, a
    // panel whose row changed) moves the surface without a move of its own.
    private void WatchOverlayAnchors(bool watch)
    {
        foreach (Control anchor in _overlayAnchors)
        {
            anchor.Move -= OnOverlayAnchorChanged;
            anchor.Resize -= OnOverlayAnchorChanged;
            anchor.ParentChanged -= OnOverlayAnchorParentChanged;
        }

        _overlayAnchors.Clear();
        _overlayAnchorsWatched = watch;
        if (!watch)
            return;

        for (Control? anchor = Parent; anchor is not null; anchor = anchor.Parent)
        {
            anchor.Move += OnOverlayAnchorChanged;
            anchor.Resize += OnOverlayAnchorChanged;
            anchor.ParentChanged += OnOverlayAnchorParentChanged;
            _overlayAnchors.Add(anchor);
        }
    }

    private void OnOverlayAnchorChanged(object? sender, EventArgs e) => UpdateOverlay();

    private void OnOverlayAnchorParentChanged(object? sender, EventArgs e) => RewatchOverlayAnchors();

    /// <summary>
    /// The child window the renderer presents into. It never paints (the renderer owns its
    /// pixels), never takes focus, and passes mouse input to the <see cref="MediaSurface"/>.
    /// </summary>
    private sealed class PresentationHost : Control
    {
        private const int WmNcHitTest = 0x0084;
        private const int HtTransparent = -1;

        public PresentationHost()
        {
            Visible = false;
            Enabled = false;
            TabStop = false;
            BackColor = Color.Black;
            SetStyle(ControlStyles.Opaque | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint, true);
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.Selectable, false);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
        }

        protected override void OnPaintBackground(PaintEventArgs pevent)
        {
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WmNcHitTest)
            {
                m.Result = HtTransparent;
                return;
            }

            base.WndProc(ref m);
        }
    }
}
