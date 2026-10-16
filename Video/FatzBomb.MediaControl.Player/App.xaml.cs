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
