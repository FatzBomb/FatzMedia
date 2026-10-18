using System.Diagnostics;
using System.IO;
using System.Windows;
using FatzBomb.MediaControl.Player.Configuration;

namespace FatzBomb.MediaControl.Player;

public partial class App : Application
{
    private TextWriterTraceListener? _logListener;
    private StreamWriter? _logWriter;

    // The engine clamps fade lengths to 60 s; clamp here too so TimeSpan.FromSeconds cannot throw.

    private const double MaxFadeNextSeconds = 60;


    public static string LogFilePath { get; } =
        Path.Combine(Path.GetTempPath(), "FatzBombMedia", "player-debug.log");

    public static string? StartupMediaPath { get; private set; }

    public static bool UseSoftwareRenderer { get; private set; }

    /// <summary>Gets the renderer chosen at startup (Stage 036A: GPU, then WARP, then software).</summary>
    internal static PlayerRendererSelection Renderer { get; private set; } = null!;

    public static bool ResetPitchOnMediaLoad { get; private set; }

    public static bool ResetTempoOnMediaLoad { get; private set; }

    public static bool AutoHideSurfaces { get; private set; } = true;

    public static bool UseVisualTransformDemo { get; private set; }

    public static bool EnableSilenceTrim { get; private set; }

    public static string? PrepareNextPath { get; private set; }

    public static bool AutoTransitionToPreparedNext { get; private set; }

    public static TimeSpan PreparedNextFadeDuration { get; private set; }

    public static Uri? StartupNetworkUri { get; private set; }

    public static int NetworkRetries { get; private set; }

    public static double[]? EqualizerGains { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        const string rendererPrefix = "--renderer=";
        var rendererArgument = e.Args
            .Where(static argument => argument.StartsWith(rendererPrefix, StringComparison.OrdinalIgnoreCase))
            .Select(static argument => argument[rendererPrefix.Length..].Trim('"'))
            .LastOrDefault();
        PlayerRendererChoice rendererChoice = e.Args.Any(static argument =>
            string.Equals(argument, "--software", StringComparison.OrdinalIgnoreCase))
                ? PlayerRendererChoice.Software
                : PlayerRendererChoice.Auto;
        var invalidRendererArgument = false;
        if (rendererArgument is not null)
        {
            if (PlayerRendererSelection.TryParse(rendererArgument, out PlayerRendererChoice parsed))
                rendererChoice = parsed;
            else
                invalidRendererArgument = true;
        }
        ResetPitchOnMediaLoad = e.Args.Any(static argument =>
            string.Equals(argument, "--reset-pitch-on-load", StringComparison.OrdinalIgnoreCase));
        ResetTempoOnMediaLoad = e.Args.Any(static argument =>
            string.Equals(argument, "--reset-tempo-on-load", StringComparison.OrdinalIgnoreCase));
        AutoHideSurfaces = !e.Args.Any(static argument =>
            string.Equals(argument, "--no-auto-hide", StringComparison.OrdinalIgnoreCase));
        UseVisualTransformDemo = e.Args.Any(static argument =>
            string.Equals(argument, "--visual-transform-demo", StringComparison.OrdinalIgnoreCase));
        EnableSilenceTrim = e.Args.Any(static argument =>
            string.Equals(argument, "--trim-silence", StringComparison.OrdinalIgnoreCase));
        const string prepareNextPrefix = "--prepare-next=";
        PrepareNextPath = e.Args
            .Where(static argument => argument.StartsWith(prepareNextPrefix, StringComparison.OrdinalIgnoreCase))
            .Select(static argument => argument[prepareNextPrefix.Length..].Trim('"'))
            .FirstOrDefault(static path => path.Length > 0);
        const string fadeNextPrefix = "--fade-next=";
        PreparedNextFadeDuration = e.Args
            .Where(static argument => argument.StartsWith(fadeNextPrefix, StringComparison.OrdinalIgnoreCase))
            .Select(static argument => double.TryParse(
                argument[fadeNextPrefix.Length..],
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var seconds) && double.IsFinite(seconds)
                    ? TimeSpan.FromSeconds(Math.Clamp(seconds, 0, MaxFadeNextSeconds))
                    : TimeSpan.Zero)
            .FirstOrDefault();
        const string urlPrefix = "--url=";
        StartupNetworkUri = e.Args
            .Where(static argument => argument.StartsWith(urlPrefix, StringComparison.OrdinalIgnoreCase))
            .Select(static argument => Uri.TryCreate(argument[urlPrefix.Length..].Trim('"'), UriKind.Absolute, out var uri) ? uri : null)
            .FirstOrDefault(static uri => uri is not null);
        const string retryPrefix = "--net-retries=";
        NetworkRetries = e.Args
            .Where(static argument => argument.StartsWith(retryPrefix, StringComparison.OrdinalIgnoreCase))
            .Select(static argument => int.TryParse(argument[retryPrefix.Length..], out var retries) ? retries : 0)
            .FirstOrDefault();
        const string eqPrefix = "--eq=";
        EqualizerGains = e.Args
            .Where(static argument => argument.StartsWith(eqPrefix, StringComparison.OrdinalIgnoreCase))
            .Select(static argument => argument[eqPrefix.Length..]
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(static gain => double.TryParse(gain, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : 0)
                .ToArray())
            .FirstOrDefault();
        AutoTransitionToPreparedNext = e.Args.Any(static argument =>
            string.Equals(argument, "--auto-next", StringComparison.OrdinalIgnoreCase));
        StartupMediaPath = e.Args.FirstOrDefault(static argument =>
            !argument.StartsWith("--", StringComparison.Ordinal));

        TryAttachDebugLog();

        // Resolved before the main window exists: the presentation mode and decoding follow it.
        try
        {
            Renderer = PlayerRendererSelection.Resolve(rendererChoice);
        }
        catch (Exception ex)
        {
            Renderer = PlayerRendererSelection.SoftwareAfterFailure(ex);
        }

        if (invalidRendererArgument)
            Renderer.AddLogLine(MediaDiagnosticLevel.Warning, $"Unknown --renderer value '{rendererArgument}' (use auto, gpu, warp or software); using {rendererChoice.ToString().ToLowerInvariant()}.");

        UseSoftwareRenderer = Renderer.UsesSoftwareRenderer;
        Trace.WriteLine($"[Player] Renderer: {Renderer.Summary}");
    }

    private void TryAttachDebugLog()
    {
        try
        {
            var directory = Path.GetDirectoryName(LogFilePath);
            if (!string.IsNullOrEmpty(directory))
                _ = Directory.CreateDirectory(directory);

            // Overwrite on each run so the captured trace reflects a single reproduction session.
            _logWriter = new StreamWriter(LogFilePath, append: false) { AutoFlush = true };
            _logListener = new TextWriterTraceListener(_logWriter);
            _ = Trace.Listeners.Add(_logListener);
            Trace.AutoFlush = true;
            Trace.WriteLine($"[Player] Debug log started {DateTime.Now:O} -> {LogFilePath}");
        }
        catch
        {
            // Logging is a diagnostic aid only; never block startup if the file cannot be created.
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            if (_logListener is not null)
            {
                Trace.Flush();
                Trace.Listeners.Remove(_logListener);
                _logListener.Dispose();
            }

            _logWriter?.Dispose();
        }
        catch
        {
            // Best-effort cleanup.
        }

        base.OnExit(e);
    }
}
