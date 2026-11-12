using System.Globalization;
using FatzMedia.Video.Core.Configuration;
using FatzMedia.Video.Sample.Player.Shared.ViewModels;

namespace FatzMedia.Video.Sample.Player.Shared;

/// <summary>
/// The command-line options every test player understands. Host-only flags are parsed by the host
/// from the same arguments; flags unknown here are ignored.
/// </summary>
public sealed record PlayerStartupOptions
{
    /// <summary>The engine clamps fade lengths to 60 s; clamped here too so <c>TimeSpan.FromSeconds</c> cannot throw.</summary>
    public const double MaxFadeNextSeconds = 60;

    /// <summary>
    /// Gets the first argument that is not a flag, when it is not an http/https URL and no
    /// <c>--url=</c> is given: a media file to load at startup.
    /// </summary>
    public string? MediaPath { get; init; }

    /// <summary>
    /// Gets the network source to load at startup: the first http/https <c>--url=</c>, else the
    /// first argument that is not a flag when it is an http/https URL (Stage 039A).
    /// </summary>
    public Uri? NetworkUri { get; init; }

    /// <summary>Gets the mode the player is created in (<c>--mode=full|background|audio</c>, Stage 043A).</summary>
    public MediaPlayerMode Mode { get; init; } = MediaPlayerMode.Full;

    /// <summary>Gets the native library directories given with <c>--native=</c> (repeatable), highest priority first.</summary>
    public IReadOnlyList<string> NativeDirectories { get; init; } = [];

    /// <summary>
    /// Gets the last <c>--renderer=</c> value as given (trimmed of quotes), or null. Each host
    /// interprets it against the renderers it has.
    /// </summary>
    public string? Renderer { get; init; }

    /// <summary>Gets whether <c>--software</c> was given (the software renderer, when <c>--renderer=</c> is absent or invalid).</summary>
    public bool SoftwareRenderer { get; init; }

    /// <summary>
    /// Gets the renderer asked for (<see cref="PlayerRendererRules.Resolve"/> over
    /// <see cref="Renderer"/> and <see cref="SoftwareRenderer"/>); <see cref="PlayerRendererRequest.Auto"/>
    /// (GPU first) by default. Stage 046A slice 3.
    /// </summary>
    public PlayerRendererRequest RendererRequest => PlayerRendererRules.Resolve(Renderer, SoftwareRenderer).Request;

    /// <summary>Gets a <c>--renderer=</c> value that is not a renderer name (then <see cref="RendererRequest"/> applies), or null.</summary>
    public string? UnknownRenderer => PlayerRendererRules.Resolve(Renderer, SoftwareRenderer).UnknownValue;

    /// <summary>Gets how many independent players the window shows (<c>--players=1..4</c>, clamped); 1 by default.</summary>
    public int Players { get; init; } = 1;

    /// <summary>
    /// Gets whether surfaces always use the frame sink (<c>--surface=framesink</c>) instead of the
    /// adapter's direct path (<c>--surface=auto</c>, the default): WPF <c>WpfComposited</c> instead
    /// of the native window, Avalonia <c>PreferGpuSharedSurface = false</c>, WinForms
    /// <c>PreferNativeWindow = false</c>. For comparisons.
    /// </summary>
    public bool PreferFrameSinkSurface { get; init; }

    /// <summary>Gets a <c>--surface=</c> value that is neither <c>auto</c> nor <c>framesink</c> (then auto applies), or null.</summary>
    public string? UnknownSurface { get; init; }

    /// <summary>
    /// Gets whether video is decoded in software whatever the renderer could take
    /// (<c>--decoding=software</c>), for CPU comparisons; otherwise (<c>--decoding=auto</c>, the
    /// default) each renderer decodes as it says (Stage 045A). See <see cref="ApplyDecoding"/>.
    /// </summary>
    public bool SoftwareDecoding { get; init; }

    /// <summary>Gets a <c>--decoding=</c> value that is neither <c>auto</c> nor <c>software</c> (then auto applies), or null.</summary>
    public string? UnknownDecoding { get; init; }

    /// <summary>
    /// Gets the screen (1-based, in the host's screen order) player 1's second screen opens on,
    /// full screen, at startup (<c>--second-screen=&lt;n&gt;</c>); null when not given. The second
    /// screen toggles use it too (see <see cref="Presentation.PlayerScreenChooser"/>).
    /// </summary>
    public int? SecondScreen { get; init; }

    /// <summary>Gets whether pitch resets after each load (<c>--reset-pitch-on-load</c>).</summary>
    public bool ResetPitchOnLoad { get; init; }

