using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace FatzMedia.Video.Core.Video;

/// <summary>
/// Keeps FFmpeg's own warning and error lines for chosen codec contexts, so a hardware decode
/// that yields no frame can say what FFmpeg reported. Installs one process-wide FFmpeg log
/// callback on first use; messages for other contexts go to FFmpeg's default logger unchanged.
/// Network reads (Stage 039A) also route the warnings FFmpeg logs on the reading thread, such as
/// an HTTP reconnect, to an observer set for the duration of the read.
/// </summary>
internal static unsafe class FfmpegLogCapture
{
    private const int MaxLinesPerContext = 6;
    private const int LineBufferBytes = 1024;
    private static readonly Lock Gate = new();
    private static readonly Dictionary<nint, List<string>> Captures = [];
    // Kept alive for the life of the process: FFmpeg holds the function pointer.
    private static av_log_set_callback_callback? _callback;

    // The observer of the network read running on this thread (Stage 039A), or null.
    [ThreadStatic]
    private static Action<int, string>? t_networkObserver;

    public static void Begin(void* context)
    {
        if (context == null)
            return;

        lock (Gate)
        {
            EnsureInstalledCore();
            Captures[(nint)context] = [];
        }
    }

    /// <summary>Installs the process-wide callback (idempotent).</summary>
    public static void EnsureInstalled()
    {
        lock (Gate)
            EnsureInstalledCore();
    }

    /// <summary>
    /// Routes FFmpeg warnings and errors logged on this thread to <paramref name="observer"/> until
    /// the returned scope ends. Used around network reads; the lines are not also printed.
    /// </summary>
    public static NetworkObserverScope ObserveNetwork(Action<int, string> observer)
    {
        Action<int, string>? previous = t_networkObserver;
        t_networkObserver = observer;
        return new NetworkObserverScope(previous);
    }

    /// <summary>Restores the previous observer of this thread.</summary>
    public readonly struct NetworkObserverScope(Action<int, string>? previous) : IDisposable
    {
        public void Dispose() => t_networkObserver = previous;
    }

    private static void EnsureInstalledCore()
    {
        if (_callback != null)
            return;

        _callback = OnLog;
        ffmpeg.av_log_set_callback(new av_log_set_callback_callback_func
        {
            Pointer = Marshal.GetFunctionPointerForDelegate(_callback)
        });
    }

    public static IReadOnlyList<string> Peek(void* context)
    {
        lock (Gate)
            return Captures.TryGetValue((nint)context, out List<string>? lines) ? [.. lines] : [];
    }

    public static void End(void* context)
    {
        lock (Gate)
            _ = Captures.Remove((nint)context);
    }

    private static void OnLog(void* context, int level, string format, byte* arguments)
    {
        if (level <= ffmpeg.AV_LOG_WARNING && context != null && TryReserveLine(context))
        {
            // The argument list can be read only once, so a captured line is not also printed.
            var buffer = stackalloc byte[LineBufferBytes];
            var printPrefix = 1;
            ffmpeg.av_log_format_line(context, level, format, arguments, buffer, LineBufferBytes, &printPrefix);
            var line = Marshal.PtrToStringUTF8((nint)buffer)?.Trim();
            if (!string.IsNullOrEmpty(line))
                AddLine(context, line);
            return;
        }

        if (level <= ffmpeg.AV_LOG_WARNING && t_networkObserver is { } observer)
        {
            var buffer = stackalloc byte[LineBufferBytes];
            var printPrefix = 1;
            ffmpeg.av_log_format_line(context, level, format, arguments, buffer, LineBufferBytes, &printPrefix);
            var line = Marshal.PtrToStringUTF8((nint)buffer)?.Trim();
            if (!string.IsNullOrEmpty(line))
            {
                try
                {
                    observer(level, line);
                }
                catch
                {
                    // An observer must never throw into FFmpeg.
                }
            }

            return;
        }

        ffmpeg.av_log_default_callback(context, level, format, arguments);
    }

    private static bool TryReserveLine(void* context)
    {
        lock (Gate)
            return Captures.TryGetValue((nint)context, out List<string>? lines) && lines.Count < MaxLinesPerContext;
    }

    private static void AddLine(void* context, string line)
    {
        lock (Gate)
            if (Captures.TryGetValue((nint)context, out List<string>? lines) && lines.Count < MaxLinesPerContext && !lines.Contains(line))
                lines.Add(line);
    }
}
