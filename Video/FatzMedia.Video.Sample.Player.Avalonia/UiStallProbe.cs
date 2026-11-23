using System.Globalization;
using Avalonia.Threading;

namespace FatzMedia.Video.Sample.Player.Avalonia;

/// <summary>
/// Debug-only rig for the "video freezes while the UI thread is busy" check (ENG-066): with
/// <c>--ui-stall=&lt;ms&gt;,&lt;every ms&gt;</c> the UI thread sleeps for the first number of
/// milliseconds once per period, as a drop-down opening or a window loading would block it. F9 does one
/// stall of the same length. <c>--ui-stall-log=&lt;file&gt;</c> writes one line per stall and per
/// renderer-presented frame (UTC ticks), so a run can be lined up with an external screen capture:
/// the player's own frame counters are raised by the renderer, so they cannot tell whether the
/// compositor showed the frame while the UI thread was blocked.
/// </summary>
sealed class UiStallProbe : IDisposable
{
    readonly int _stallMs;
    readonly DispatcherTimer? _timer;
    StreamWriter? _log;
    readonly string? _logPath;
    readonly Lock _logGate = new();
    IPlayerSession? _session;

    UiStallProbe(int stallMs, int periodMs, string? logPath)
    {
        _stallMs = stallMs;
        _logPath = logPath;
        if (periodMs > 0)
        {
            _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(periodMs), DispatcherPriority.Normal, OnTick);
            _timer.Stop();
        }
    }

    /// <summary>Gets the probe for this run, or null without <c>--ui-stall=</c>.</summary>
    public static UiStallProbe? Create(IReadOnlyList<string> args)
    {
        var value = Value(args, "--ui-stall=");
        if (value is null)
            return null;

        var parts = value.Split(',');
        if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var stallMs) || stallMs <= 0)
            return null;

        var periodMs = parts.Length > 1 && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var period) && period > 0
            ? period
            : 0;
        return new UiStallProbe(stallMs, periodMs, Value(args, "--ui-stall-log="));
    }

    /// <summary>Starts logging <paramref name="session"/>'s presented frames and, when periodic, stalling (UI thread).</summary>
    public void Start(IPlayerSession session)
    {
        _session?.PresentationFramePresented -= OnFramePresented;
        _session = session;
        if (_log is null && _logPath is not null)
        {
            try
            {
                var log = new StreamWriter(_logPath, append: false) { AutoFlush = true };
                lock (_logGate)
                    _log = log;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A bad log path only loses the log: the stalls still run.
            }
        }

        session.PresentationFramePresented += OnFramePresented;
        _timer?.Start();
    }

    /// <summary>One stall of the configured length (F9).</summary>
    public void StallOnce() => Stall();

    public void Dispose()
    {
        _timer?.Stop();
        _session?.PresentationFramePresented -= OnFramePresented;
        lock (_logGate)
            _log?.Dispose();
    }

    void OnTick(object? sender, EventArgs e) => Stall();

    void Stall()
    {
        Write("stall-start");
        Thread.Sleep(_stallMs);
        Write("stall-end");
    }

    // Raised on the renderer's thread.
    void OnFramePresented(object? sender, EventArgs e) => Write("frame");

    void Write(string kind)
    {
        var ticks = DateTime.UtcNow.Ticks;
        lock (_logGate)
            _log?.WriteLine($"{kind},{ticks}");
    }

    static string? Value(IReadOnlyList<string> args, string prefix) =>
        args.Select(argument => argument.Trim('"'))
            .LastOrDefault(argument => argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))?[prefix.Length..];
}