    /// <summary>Gets whether tempo resets after each load (<c>--reset-tempo-on-load</c>).</summary>
    public bool ResetTempoOnLoad { get; init; }

    /// <summary>Gets whether the visual transforms start at a demo setting (<c>--visual-transform-demo</c>).</summary>
    public bool VisualTransformDemo { get; init; }

    /// <summary>Gets whether leading and trailing silence is trimmed (<c>--trim-silence</c>).</summary>
    public bool TrimSilence { get; init; }

    /// <summary>
    /// Gets whether a background video follows the song's transport (<c>--background-follows-media</c>,
    /// <see cref="MediaPlayerConfiguration.BackgroundFollowsMedia"/>); by default it loops on its own.
    /// </summary>
    public bool BackgroundFollowsMedia { get; init; }

    /// <summary>Gets whether HDR video is shown without tone mapping (<c>--no-hdr-tonemap</c>), for comparison.</summary>
    public bool DisableHdrToneMapping { get; init; }

    /// <summary>Gets whether HDR video is tone mapped on the CPU (<c>--hdr-tonemap-cpu</c>), for comparison.</summary>
    public bool PreferCpuHdrToneMapping { get; init; }

    /// <summary>
    /// Gets whether HDR video is shown in HDR on an HDR display (<c>--hdr-output</c>, Direct3D11
    /// native window surfaces; stage 042A slice 2).
    /// </summary>
    public bool HdrOutput { get; init; }

    /// <summary>Gets the item to prepare as next at startup (<c>--prepare-next=</c>).</summary>
    public string? PrepareNextPath { get; init; }

    /// <summary>Gets whether the prepared item starts when the current one ends (<c>--auto-next</c>).</summary>
    public bool AutoNext { get; init; }

    /// <summary>Gets the fade into the prepared item (<c>--fade-next=&lt;seconds&gt;</c>, 0 to 60).</summary>
    public TimeSpan FadeNext { get; init; }

    /// <summary>Gets the bounded network retries (<c>--net-retries=</c>).</summary>
    public int NetworkRetries { get; init; }

    /// <summary>Gets the extra certificate authority file for https (<c>--net-ca-file=</c>).</summary>
    public string? NetworkCaFile { get; init; }

    /// <summary>Gets whether https certificate errors are ignored (<c>--net-allow-invalid-certs</c>).</summary>
    public bool NetworkAllowInvalidCertificates { get; init; }

    /// <summary>
    /// Gets whether the surfaces auto-hide until visual content is ready (default true;
    /// <c>--no-auto-hide</c> turns it off). Every adapter's surface has the option since Stage 046A.
    /// </summary>
    public bool AutoHideSurfaces { get; init; } = true;

    /// <summary>
    /// Gets whether a test window with the adapter's <c>BackgroundSurface</c> and
    /// <c>KaraokeSurface</c> opens at startup (<c>--lightweight-surfaces</c>, Stage 046A slice 2;
    /// every player has it).
    /// </summary>
    public bool LightweightSurfacesWindow { get; init; }

    /// <summary>
    /// Gets the FatzAudio output device (<c>--audio-device=</c>, its id or name; the Log lists the names when
    /// it is not found), or null for the system default.
    /// </summary>
    public string? AudioDevice { get; init; }

    /// <summary>
    /// Gets the scaled CD+G picture (<c>--cdg-scale</c>, smoothness <see cref="DefaultCdgSmoothness"/>, or <c>--cdg-scale=0..10</c>; <c>--cdg-effects</c>
    /// adds an outline and a shadow); off (the classic picture) without it.
    /// </summary>
    public CdgScalingOptions CdgScaling { get; init; }

    /// <summary>Gets whether FatzAudio decodes video soundtracks from FFmpeg's packets (<c>--video-audio=fatz</c>); FFmpeg decodes otherwise.</summary>
    public bool AudioEngineDecodesSoundtracks { get; init; }

    /// <summary>
    /// The audio options on <paramref name="configuration"/>: FatzAudio plays on <c>--audio-device=</c> when
    /// given, CD+G scaled with <c>--cdg-scale</c>, soundtracks decoded with <c>--video-audio=fatz</c>.
    /// </summary>
    public MediaPlayerConfiguration ApplyAudioOptions(MediaPlayerConfiguration configuration) => configuration with
    {
        AudioOutputDeviceId = AudioDevice,
        CdgScaling = CdgScaling,
        AudioEngineDecodesSoundtracks = AudioEngineDecodesSoundtracks
    };

    /// <summary>Gets the equalizer gains in dB (<c>--eq=g1,...,g10</c>), which also turn it on; null when not given.</summary>
    public IReadOnlyList<double>? EqualizerGains { get; init; }

