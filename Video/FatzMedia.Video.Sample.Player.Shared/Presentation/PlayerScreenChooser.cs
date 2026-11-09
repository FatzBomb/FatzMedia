using System.Globalization;

namespace FatzMedia.Video.Sample.Player.Shared.Presentation;

/// <summary>A rectangle in the host's screen coordinates (device pixels on every host).</summary>
public readonly record struct PlayerScreenBounds(int X, int Y, int Width, int Height)
{
    /// <summary>Gets the area <paramref name="other"/> shares with this rectangle (0 when they do not meet).</summary>
    public long OverlapArea(PlayerScreenBounds other)
    {
        var width = Math.Min(X + (long)Width, other.X + (long)other.Width) - Math.Max(X, other.X);
        var height = Math.Min(Y + (long)Height, other.Y + (long)other.Height) - Math.Max(Y, other.Y);
        return width > 0 && height > 0 ? width * height : 0;
    }
}

/// <summary>One screen as a host enumerates it (WPF <c>PresentationScreen</c>, Avalonia <c>Screens</c>, WinForms <c>Screen.AllScreens</c>).</summary>
/// <param name="Name">The screen's name, or empty when the platform has none.</param>
/// <param name="Bounds">The screen's rectangle.</param>
/// <param name="IsPrimary">Whether it is the primary screen.</param>
public sealed record PlayerScreen(string Name, PlayerScreenBounds Bounds, bool IsPrimary);

/// <summary>Where a second screen opens.</summary>
/// <param name="ScreenIndex">The 0-based screen, or null for the host's default placement (no screens known).</param>
/// <param name="FullScreen">Whether the window covers that screen.</param>
/// <param name="Where">A log line ("full screen on screen 2 of 2 (...)").</param>
public sealed record PlayerScreenChoice(int? ScreenIndex, bool FullScreen, string Where);

/// <summary>
/// The second-screen placement rule every test player shares (Stage 046A slice 3), over the
/// screens' rectangles: <c>--second-screen=&lt;n&gt;</c> when that screen exists, else the first
/// screen that is not the main window's, full screen; with one screen, a window beside the player
/// on the main window's screen.
/// </summary>
public static class PlayerScreenChooser
{
    /// <param name="screens">Every screen, in the host's order (the numbers of <c>--second-screen=</c>).</param>
    /// <param name="ownerBounds">The main window's rectangle (or its screen's), or null when unknown.</param>
    /// <param name="requested">The 1-based screen asked for (<c>--second-screen=</c>), or null.</param>
    public static PlayerScreenChoice Choose(IReadOnlyList<PlayerScreen> screens, PlayerScreenBounds? ownerBounds, int? requested)
    {
        ArgumentNullException.ThrowIfNull(screens);
        if (requested is { } number && number >= 1 && number <= screens.Count)
            return FullScreenOn(screens, number - 1);

        var current = OwnerScreen(screens, ownerBounds);
        for (var i = 0; i < screens.Count; i++)
        {
            if (i != current)
                return FullScreenOn(screens, i);
        }

        var missing = requested is { } absent ? $"; screen {absent} does not exist" : string.Empty;
        return new PlayerScreenChoice(current, FullScreen: false, $"in a window (one screen{missing})");
    }

    /// <summary>
    /// Gets the screen that holds most of <paramref name="ownerBounds"/>; with no overlap (or no
    /// bounds) the primary screen, else the first; null when there are no screens.
    /// </summary>
    public static int? OwnerScreen(IReadOnlyList<PlayerScreen> screens, PlayerScreenBounds? ownerBounds)
    {
        ArgumentNullException.ThrowIfNull(screens);
        if (screens.Count == 0)
            return null;

        var best = -1;
        long bestArea = 0;
        if (ownerBounds is { } bounds)
        {
            for (var i = 0; i < screens.Count; i++)
            {
                var area = screens[i].Bounds.OverlapArea(bounds);
                if (area > bestArea)
                {
                    best = i;
                    bestArea = area;
                }
            }
        }

        if (best >= 0)
            return best;

        for (var i = 0; i < screens.Count; i++)
        {
            if (screens[i].IsPrimary)
                return i;
        }

        return 0;
    }

    /// <summary>Gets the screen's name, or its size and place when the platform gives no name (Avalonia on Windows).</summary>
    public static string Describe(PlayerScreen screen)
    {
        ArgumentNullException.ThrowIfNull(screen);
        return string.IsNullOrWhiteSpace(screen.Name)
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"{screen.Bounds.Width}x{screen.Bounds.Height} at {screen.Bounds.X},{screen.Bounds.Y}{(screen.IsPrimary ? ", primary" : string.Empty)}")
            : screen.Name;
    }

    static PlayerScreenChoice FullScreenOn(IReadOnlyList<PlayerScreen> screens, int index) =>
        new(index, FullScreen: true, $"full screen on screen {index + 1} of {screens.Count} ({Describe(screens[index])})");
}
