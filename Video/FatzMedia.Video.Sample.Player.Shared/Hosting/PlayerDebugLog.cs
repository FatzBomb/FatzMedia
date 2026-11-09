using System.Diagnostics;

namespace FatzMedia.Video.Sample.Player.Shared.Hosting;

/// <summary>
/// The test players' debug log file: every <see cref="Trace"/> line of the run (the view model
/// writes each diagnostic there), overwritten at each start so it holds one reproduction.
/// </summary>
public sealed class PlayerDebugLog : IDisposable
{
    private TextWriterTraceListener? _listener;
    private StreamWriter? _writer;

    private PlayerDebugLog(string path) => FilePath = path;

    /// <summary>Gets the log file's path.</summary>
    public string FilePath { get; }

    /// <summary>Gets <c>%TEMP%/FatzMedia.Video/&lt;fileName&gt;</c>.</summary>
    public static string DefaultPath(string fileName) =>
        Path.Combine(Path.GetTempPath(), "FatzMedia.Video", fileName);

    /// <summary>
    /// Starts writing <see cref="Trace"/> to <paramref name="path"/>. Logging is a diagnostic aid
    /// only: when the file cannot be created the log stays detached and startup goes on.
    /// </summary>
    public static PlayerDebugLog Attach(string path, string hostName)
    {
        var log = new PlayerDebugLog(path);
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
                _ = Directory.CreateDirectory(directory);

            // Overwrite on each run so the captured trace reflects a single reproduction session.
            log._writer = new StreamWriter(path, append: false) { AutoFlush = true };
            log._listener = new TextWriterTraceListener(log._writer);
            _ = Trace.Listeners.Add(log._listener);
            Trace.AutoFlush = true;
            Trace.WriteLine($"[{hostName}] Debug log started {DateTime.Now:O} -> {path}");
        }
        catch
        {
            // Logging is a diagnostic aid only; never block startup if the file cannot be created.
        }

        return log;
    }

    public void Dispose()
    {
        try
        {
            if (_listener is not null)
            {
                Trace.Flush();
                Trace.Listeners.Remove(_listener);
                _listener.Dispose();
                _listener = null;
            }

            _writer?.Dispose();
            _writer = null;
        }
        catch
        {
            // Best-effort cleanup.
        }
    }
}
