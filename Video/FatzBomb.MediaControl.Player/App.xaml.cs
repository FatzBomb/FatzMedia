using System.Diagnostics;
using System.IO;
using System.Windows;

namespace FatzBomb.MediaControl.Player;

public partial class App : Application
{
    private TextWriterTraceListener? _logListener;
    private StreamWriter? _logWriter;

    public static string LogFilePath { get; } =
        Path.Combine(Path.GetTempPath(), "FatzBombMedia", "player-debug.log");

    public static string? StartupMediaPath { get; private set; }

    public static bool UseSoftwareRenderer { get; private set; }

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

        UseSoftwareRenderer = e.Args.Any(static argument =>
            string.Equals(argument, "--software", StringComparison.OrdinalIgnoreCase));
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
                out var seconds) ? TimeSpan.FromSeconds(seconds) : TimeSpan.Zero)
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
