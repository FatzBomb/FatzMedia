using FatzBomb.MediaControl.Core.Network;
using FFmpeg.AutoGen;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace FatzBomb.MediaControl.Core.Media;

/// <summary>
/// Options for opening a network source through FFmpeg (Stage 039A).
/// </summary>
/// <param name="Retry">The host's bounded retry policy: its connect timeout bounds the open and every read.</param>
/// <param name="Cancellation">Cancels an open in progress (Close or Dispose while connecting).</param>
internal sealed record FfmpegNetworkOptions(NetworkRetryPolicy Retry, CancellationToken Cancellation)
{
    /// <summary>The default policy, not cancellable.</summary>
    public static FfmpegNetworkOptions Default { get; } = new(NetworkRetryPolicy.Default, CancellationToken.None);
}

/// <summary>Process-wide FFmpeg network setup (Stage 039A).</summary>
internal static class FfmpegNetwork
{
    private static int _initialized;

    /// <summary>Calls <c>avformat_network_init</c> once per process, before the first network open.</summary>
    public static void EnsureInitialized()
    {
        if (Interlocked.Exchange(ref _initialized, 1) == 0)
            _ = ffmpeg.avformat_network_init();
    }

    /// <summary>Whether <paramref name="path"/> is an absolute http/https URL rather than a file path.</summary>
    public static bool IsNetworkUrl(string? path) =>
        !string.IsNullOrWhiteSpace(path)
        && (path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || path.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        && Uri.TryCreate(path, UriKind.Absolute, out _);

    /// <summary>Describes an FFmpeg error code (<c>av_strerror</c>).</summary>
    public static unsafe string DescribeError(int error)
    {
        const int size = 256;
        var buffer = stackalloc byte[size];
        return ffmpeg.av_strerror(error, buffer, size) == 0
            ? Marshal.PtrToStringUTF8((nint)buffer) ?? $"error {error}"
            : $"error {error}";
    }
}

/// <summary>
/// The FFmpeg interrupt callback of one network format context (Stage 039A). FFmpeg polls it
/// during blocking network I/O; it asks FFmpeg to give up when the owner aborted (Close, Dispose),
/// the open was cancelled, or the operation's deadline passed. It also times the read in progress,
/// which is how buffering is detected.
/// </summary>
internal sealed unsafe class FfmpegInterrupt : IDisposable
{
    // Kept alive for the life of the process: FFmpeg holds the function pointer.
    private static readonly AVIOInterruptCB_callback Callback = OnInterrupt;
    private static readonly nint CallbackPointer = Marshal.GetFunctionPointerForDelegate(Callback);

    private GCHandle _handle;
    private volatile bool _aborted;
    private CancellationToken _cancellation;
    private long _deadline;
    private long _operationStarted;

    public FfmpegInterrupt() => _handle = GCHandle.Alloc(this, GCHandleType.Weak);

    /// <summary>Whether <see cref="Abort"/> was called since the last <see cref="Reset"/>.</summary>
    public bool IsAborted => _aborted;

    /// <summary>Whether an interrupt was requested (abort, cancellation, or a passed deadline).</summary>
    public bool IsInterruptRequested
    {
        get
        {
            if (_aborted || _cancellation.IsCancellationRequested)
                return true;

            var deadline = Interlocked.Read(ref _deadline);
            return deadline != 0 && Stopwatch.GetTimestamp() >= deadline;
        }
    }

    /// <summary>How long the current operation has been running, or zero when none is.</summary>
    public TimeSpan CurrentOperationDuration
    {
        get
        {
            var started = Interlocked.Read(ref _operationStarted);
            return started == 0 ? TimeSpan.Zero : Stopwatch.GetElapsedTime(started);
        }
    }

    /// <summary>Prepares for a new open: clears the abort flag and takes the open's cancellation token.</summary>
    public void Reset(CancellationToken cancellation)
    {
        _aborted = false;
        _cancellation = cancellation;
        _ = Interlocked.Exchange(ref _deadline, 0);
        _ = Interlocked.Exchange(ref _operationStarted, 0);
    }

    /// <summary>Stops using the open's cancellation token once the open completed.</summary>
    public void ReleaseCancellation() => _cancellation = CancellationToken.None;

    /// <summary>Asks FFmpeg to give up every blocking call on this context from now on. Thread-safe and lock-free.</summary>
    public void Abort() => _aborted = true;

    /// <summary>Installs the callback on <paramref name="context"/>.</summary>
    public void Install(AVFormatContext* context)
    {
        context->interrupt_callback.callback = new AVIOInterruptCB_callback_func { Pointer = CallbackPointer };
        context->interrupt_callback.opaque = (void*)GCHandle.ToIntPtr(_handle);
    }

    /// <summary>Starts timing an operation, optionally with a deadline.</summary>
    public void BeginOperation(TimeSpan? timeout = null)
    {
        var now = Stopwatch.GetTimestamp();
        _ = Interlocked.Exchange(ref _deadline, timeout is { } value ? now + (long)(value.TotalSeconds * Stopwatch.Frequency) : 0);
        _ = Interlocked.Exchange(ref _operationStarted, now);
    }

    /// <summary>Ends the operation started by <see cref="BeginOperation"/>.</summary>
    public void EndOperation()
    {
        _ = Interlocked.Exchange(ref _deadline, 0);
        _ = Interlocked.Exchange(ref _operationStarted, 0);
    }

    public void Dispose()
    {
        if (_handle.IsAllocated)
            _handle.Free();
    }

    private static int OnInterrupt(void* opaque)
    {
        if (opaque == null)
            return 0;

        var handle = GCHandle.FromIntPtr((nint)opaque);
        return handle.IsAllocated && handle.Target is FfmpegInterrupt interrupt && interrupt.IsInterruptRequested ? 1 : 0;
    }
}
