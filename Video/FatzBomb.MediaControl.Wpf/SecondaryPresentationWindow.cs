using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace FatzBomb.MediaControl.Wpf;

/// <summary>
/// A window that shows a session on another screen: the singer or projector screen of a karaoke
/// host. It holds one <see cref="MediaSurface"/> with a non-primary <see cref="MediaSurface.Role"/>
/// (<see cref="MediaPresentationSurfaceRole.Projector"/> by default), so the session keeps its
/// primary surface and renders every frame for this window too, at this window's size. Closing
/// the window detaches the surface. Same shape as the Avalonia and WinForms adapters' windows
/// (Stage 046A): placement on a chosen <see cref="PresentationScreen"/> and full screen.
/// </summary>
/// <remarks>
/// F11 or a double-click toggles full screen. Escape leaves full screen, and in a window it
/// closes the window. A host that wants another
/// layout can put a <see cref="MediaSurface"/> with a non-primary role in any window instead.
/// </remarks>
public sealed class SecondaryPresentationWindow : Window
{
    /// <summary>The default size (DIPs) when the window is not full screen.</summary>
    public static readonly Size DefaultSize = new(960, 540);

    private readonly IMediaControlSession _session;
    private bool _isFullScreen;
    private WindowStyle _restoreWindowStyle;
    private ResizeMode _restoreResizeMode;
    private WindowState _restoreWindowState;

    /// <param name="session">The session to present from.</param>
    /// <param name="surfaceId">The secondary surface id; distinct per secondary window of one session.</param>
    /// <param name="role">The surface role.</param>
    /// <param name="presentationMode">
    /// Match the primary surface: a native HWND for swap-chain presentation, or a WPF-composited
    /// frame sink for software mode, which cannot draw into an HWND (ENG-016).
    /// </param>
    public SecondaryPresentationWindow(
        IMediaControlSession session,
        string surfaceId,
        MediaPresentationSurfaceRole role = MediaPresentationSurfaceRole.Projector,
        WpfPresentationMode presentationMode = WpfPresentationMode.NativeHwndOverlay)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        Surface = new MediaSurface
        {
            PresentationMode = presentationMode,
            Role = role,
            SurfaceId = string.IsNullOrWhiteSpace(surfaceId) ? MediaSurface.DefaultSecondarySurfaceId : surfaceId.Trim(),
        };

        Title = "FatzBombMedia Secondary Output";
        Width = DefaultSize.Width;
        Height = DefaultSize.Height;
        MinWidth = 320;
        MinHeight = 180;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Brushes.Black;
        Foreground = Brushes.White;
        Content = Surface;
        Closed += OnWindowClosed;
    }

    /// <summary>
    /// Gets the surface this window shows. It takes the session when the window gets its handle
    /// (shown), so a window that is never shown does not follow the session.
    /// </summary>
    public MediaSurface Surface { get; }

    /// <summary>Gets the surface id the window attaches with.</summary>
    public string SurfaceId => Surface.EffectiveSurfaceId;

    /// <summary>Gets or sets whether the window covers its whole screen (borderless, maximized).</summary>
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
                _restoreWindowStyle = WindowStyle;
                _restoreResizeMode = ResizeMode;
                _restoreWindowState = WindowState;
                WindowStyle = WindowStyle.None;
                ResizeMode = ResizeMode.NoResize;

                // Maximizing again after the style change makes the borderless window cover the
                // taskbar too.
                WindowState = WindowState.Normal;
                WindowState = WindowState.Maximized;
            }
            else
            {
                WindowStyle = _restoreWindowStyle;
                ResizeMode = _restoreResizeMode;
                WindowState = _restoreWindowState == WindowState.Minimized ? WindowState.Normal : _restoreWindowState;
            }
        }
    }

    /// <summary>
    /// Places the window on <paramref name="screen"/> (centred in its working area, or covering
    /// it when <paramref name="fullScreen"/>) and shows it, owned by <paramref name="owner"/> when
    /// given. Call it instead of <see cref="Window.Show"/>.
    /// </summary>
    public void ShowOnScreen(PresentationScreen? screen, bool fullScreen, Window? owner = null)
    {
        if (owner is not null)
            Owner = owner;

        if (screen is not null)
        {
            // Placed in device pixels before it shows, so it neither jumps nor attaches on another
            // monitor first. WPF rescales the window itself when that monitor's DPI differs.
            WindowStartupLocation = WindowStartupLocation.Manual;
            var hwnd = new WindowInteropHelper(this).EnsureHandle();
            if (NativeMethods.GetWindowRect(hwnd, out NativeMethods.RECT rect))
            {
                var width = (int)Math.Round(Width * screen.Scaling);
                var height = (int)Math.Round(Height * screen.Scaling);
                if (width <= 0 || height <= 0 || !double.IsFinite(Width) || !double.IsFinite(Height))
                    (width, height) = (rect.Right - rect.Left, rect.Bottom - rect.Top);

                (int x, int y) = PresentationScreen.CenteredPosition(screen.WorkingArea, width, height);
                _ = NativeMethods.SetWindowPos(
                    hwnd,
                    IntPtr.Zero,
                    x,
                    y,
                    0,
                    0,
                    NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
            }
        }

        if (fullScreen)
            IsFullScreen = true;

        Show();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // The mode was set before the player: a surface defers a mode change while media is open.
        Surface.Player ??= _session;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled)
            return;

        if (e.Key == Key.F11)
        {
            IsFullScreen = !IsFullScreen;
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            // Escape leaves full screen; in a window it closes the window (keyboard close).
            if (IsFullScreen)
                IsFullScreen = false;
            else
                Close();
            e.Handled = true;
        }
    }

    protected override void OnMouseDoubleClick(MouseButtonEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (e.Handled || e.ChangedButton != MouseButton.Left)
            return;

        IsFullScreen = !IsFullScreen;
        e.Handled = true;
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        Closed -= OnWindowClosed;

        // Detached before the window's resources go, so later presents skip this surface.
        Surface.Player = null;
    }
}
