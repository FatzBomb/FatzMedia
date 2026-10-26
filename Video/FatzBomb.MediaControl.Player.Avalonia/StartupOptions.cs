using FatzBomb.MediaControl.Player.Shared;

namespace FatzBomb.MediaControl.Player.Avalonia;

/// <summary>
/// Command line: the flags every test player shares (<see cref="PlayerStartupOptions"/>: media file
/// or http(s) URL, <c>--url=</c>, <c>--mode=</c>, <c>--native=</c>, <c>--net-*</c>, <c>--eq=</c>, ...)
/// plus this player's own <c>[--renderer=auto|software|vulkan|direct3d11] [--software]
/// [--surface=auto|framesink] [--decoding=auto|software] [--players=1..4] [--second-screen=&lt;n&gt;]</c>.
/// </summary>
internal sealed class StartupOptions
{
    public const int MaxPlayers = 4;

    public static StartupOptions Current { get; set; } = new();

    /// <summary>Gets the options shared with the other test players. The startup media goes to the first player only.</summary>
    public PlayerStartupOptions Shared { get; private init; } = new();

    /// <summary>
    /// Gets the requested renderer (<c>auto</c> picks Vulkan when it is usable, else software;
    /// <c>--software</c> is <c>software</c>); software when not given.
    /// </summary>
    public string Renderer { get; private init; } = "software";

    public int Players { get; private init; } = 1;

    /// <summary>
    /// Gets whether surfaces may use the shared GPU image path (<c>--surface=auto</c>, the default)
    /// or always use the frame sink (<c>--surface=framesink</c>). Stage 027A slice 2.
    /// </summary>
    public bool PreferGpuSurface { get; private init; } = true;

    /// <summary>
    /// Gets whether video is decoded in software for every renderer (<c>--decoding=software</c>), for
    /// CPU comparisons; otherwise (<c>--decoding=auto</c>, the default) each renderer decodes as
    /// <see cref="Renderers.ChooseDecoding"/> says. Stage 045A.
    /// </summary>
    public bool SoftwareDecoding { get; private init; }

    /// <summary>
    /// Gets the screen (1-based, in Avalonia's <c>Screens.All</c> order) that player 1's second
    /// screen opens on, full screen, at startup (<c>--second-screen=&lt;n&gt;</c>, Stage 027A slice
    /// 5); null when not given. The toggles use it too.
    /// </summary>
    public int? SecondScreen { get; private init; }

    public static StartupOptions Parse(IReadOnlyList<string> args)
    {
        PlayerStartupOptions shared = PlayerStartupOptions.Parse(args);
        // --software asks for the software renderer, which is already the default here.
        var renderer = shared.Renderer?.ToLowerInvariant() ?? "software";
        var players = PlayerStartupOptions.Values(args, "--players=")
            .Select(static value => int.TryParse(value, out var count) ? Math.Clamp(count, 1, MaxPlayers) : (int?)null)
            .LastOrDefault(static count => count is not null) ?? 1;
        var surface = PlayerStartupOptions.Values(args, "--surface=").LastOrDefault();
        var decoding = PlayerStartupOptions.Values(args, "--decoding=").LastOrDefault();
        var secondScreen = PlayerStartupOptions.Values(args, "--second-screen=")
            .Select(static value => int.TryParse(value, out var screen) && screen > 0 ? screen : (int?)null)
            .LastOrDefault(static screen => screen is not null);

        return new StartupOptions
        {
            Shared = shared,
            Renderer = renderer,
            Players = players,
            PreferGpuSurface = surface is null || !surface.Equals("framesink", StringComparison.OrdinalIgnoreCase),
            SoftwareDecoding = decoding is not null && decoding.Equals("software", StringComparison.OrdinalIgnoreCase),
            SecondScreen = secondScreen,
        };
    }

    /// <summary>Gets the shared options for player <paramref name="index"/> (1-based): only player 1 gets the startup media.</summary>
    public PlayerStartupOptions ForPlayer(int index) => index == 1 ? Shared : Shared.WithoutStartupMedia();
}
