using System.ComponentModel;

namespace FatzMedia.Video.WinForms;

/// <summary>
/// A window that shows a session on another screen (Stage 046A): the singer or projector screen
/// of a karaoke host. It holds one <see cref="MediaSurface"/> with the
/// <see cref="MediaPresentationSurfaceRole.Projector"/> role, so the session keeps its primary
/// surface and renders every frame for this window too, at this window's size. Closing the window
/// detaches the surface. Same shape as the Avalonia and WPF adapters' windows: placement on a
/// chosen <see cref="Screen"/> and full screen.
/// </summary>
/// <remarks>
/// F11 or a double-click toggles full screen. Escape leaves full screen, and in a window it
/// closes the window. A host that wants another
/// layout can put a <see cref="MediaSurface"/> with a non-primary <see cref="MediaSurface.Role"/>
/// in any form instead.
/// </remarks>
public class SecondaryPresentationWindow : Form
{
    /// <summary>The default client size, in pixels, when the window is not full screen.</summary>
    public static readonly Size DefaultClientSize = new(960, 540);

    readonly IPlayerSession _session;
    bool _isFullScreen;
    bool _closed;
    FormBorderStyle _restoreBorderStyle;
    FormWindowState _restoreWindowState;

    /// <param name="session">The session to present.</param>
    /// <param name="surfaceId">
    /// The surface id; distinct per secondary window of one session. Defaults to
    /// <see cref="MediaSurface.DefaultSecondarySurfaceId"/>.
    /// </param>
    public SecondaryPresentationWindow(IPlayerSession session, string? surfaceId = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        Surface = new MediaSurface
        {
            Dock = DockStyle.Fill,
            Role = MediaPresentationSurfaceRole.Projector,
            SurfaceId = string.IsNullOrWhiteSpace(surfaceId) ? MediaSurface.DefaultSecondarySurfaceId : surfaceId.Trim(),
        };

        Text = "FatzMedia.Video Secondary Output";
        BackColor = Color.Black;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = DefaultClientSize;
        MinimumSize = new Size(320, 180);
        KeyPreview = true;
        Controls.Add(Surface);
        Surface.MouseDoubleClick += OnSurfaceMouseDoubleClick;
    }

    /// <summary>
    /// Gets the surface this window shows. It takes the session when the window gets its handle
    /// (shown), so a window that is never shown does not follow the session.
    /// </summary>
    [Browsable(false)]
    public MediaSurface Surface { get; }

    /// <summary>Gets or sets whether the window covers its whole screen (borderless, maximized).</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    [Browsable(false)]
    public bool IsFullScreen
    {
        get => _isFullScreen;
        set
        {
            if (_isFullScreen == value)
                return;

            _isFullScreen = value;
            if (value)
            {
                _restoreBorderStyle = FormBorderStyle;
                _restoreWindowState = WindowState;
                FormBorderStyle = FormBorderStyle.None;

                // Maximizing again after the border change makes the window cover the taskbar.
                WindowState = FormWindowState.Normal;
                WindowState = FormWindowState.Maximized;
            }
            else
            {
                FormBorderStyle = _restoreBorderStyle;
                WindowState = _restoreWindowState == FormWindowState.Minimized ? FormWindowState.Normal : _restoreWindowState;
            }
        }
    }

    /// <summary>
    /// Places the window on <paramref name="screen"/> (centred in its working area, or covering
    /// it when <paramref name="fullScreen"/>) and shows it, owned by <paramref name="owner"/> when
    /// given. Call it instead of <see cref="Control.Show"/>.
    /// </summary>
    public void ShowOnScreen(Screen? screen, bool fullScreen, IWin32Window? owner = null)
    {
        if (screen is not null)
        {
            // Placed before it shows, so it neither jumps nor attaches on another screen first. A
            // per-monitor DPI-aware process rescales the window when that screen's DPI differs.
            StartPosition = FormStartPosition.Manual;
            Location = CenteredLocation(screen.WorkingArea, Size);
        }

        if (owner is null)
            Show();
        else
            Show(owner);

        // Maximized only once the window exists on that screen: maximizing before it shows let the
        // per-monitor DPI rescale move the maximized window, so it covered only part of a screen
        // whose scale differs from the main one (owner's 046A smoke).
        if (fullScreen)
            IsFullScreen = true;
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        if (!_isFullScreen || WindowState != FormWindowState.Maximized)
            return;

        // Windows applied its suggested bounds for the new scale: cover the screen again.
        WindowState = FormWindowState.Normal;
        WindowState = FormWindowState.Maximized;
    }

    /// <summary>The top-left position that centres a window of <paramref name="size"/> in <paramref name="area"/> (clamped to it).</summary>
    internal static Point CenteredLocation(Rectangle area, Size size)
    {
        (var x, var y) = PresentationPlacement.Center(area.X, area.Y, area.Width, area.Height, size.Width, size.Height);
        return new Point(x, y);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (!IsDisposed && Surface.Player is null && !_closed)
            Surface.Player = _session;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled)
            return;

        if (e.KeyCode == Keys.F11)
        {
            IsFullScreen = !IsFullScreen;
            e.Handled = true;
        }
        else if (e.KeyCode == Keys.Escape)
        {
            // Escape leaves full screen; in a window it closes the window (keyboard close).
            if (IsFullScreen)
                IsFullScreen = false;
            else
                Close();
            e.Handled = true;
        }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        // Detach before the window's handles go, so later presents skip this surface.
        _closed = true;
        Surface.MouseDoubleClick -= OnSurfaceMouseDoubleClick;
        Surface.Player = null;
        base.OnFormClosed(e);
    }

    void OnSurfaceMouseDoubleClick(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
            IsFullScreen = !IsFullScreen;
    }
}
