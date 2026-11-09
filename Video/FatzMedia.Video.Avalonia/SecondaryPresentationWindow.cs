using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform;

namespace FatzMedia.Video.Avalonia;

/// <summary>
/// A window that shows a session on another screen (Stage 027A slice 5): the singer or projector
/// screen of a karaoke host. It holds one <see cref="MediaSurface"/> with the
/// <see cref="MediaPresentationSurfaceRole.Projector"/> role, so the session keeps its primary
/// surface and renders every frame for this window too, at this window's size. Closing the window
/// detaches the surface. Mirrors the WPF adapter's <c>SecondaryPresentationWindow</c>; it adds
/// placement on a chosen <see cref="Screen"/> and full screen.
/// </summary>
/// <remarks>
/// F11 or a double-click toggles full screen. Escape leaves full screen, and in a window it
/// closes the window. A host that wants another layout can put a <see cref="MediaSurface"/> with a
/// non-primary <see cref="MediaSurface.Role"/> in any window instead.
/// </remarks>
public class SecondaryPresentationWindow : Window
{
    /// <summary>The default size (DIPs) when the window is not full screen.</summary>
    public static readonly Size DefaultSize = new(960, 540);

    /// <param name="session">The session to present.</param>
    /// <param name="surfaceId">
    /// The surface id; distinct per secondary window of one session. Defaults to
    /// <see cref="MediaSurface.DefaultSecondarySurfaceId"/>.
    /// </param>
    public SecondaryPresentationWindow(IMediaControlSession session, string? surfaceId = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        Surface = new MediaSurface
        {
            Role = MediaPresentationSurfaceRole.Projector,
            SurfaceId = string.IsNullOrWhiteSpace(surfaceId) ? MediaSurface.DefaultSecondarySurfaceId : surfaceId.Trim(),
            Player = session,
        };

        Title = "FatzMedia.Video Secondary Output";
        Width = DefaultSize.Width;
        Height = DefaultSize.Height;
        MinWidth = 320;
        MinHeight = 180;
        Background = Brushes.Black;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Content = Surface;
    }

    /// <summary>Gets the surface this window shows.</summary>
    public MediaSurface Surface { get; }

    /// <summary>Gets or sets whether the window covers its whole screen.</summary>
    public bool IsFullScreen
    {
        get => WindowState == WindowState.FullScreen;
        set => WindowState = value ? WindowState.FullScreen : WindowState.Normal;
    }

    /// <summary>
    /// Places the window on <paramref name="screen"/> (centred at <see cref="DefaultSize"/>, or
    /// covering it when <paramref name="fullScreen"/>) and shows it, owned by
    /// <paramref name="owner"/> when given. Call it instead of <see cref="Window.Show()"/>.
    /// </summary>
    public void ShowOnScreen(Screen? screen, bool fullScreen, Window? owner = null)
    {
        if (screen is not null)
        {
            // Placed before it shows, so it neither jumps nor attaches at another size first.
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = CenteredPosition(screen.WorkingArea, screen.Scaling, new Size(Width, Height));
        }

        if (fullScreen)
            WindowState = WindowState.FullScreen;

        if (owner is null)
            Show();
        else
            Show(owner);
    }

    /// <summary>The top-left pixel position that centres a window of <paramref name="size"/> DIPs in <paramref name="area"/>.</summary>
    internal static PixelPoint CenteredPosition(PixelRect area, double scaling, Size size)
    {
        if (!double.IsFinite(scaling) || scaling <= 0)
            scaling = 1;

        (int x, int y) = PresentationPlacement.Center(
            area.X, area.Y, area.Width, area.Height, (int)Math.Round(size.Width * scaling), (int)Math.Round(size.Height * scaling));
        return new PixelPoint(x, y);
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

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.Handled && e.ClickCount == 2)
        {
            IsFullScreen = !IsFullScreen;
            e.Handled = true;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        // Detach before the window's resources go, so later presents skip this surface.
        Surface.Player = null;
        base.OnClosed(e);
    }
}
