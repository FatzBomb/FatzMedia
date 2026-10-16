using System.ComponentModel;

namespace FatzBomb.MediaControl.WinForms;

/// <summary>
/// WinForms media surface: a child window whose HWND is attached to the bound
/// <see cref="IMediaControlSession"/> as the primary presentation surface. The host-selected
/// renderer (for example Direct3D11) presents into it; the control owns only HWND lifecycle,
/// size, and DPI.
/// </summary>
public class MediaSurface : Control
{
    private IMediaControlSession? _player;
    private IMediaControlSession? _attachedSession;
    private (int Width, int Height) _attachedPixelSize;
    private VideoDisplayMode _videoDisplayMode = VideoDisplayMode.Zoom;

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
            if (value is not null)
                value.VideoDisplayMode = _videoDisplayMode;
            AttachOrResize();
        }
    }

    /// <summary>Gets or sets how primary video maps into the surface (forwarded to the session).</summary>
    [DefaultValue(VideoDisplayMode.Zoom)]
    public VideoDisplayMode VideoDisplayMode
    {
        get => _videoDisplayMode;
        set
        {
            _videoDisplayMode = value;
            if (_player is not null)
                _player.VideoDisplayMode = value;
        }
    }

    /// <summary>Gets whether the primary surface is attached to a session.</summary>
    [Browsable(false)]
    public bool IsPrimarySurfaceAttached => _attachedSession is not null;

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
                && session.ResizePresentationSurface(MediaPresentationSurfaceDescriptor.PrimarySurfaceId, pixelSize.Width, pixelSize.Height))
            {
                _attachedPixelSize = pixelSize;
            }

            return;
        }

        Detach();
        var scale = DeviceDpi / 96.0;
        var attached = session.AttachPresentationSurface(new MediaPresentationSurfaceDescriptor(
            MediaPresentationSurfaceDescriptor.PrimarySurfaceId,
            MediaPresentationSurfaceRole.Primary,
            MediaNativeSurfaceDescriptor.Win32Hwnd(Handle),
            pixelSize.Width,
            pixelSize.Height,
            scale,
            scale));
        if (attached)
        {
            _attachedSession = session;
            _attachedPixelSize = pixelSize;
        }
    }

    private void Detach()
    {
        IMediaControlSession? session = _attachedSession;
        _attachedSession = null;
        _attachedPixelSize = default;
        if (session is not null)
            _ = session.DetachPresentationSurface(MediaPresentationSurfaceDescriptor.PrimarySurfaceId);
    }
}
