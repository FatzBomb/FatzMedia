using FatzBomb.MediaControl.Core.Configuration;

namespace FatzBomb.MediaControl.Player.Avalonia;

/// <summary>
/// Command line: <c>[media-file] [--renderer=auto|software|vulkan|direct3d11] [--surface=auto|framesink] [--players=1..4]
/// [--mode=full|background|audio] [--second-screen=&lt;n&gt;] [--native=&lt;dir&gt;]...</c>
/// </summary>
internal sealed class StartupOptions
{
    public const int MaxPlayers = 4;

    public static StartupOptions Current { get; set; } = new();

    /// <summary>Gets the media file for the first player, when one was passed.</summary>
    public string? MediaPath { get; private init; }

    /// <summary>Gets the requested renderer (<c>auto</c> picks Vulkan when it is usable, else software).</summary>
    public string Renderer { get; private init; } = "software";

    public int Players { get; private init; } = 1;

    /// <summary>
    /// Gets whether surfaces may use the shared GPU image path (<c>--surface=auto</c>, the default)
    /// or always use the frame sink (<c>--surface=framesink</c>). Stage 027A slice 2.
    /// </summary>
    public bool PreferGpuSurface { get; private init; } = true;

    /// <summary>Gets the mode every player is created in (Stage 043A).</summary>
    public MediaPlayerMode Mode { get; private init; } = MediaPlayerMode.Full;

    /// <summary>
    /// Gets the screen (1-based, in Avalonia's <c>Screens.All</c> order) that player 1's second
    /// screen opens on, full screen, at startup (<c>--second-screen=&lt;n&gt;</c>, Stage 027A slice
    /// 5); null when not given. The toggles use it too.
    /// </summary>
    public int? SecondScreen { get; private init; }

    /// <summary>Gets the directories given with <c>--native=</c>, highest priority first.</summary>
    public IReadOnlyList<string> NativeDirectories { get; private init; } = [];

    public static StartupOptions Parse(IEnumerable<string> args)
    {
        string? media = null;
        var renderer = "software";
        var players = 1;
        var preferGpuSurface = true;
        var mode = MediaPlayerMode.Full;
        int? secondScreen = null;
        List<string> native = [];

        foreach (var argument in args)
        {
            if (TryValue(argument, "--renderer=", out var value))
                renderer = value.ToLowerInvariant();
            else if (TryValue(argument, "--surface=", out value))
                preferGpuSurface = !value.Equals("framesink", StringComparison.OrdinalIgnoreCase);
            else if (TryValue(argument, "--players=", out value) && int.TryParse(value, out var count))
                players = Math.Clamp(count, 1, MaxPlayers);
            else if (TryValue(argument, "--mode=", out value) && Enum.TryParse(value, ignoreCase: true, out MediaPlayerMode parsed)
                && Enum.IsDefined(parsed))
                mode = parsed;
            else if (TryValue(argument, "--second-screen=", out value) && int.TryParse(value, out var screen) && screen > 0)
                secondScreen = screen;
            else if (TryValue(argument, "--native=", out value) && value.Length > 0)
                native.Add(value);
            else if (!argument.StartsWith("--", StringComparison.Ordinal))
                media ??= argument;
        }

        return new StartupOptions
        {
            MediaPath = media,
            Renderer = renderer,
            Players = players,
            PreferGpuSurface = preferGpuSurface,
            Mode = mode,
            SecondScreen = secondScreen,
            NativeDirectories = native
        };
    }

    private static bool TryValue(string argument, string prefix, out string value)
    {
        if (argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            value = argument[prefix.Length..].Trim('"');
            return true;
        }

        value = string.Empty;
        return false;
    }
}
