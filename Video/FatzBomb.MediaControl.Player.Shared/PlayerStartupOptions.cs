using System.Globalization;

namespace FatzBomb.MediaControl.Player.Shared;

/// <summary>
/// The command-line options every test player understands. Host-only flags (for example the WPF
/// player's <c>--no-auto-hide</c>) are parsed by the host from the same arguments; flags unknown
/// here are ignored.
/// </summary>
public sealed record PlayerStartupOptions
{
    /// <summary>The engine clamps fade lengths to 60 s; clamped here too so <c>TimeSpan.FromSeconds</c> cannot throw.</summary>
    public const double MaxFadeNextSeconds = 60;

    /// <summary>Gets the first argument that is not a flag: a media file to load at startup.</summary>
    public string? MediaPath { get; init; }

    /// <summary>Gets the network source to load at startup (<c>--url=</c>).</summary>
    public Uri? NetworkUri { get; init; }

    /// <summary>
    /// Gets the last <c>--renderer=</c> value as given (trimmed of quotes), or null. Each host
    /// interprets it against the renderers it has.
    /// </summary>
    public string? Renderer { get; init; }

    /// <summary>Gets whether <c>--software</c> was given (the software renderer, when <c>--renderer=</c> is absent or invalid).</summary>
    public bool SoftwareRenderer { get; init; }

    /// <summary>Gets whether pitch resets after each load (<c>--reset-pitch-on-load</c>).</summary>
    public bool ResetPitchOnLoad { get; init; }

    /// <summary>Gets whether tempo resets after each load (<c>--reset-tempo-on-load</c>).</summary>
    public bool ResetTempoOnLoad { get; init; }

    /// <summary>Gets whether the visual transforms start at a demo setting (<c>--visual-transform-demo</c>).</summary>
    public bool VisualTransformDemo { get; init; }

    /// <summary>Gets whether leading and trailing silence is trimmed (<c>--trim-silence</c>).</summary>
    public bool TrimSilence { get; init; }

    /// <summary>Gets whether HDR video is shown without tone mapping (<c>--no-hdr-tonemap</c>), for comparison.</summary>
    public bool DisableHdrToneMapping { get; init; }

    /// <summary>Gets whether HDR video is tone mapped on the CPU (<c>--hdr-tonemap-cpu</c>), for comparison.</summary>
    public bool PreferCpuHdrToneMapping { get; init; }

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

    /// <summary>Gets the equalizer gains in dB (<c>--eq=g1,...,g10</c>), which also turn it on; null when not given.</summary>
    public IReadOnlyList<double>? EqualizerGains { get; init; }

    /// <summary>Parses <paramref name="args"/>. Unknown flags are ignored.</summary>
    public static PlayerStartupOptions Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        return new PlayerStartupOptions
        {
            MediaPath = args.FirstOrDefault(static argument => !argument.StartsWith("--", StringComparison.Ordinal)),
            NetworkUri = Values(args, "--url=")
                .Select(static value => Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) ? uri : null)
                .FirstOrDefault(static uri => uri is not null),
            Renderer = Values(args, "--renderer=").LastOrDefault(),
            SoftwareRenderer = HasFlag(args, "--software"),
            ResetPitchOnLoad = HasFlag(args, "--reset-pitch-on-load"),
            ResetTempoOnLoad = HasFlag(args, "--reset-tempo-on-load"),
            VisualTransformDemo = HasFlag(args, "--visual-transform-demo"),
            TrimSilence = HasFlag(args, "--trim-silence"),
            DisableHdrToneMapping = HasFlag(args, "--no-hdr-tonemap"),
            PreferCpuHdrToneMapping = HasFlag(args, "--hdr-tonemap-cpu"),
            PrepareNextPath = Values(args, "--prepare-next=").FirstOrDefault(static path => path.Length > 0),
            AutoNext = HasFlag(args, "--auto-next"),
            FadeNext = Values(args, "--fade-next=").Select(ParseFade).FirstOrDefault(),
            NetworkRetries = Values(args, "--net-retries=", trimQuotes: false)
                .Select(static value => int.TryParse(value, out var retries) ? retries : 0)
                .FirstOrDefault(),
            NetworkCaFile = Values(args, "--net-ca-file=").FirstOrDefault(static path => path.Length > 0),
            NetworkAllowInvalidCertificates = HasFlag(args, "--net-allow-invalid-certs"),
            EqualizerGains = Values(args, "--eq=", trimQuotes: false).Select(ParseGains).FirstOrDefault(),
        };
    }

    /// <summary>Gets whether <paramref name="flag"/> was given (case-insensitive).</summary>
    public static bool HasFlag(IReadOnlyList<string> args, string flag) =>
        args.Any(argument => string.Equals(argument, flag, StringComparison.OrdinalIgnoreCase));

    /// <summary>Gets the values of every <c>prefix=value</c> argument, in order (case-insensitive prefix, quotes trimmed).</summary>
    public static IEnumerable<string> Values(IReadOnlyList<string> args, string prefix, bool trimQuotes = true) =>
        args.Where(argument => argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(argument => trimQuotes ? argument[prefix.Length..].Trim('"') : argument[prefix.Length..]);

    private static TimeSpan ParseFade(string value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && double.IsFinite(seconds)
            ? TimeSpan.FromSeconds(Math.Clamp(seconds, 0, MaxFadeNextSeconds))
            : TimeSpan.Zero;

    private static double[] ParseGains(string value) =>
        [.. value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static gain => double.TryParse(gain, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0)];
}
