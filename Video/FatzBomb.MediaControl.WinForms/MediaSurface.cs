using System.ComponentModel;

namespace FatzBomb.MediaControl.WinForms;

/// <summary>
/// WinForms media surface: a child window whose HWND is attached to the bound
/// <see cref="IMediaControlSession"/> as a presentation surface, the primary one by default (a
/// second screen sets <see cref="Role"/>). The host-selected renderer (for example Direct3D11)
/// presents into it; the control owns only HWND lifecycle, size, and DPI.
/// </summary>
public class MediaSurface : Control
{
    /// <summary>The surface id a non-primary surface uses when <see cref="SurfaceId"/> is not set.</summary>
    public const string DefaultSecondarySurfaceId = "secondary";

    private IMediaControlSession? _player;
    private IMediaControlSession? _attachedSession;
    private string? _attachedSurfaceId;
    private (int Width, int Height) _attachedPixelSize;
    private VideoDisplayMode _videoDisplayMode = VideoDisplayMode.Zoom;
    private MediaPresentationSurfaceRole _role = MediaPresentationSurfaceRole.Primary;
    private string? _surfaceId;

    public MediaSurface()
    {
        BackColor = Color.Black;
        // The renderer owns every pixel; WinForms must not paint or erase over it.
        SetStyle(ControlStyles.Opaque | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint, true);
        SetStyle(ControlStyles.OptimizedDoubleBuffer, false);
    }

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
            if (value is not null && _role == MediaPresentationSurfaceRole.Primary)
                value.VideoDisplayMode = _videoDisplayMode;
            AttachOrResize();
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

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        AttachOrResize();
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        Detach();
        base.OnHandleDestroyed(e);
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
        // Until the first frame is presented, show the background; afterwards the renderer
        // repaints the HWND itself.
        if (_attachedSession is null)
        {
            using var brush = new SolidBrush(BackColor);
            e.Graphics.FillRectangle(brush, ClientRectangle);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            Detach();
        base.Dispose(disposing);
    }

    private void AttachOrResize()
    {
        IMediaControlSession? session = _player;
        if (session is null || !IsHandleCreated || ClientSize.Width <= 0 || ClientSize.Height <= 0)
            return;

        // ClientSize is already in device pixels for a DPI-aware WinForms process.
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
        var attached = session.AttachPresentationSurface(new MediaPresentationSurfaceDescriptor(
            surfaceId,
            _role,
            MediaNativeSurfaceDescriptor.Win32Hwnd(Handle),
            pixelSize.Width,
            pixelSize.Height,
            scale,
            scale));
        if (attached)
        {
            _attachedSession = session;
            _attachedSurfaceId = surfaceId;
            _attachedPixelSize = pixelSize;
        }
    }

    private void Detach()
    {
        IMediaControlSession? session = _attachedSession;
        var surfaceId = _attachedSurfaceId;
        _attachedSession = null;
        _attachedSurfaceId = null;
        _attachedPixelSize = default;
        if (session is not null && surfaceId is not null)
            _ = session.DetachPresentationSurface(surfaceId);
    }
}
