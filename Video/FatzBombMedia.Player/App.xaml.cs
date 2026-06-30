using System.Diagnostics;
using System.IO;
using System.Windows;

namespace FatzBombMedia.Player;

public partial class App : Application
{
    private TextWriterTraceListener? _logListener;
    private StreamWriter? _logWriter;

    public static string LogFilePath { get; } =
        Path.Combine(Path.GetTempPath(), "FatzBombMedia", "player-debug.log");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
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