    /// <summary>The most players a test player window shows.</summary>
    public const int MaxPlayers = 4;

    static CdgScalingOptions ParseCdgScaling(IReadOnlyList<string> args)
    {
        var smoothness = Values(args, "--cdg-scale=").LastOrDefault();
        if (smoothness is null && !args.Contains("--cdg-scale"))
            return default;

        var scaling = new CdgScalingOptions
        {
            Enabled = true,
            Smoothness = int.TryParse(smoothness, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : DefaultCdgSmoothness,
            EdgeBlend = true,
        };
        return args.Contains("--cdg-effects") ? scaling.WithDefaultEffects() : scaling;
    }

    /// <summary>The smoothness of a bare <c>--cdg-scale</c>: traced straight edges with sharp corners (owner pick, ENG-064).</summary>
    public const int DefaultCdgSmoothness = 6;

    /// <summary>Parses <paramref name="args"/>. Unknown flags are ignored.</summary>
    public static PlayerStartupOptions Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var positional = args.FirstOrDefault(static argument => !argument.StartsWith("--", StringComparison.Ordinal));
        var positionalUrl = positional is not null && PlayerViewModel.TryParseNetworkUrl(positional, out var parsedUrl) ? parsedUrl : null;
        var surface = Values(args, "--surface=").LastOrDefault();
        var decoding = Values(args, "--decoding=").LastOrDefault();
        var networkUri = Values(args, "--url=")
            .Select(static value => PlayerViewModel.TryParseNetworkUrl(value, out var uri) ? uri : null)
            .FirstOrDefault(static uri => uri is not null);

        return new PlayerStartupOptions
        {
            // One startup item: a URL (flag or positional) replaces a positional file.
            MediaPath = positionalUrl is null && networkUri is null ? positional : null,
            NetworkUri = networkUri ?? positionalUrl,
            Mode = Values(args, "--mode=")
                .Select(static value => Enum.TryParse(value, ignoreCase: true, out MediaPlayerMode mode) && Enum.IsDefined(mode) ? mode : (MediaPlayerMode?)null)
                .LastOrDefault(static mode => mode is not null) ?? MediaPlayerMode.Full,
            NativeDirectories = [.. Values(args, "--native=").Where(static path => path.Length > 0)],
            Renderer = Values(args, "--renderer=").LastOrDefault(),
            SoftwareRenderer = HasFlag(args, "--software"),
            ResetPitchOnLoad = HasFlag(args, "--reset-pitch-on-load"),
            ResetTempoOnLoad = HasFlag(args, "--reset-tempo-on-load"),
            VisualTransformDemo = HasFlag(args, "--visual-transform-demo"),
            TrimSilence = HasFlag(args, "--trim-silence"),
            BackgroundFollowsMedia = HasFlag(args, "--background-follows-media"),
            DisableHdrToneMapping = HasFlag(args, "--no-hdr-tonemap"),
            PreferCpuHdrToneMapping = HasFlag(args, "--hdr-tonemap-cpu"),
            HdrOutput = HasFlag(args, "--hdr-output"),
            PrepareNextPath = Values(args, "--prepare-next=").FirstOrDefault(static path => path.Length > 0),
            AutoNext = HasFlag(args, "--auto-next"),
            FadeNext = Values(args, "--fade-next=").Select(ParseFade).FirstOrDefault(),
            NetworkRetries = Values(args, "--net-retries=", trimQuotes: false)
                .Select(static value => int.TryParse(value, out var retries) ? retries : 0)
                .FirstOrDefault(),
            NetworkCaFile = Values(args, "--net-ca-file=").FirstOrDefault(static path => path.Length > 0),
            NetworkAllowInvalidCertificates = HasFlag(args, "--net-allow-invalid-certs"),
            EqualizerGains = Values(args, "--eq=", trimQuotes: false).Select(ParseGains).FirstOrDefault(),
            AutoHideSurfaces = !HasFlag(args, "--no-auto-hide"),
            LightweightSurfacesWindow = HasFlag(args, "--lightweight-surfaces"),
            Players = Values(args, "--players=", trimQuotes: false)
                .Select(static value => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) ? Math.Clamp(count, 1, MaxPlayers) : (int?)null)
                .LastOrDefault(static count => count is not null) ?? 1,
            PreferFrameSinkSurface = IsValue(surface, "framesink"),
            UnknownSurface = surface is null || IsValue(surface, "framesink") || IsValue(surface, "auto") ? null : surface,
            AudioDevice = Values(args, "--audio-device=").LastOrDefault(static value => !string.IsNullOrWhiteSpace(value)),
            CdgScaling = ParseCdgScaling(args),
            AudioEngineDecodesSoundtracks = Values(args, "--video-audio=").LastOrDefault() is { } videoAudio && IsValue(videoAudio, "fatz"),
            SoftwareDecoding = IsValue(decoding, "software"),
            UnknownDecoding = decoding is null || IsValue(decoding, "software") || IsValue(decoding, "auto") ? null : decoding,
            SecondScreen = Values(args, "--second-screen=", trimQuotes: false)
                .Select(static value => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var screen) && screen > 0 ? screen : (int?)null)
                .LastOrDefault(static screen => screen is not null),
        };
    }

    /// <summary>
    /// Gets these options for a second or later player in the same process: the same settings,
    /// without the startup media, URL and prepared item, which belong to the first player.
    /// </summary>
    public PlayerStartupOptions WithoutStartupMedia() => this with { MediaPath = null, NetworkUri = null, PrepareNextPath = null };

    /// <summary>Gets these options for player <paramref name="index"/> (1-based): only player 1 gets the startup media.</summary>
    public PlayerStartupOptions ForPlayer(int index) => index == 1 ? this : WithoutStartupMedia();

    /// <summary>
    /// Gets these options for player <paramref name="index"/> (1-based) of a window build: in the
    /// first build (<paramref name="atStartup"/>) only player 1 gets the startup media; players
    /// rebuilt from the toolbar start empty.
    /// </summary>
    public PlayerStartupOptions ForBuild(int index, bool atStartup) => atStartup ? ForPlayer(index) : WithoutStartupMedia();

    /// <summary>
    /// Gets whether player <paramref name="index"/> (1-based) opens its second screen once it has
    /// started: only player 1 of the window's first build (<paramref name="atStartup"/>), when
    /// <c>--second-screen=</c> was given.
    /// </summary>
    public bool OpensSecondScreenAtStartup(int index, bool atStartup) => atStartup && index == 1 && SecondScreen is not null;

    /// <summary>
    /// Gets how video is decoded: <paramref name="rendererDecoding"/> (what the chosen renderer can
    /// present), or software with <c>--decoding=software</c>, with a short reason.
    /// </summary>
    public (VideoDecodingMethod Decoding, string? Forced) ApplyDecoding(VideoDecodingMethod rendererDecoding) =>
        SoftwareDecoding && rendererDecoding != VideoDecodingMethod.Software
            ? (VideoDecodingMethod.Software, "software decoding (--decoding=software)")
            : (rendererDecoding, null);

    /// <summary>
    /// Gets a warning for each player value that was not understood (<c>--renderer=</c>,
    /// <c>--surface=</c>, <c>--decoding=</c>), naming the default used instead. Hosts log them.
    /// </summary>
    public IReadOnlyList<string> DescribeUnknownValues()
    {
        List<string> warnings = [];
        if (UnknownRenderer is { } renderer)
            warnings.Add($"Unknown --renderer value '{renderer}' (use {PlayerRendererRules.Usage}); using {PlayerRendererRules.Name(RendererRequest)}.");
        if (UnknownSurface is { } surface)
            warnings.Add($"Unknown --surface value '{surface}' (use auto or framesink); using auto.");
        if (UnknownDecoding is { } decoding)
            warnings.Add($"Unknown --decoding value '{decoding}' (use auto or software); using auto.");
        return warnings;
    }

    /// <summary>Gets whether <paramref name="flag"/> was given (case-insensitive).</summary>
    public static bool HasFlag(IReadOnlyList<string> args, string flag) =>
        args.Any(argument => string.Equals(argument, flag, StringComparison.OrdinalIgnoreCase));

    /// <summary>Gets the values of every <c>prefix=value</c> argument, in order (case-insensitive prefix, quotes trimmed).</summary>
    public static IEnumerable<string> Values(IReadOnlyList<string> args, string prefix, bool trimQuotes = true) =>
        args.Where(argument => argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(argument => trimQuotes ? argument[prefix.Length..].Trim('"') : argument[prefix.Length..]);

    static bool IsValue(string? value, string expected) =>
        value is not null && value.Trim().Equals(expected, StringComparison.OrdinalIgnoreCase);

    static TimeSpan ParseFade(string value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && double.IsFinite(seconds)
            ? TimeSpan.FromSeconds(Math.Clamp(seconds, 0, MaxFadeNextSeconds))
            : TimeSpan.Zero;

    static double[] ParseGains(string value) =>
        [.. value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static gain => double.TryParse(gain, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0)];
}
