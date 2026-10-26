using System.ComponentModel;

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

    private static int s_nextSurfaceNumber;

    private readonly int _surfaceNumber = Interlocked.Increment(ref s_nextSurfaceNumber);
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
    private bool _attachRefusalReported;
    private int _contentStateQueued;
    private bool _isContentVisible;
    private bool _isPresentationSuspended;
    private bool _presentationWindowShown;
    private IMediaControlSession? _frameSession;
    private int _framePresentedSinceAttach;

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
            _attachRefusalReported = false;
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
    /// while attached, <see cref="MediaSurfacePresentationPath.None"/> otherwise.
    /// </summary>
    [Browsable(false)]
    public MediaSurfacePresentationPath PresentationPath { get; private set; }

    /// <summary>Gets why the current path was chosen.</summary>
    [Browsable(false)]
    public string? PresentationPathReason { get; private set; }

    /// <summary>Gets the id this surface attaches with (<see cref="SurfaceId"/> or the role's default).</summary>
    [Browsable(false)]
    public string EffectiveSurfaceId => string.IsNullOrWhiteSpace(_surfaceId)
        ? _role == MediaPresentationSurfaceRole.Primary ? MediaPresentationSurfaceDescriptor.PrimarySurfaceId : DefaultSecondarySurfaceId
        : _surfaceId.Trim();

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

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);

        // A callback queued on the previous handle is dropped when that handle goes, so the
        // coalescing flag would stay set and block every later background update.
        Volatile.Write(ref _contentStateQueued, 0);
        WatchFormResize();
        AttachOrResize();
        UpdateContentState();
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        Detach();
        WatchFormResize(null);
        base.OnHandleDestroyed(e);
    }

    protected override void OnParentChanged(EventArgs e)
    {
        base.OnParentChanged(e);
        if (IsHandleCreated)
            WatchFormResize();
    }

    protected override void OnClientSizeChanged(EventArgs e)
    {
        base.OnClientSizeChanged(e);
        AttachOrResize();
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        AttachOrResize();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        // The child window is clipped out while it shows; this paints the background around and
        // instead of it.
        using var brush = new SolidBrush(BackColor);
        e.Graphics.FillRectangle(brush, ClientRectangle);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Detach();
            WatchFormResize(null);
            _contentTracker.Changed -= OnContentTrackerChanged;
            _contentTracker.Dispose();
        }

        base.Dispose(disposing);
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

        // The child window has no pixels of its own until the renderer presents into it, so it
        // stays hidden (the control paints BackColor) until a frame arrives, whatever AutoHide
        // says. Listen before attaching: the attach itself presents the current frame.
        WatchPresentedFrames(session);
        var attached = session.AttachPresentationSurface(new MediaPresentationSurfaceDescriptor(
            surfaceId,
            _role,
            MediaNativeSurfaceDescriptor.Win32Hwnd(_host.Handle),
            pixelSize.Width,
            pixelSize.Height,
            scale,
            scale));
        if (attached)
        {
            _attachedSession = session;
            _attachedSurfaceId = surfaceId;
            _attachedPixelSize = pixelSize;
            _attachRefusalReported = false;
            if (_isLiveResizing)
                _ = session.SetPresentationSurfaceLiveResize(surfaceId, true);
            SetPath(MediaSurfacePresentationPath.NativeWindow, "the renderer presents into the control's child window");
        }
        else
        {
            WatchPresentedFrames(null);
        }

        if (!attached && !_attachRefusalReported)
        {
            // Layout retries the attach on every resize: report a refusal once per session.
            _attachRefusalReported = true;
            DiagnosticRouter.Raise(
                session,
                MediaDiagnosticLevel.Warning,
                DiagnosticCategory,
                MediaSurfacePresentationPathText.DescribeAttachRefused(DescribeSurface()));
        }

        UpdateContentState();
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

        WatchPresentedFrames(null);
        SetPath(MediaSurfacePresentationPath.None, null);
        ShowPresentationWindow(false);
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

    private string DescribeSurface() => $"Surface #{_surfaceNumber} '{EffectiveSurfaceId}'";

    private void SetPath(MediaSurfacePresentationPath path, string? reason)
    {
        if (path == PresentationPath && reason == PresentationPathReason)
            return;

        PresentationPath = path;
        PresentationPathReason = reason;
        if (path != MediaSurfacePresentationPath.None)
        {
            DiagnosticRouter.Raise(
                _attachedSession,
                MediaDiagnosticLevel.Info,
                DiagnosticCategory,
                MediaSurfacePresentationPathText.DescribeChoice(DescribeSurface(), path, reason));
        }

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
                DiagnosticRouter.Raise(
                    session,
                    MediaDiagnosticLevel.Info,
                    DiagnosticCategory,
                    MediaSurfacePresentationPathText.DescribeAudioModeCleared(DescribeSurface()));
            }
        }

        // Hiding the child window shows the background instead of the frame it last kept.
        ShowPresentationWindow(
            _attachedSession is not null
            && Volatile.Read(ref _framePresentedSinceAttach) != 0
            && _contentTracker.ShowsContent);

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
