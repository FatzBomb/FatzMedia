using FatzMedia.Video.Core.Network;
using FFmpeg.AutoGen;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace FatzMedia.Video.Core.Media;

/// <summary>
/// Options for opening a network source through FFmpeg (Stage 039A).
/// </summary>
/// <param name="Retry">The host's bounded retry policy: its connect timeout bounds the open and every read.</param>
/// <param name="Cancellation">Cancels an open in progress (Close or Dispose while connecting).</param>
/// <param name="Tls">The host's certificate checks for https (ENG-034).</param>
/// <param name="ReadAhead">How far a <see cref="SharedDemuxer"/> reads ahead (slice 2); null uses the default.</param>
internal sealed record FfmpegNetworkOptions(NetworkRetryPolicy Retry, CancellationToken Cancellation, NetworkTlsPolicy Tls, NetworkReadAheadPolicy? ReadAhead = null)
{
    /// <summary>The default policies, not cancellable.</summary>
    public static FfmpegNetworkOptions Default { get; } = new(NetworkRetryPolicy.Default, CancellationToken.None, NetworkTlsPolicy.Default);
}

/// <summary>Process-wide FFmpeg network setup (Stage 039A).</summary>
internal static class FfmpegNetwork
{
    private static int _initialized;
    private static FfmpegTlsBackend? _tlsBackend;

    /// <summary>Calls <c>avformat_network_init</c> once per process, before the first network open.</summary>
    public static void EnsureInitialized()
    {
        if (Interlocked.Exchange(ref _initialized, 1) == 0)
            _ = ffmpeg.avformat_network_init();
    }

    /// <summary>
    /// The TLS backend of the loaded FFmpeg build (ENG-034), read once from its configure line;
    /// <see cref="FfmpegTlsBackend.Unknown"/> when FFmpeg is not loaded.
    /// </summary>
    public static FfmpegTlsBackend TlsBackend
    {
        get
        {
            if (_tlsBackend is { } known)
                return known;

            FfmpegTlsBackend backend;
            try
            {
                backend = NetworkTlsRules.DetectBackend(ffmpeg.avformat_configuration(), OperatingSystem.IsWindows());
            }
            catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or NotSupportedException or TypeInitializationException)
            {
                // Not loaded yet (or a test host without FFmpeg): ask again next time.
                return FfmpegTlsBackend.Unknown;
            }

            _tlsBackend = backend;
            return backend;
        }
    }

    /// <summary>
    /// The warning for this FFmpeg build when it cannot verify <paramref name="url"/> with
    /// <paramref name="policy"/> as given (see <see cref="NetworkTlsRules.GetSetupWarning"/>), or
    /// <see langword="null"/>. Only https URLs are checked.
    /// </summary>
    public static string? GetTlsSetupWarning(string url, NetworkTlsPolicy policy) =>
        NetworkTlsRules.IsHttps(url) ? NetworkTlsRules.GetSetupWarning(TlsBackend, OperatingSystem.IsWindows(), policy) : null;

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
/// the open was cancelled, the operation's deadline passed, or a newer seek superseded the
/// operation (ENG-035). It also times the read in progress, which is how buffering is detected.
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
    private long _supersededBefore;
    private Func<bool>? _operationCancelled;

    public FfmpegInterrupt() => _handle = GCHandle.Alloc(this, GCHandleType.Weak);

    /// <summary>Whether <see cref="Abort"/> was called since the last <see cref="Reset"/>.</summary>
    public bool IsAborted => _aborted;

    /// <summary>
    /// Whether the engine itself is ending the operation in progress (abort, cancellation, or a newer
    /// operation superseding it), as opposed to a timeout. FFmpeg's log lines that follow are expected.
    /// </summary>
    public bool IsCancelledByOwner => _aborted || _cancellation.IsCancellationRequested || IsOperationSuperseded;

    /// <summary>Whether the deadline of the operation in progress has passed.</summary>
    public bool IsDeadlinePassed
    {
        get
        {
            var deadline = Interlocked.Read(ref _deadline);
            return deadline != 0 && Stopwatch.GetTimestamp() >= deadline;
        }
    }

    /// <summary>Whether an interrupt was requested (abort, cancellation, or a passed deadline).</summary>
    public bool IsInterruptRequested
    {
        get
        {
            if (_aborted || _cancellation.IsCancellationRequested)
                return true;

            if (IsOperationSuperseded)
                return true;

            var deadline = Interlocked.Read(ref _deadline);
            return deadline != 0 && Stopwatch.GetTimestamp() >= deadline;
        }
    }

    /// <summary>
    /// Whether the operation in progress started before <see cref="SupersedeOperationsStartedBefore"/>
    /// was last called, or the owner's cancellation condition (<see cref="SetOperationCancellation"/>)
    /// holds.
    /// </summary>
    private bool IsOperationSuperseded
    {
        get
        {
            var started = Interlocked.Read(ref _operationStarted);
            return started != 0
                && (started < Interlocked.Read(ref _supersededBefore) || Volatile.Read(ref _operationCancelled)?.Invoke() == true);
        }
    }

    /// <summary>
    /// Sets a condition that gives up the operation in progress while it holds, like
    /// <see cref="SupersedeOperationsStartedBefore"/> but without a timing window: the network
    /// read-ahead (slice 2) uses it to give up a read for a position a seek has left. It is polled
    /// from FFmpeg's I/O, so it must be cheap and lock-free. Null removes it.
    /// </summary>
    public void SetOperationCancellation(Func<bool>? condition) => Volatile.Write(ref _operationCancelled, condition);

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
        Volatile.Write(ref _operationCancelled, null);
        _ = Interlocked.Exchange(ref _deadline, 0);
        _ = Interlocked.Exchange(ref _operationStarted, 0);
    }

    /// <summary>Stops using the open's cancellation token once the open completed.</summary>
    public void ReleaseCancellation() => _cancellation = CancellationToken.None;

    /// <summary>Asks FFmpeg to give up every blocking call on this context from now on. Thread-safe and lock-free.</summary>
    public void Abort() => _aborted = true;

    /// <summary>
    /// Asks FFmpeg to give up the operation in progress if it started before
    /// <paramref name="timestamp"/> (a <see cref="Stopwatch"/> timestamp); operations started
    /// later are unaffected (ENG-035: a newer seek makes a read or seek for the old position
    /// pointless). Thread-safe and lock-free.
    /// </summary>
    public void SupersedeOperationsStartedBefore(long timestamp)
    {
        var current = Interlocked.Read(ref _supersededBefore);
        while (timestamp > current)
        {
            var seen = Interlocked.CompareExchange(ref _supersededBefore, timestamp, current);
            if (seen == current)
                return;

            current = seen;
        }
    }

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

    /// <summary>
    /// Ends the operation started by <see cref="BeginOperation"/>. Returns whether it was
    /// superseded (<see cref="SupersedeOperationsStartedBefore"/>), so a failure is not taken for
    /// a dropped stream.
    /// </summary>
    public bool EndOperation()
    {
        var superseded = IsOperationSuperseded;
        _ = Interlocked.Exchange(ref _deadline, 0);
        _ = Interlocked.Exchange(ref _operationStarted, 0);
        return superseded;
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

/// <summary>
/// Adds the host's TLS options to every nested open of one network format context (ENG-034).
/// FFmpeg hands the open options to the first connection only: the HLS and DASH demuxers open
/// playlists, segments and keys through <c>AVFormatContext.io_open</c> with a fixed subset
/// (headers, cookies, timeouts) that leaves out <c>tls_verify</c> and <c>ca_file</c>. This wraps
/// the context's <c>io_open</c> and adds the TLS options to each http/https open that does not set
/// them, so a segment is verified exactly like its playlist.
/// </summary>
internal sealed unsafe class FfmpegTlsIoOpen : IDisposable
{
    private GCHandle _handle;
    private AVDictionary* _options;
    private nint _defaultIoOpen;

    public FfmpegTlsIoOpen() => _handle = GCHandle.Alloc(this, GCHandleType.Weak);

    /// <summary>Takes the TLS options for the next open.</summary>
    public void SetOptions(IReadOnlyList<KeyValuePair<string, string>> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        fixed (AVDictionary** dictionary = &_options)
        {
            ffmpeg.av_dict_free(dictionary);
            foreach (KeyValuePair<string, string> option in options)
                _ = ffmpeg.av_dict_set(dictionary, option.Key, option.Value, 0);
        }
    }

    /// <summary>Wraps the <c>io_open</c> of a freshly allocated <paramref name="context"/>.</summary>
    public void Install(AVFormatContext* context)
    {
        _defaultIoOpen = (nint)context->io_open.Pointer;
        context->opaque = (void*)GCHandle.ToIntPtr(_handle);
        context->io_open = new AVFormatContext_io_open_func { Pointer = (nint)(delegate* unmanaged[Cdecl]<AVFormatContext*, AVIOContext**, byte*, int, AVDictionary**, int>)&OnIoOpen };
    }

    public void Dispose()
    {
        fixed (AVDictionary** dictionary = &_options)
            ffmpeg.av_dict_free(dictionary);
        if (_handle.IsAllocated)
            _handle.Free();
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static int OnIoOpen(AVFormatContext* context, AVIOContext** pb, byte* url, int flags, AVDictionary** options)
    {
        FfmpegTlsIoOpen? hook = null;
        if (context != null && context->opaque != null)
        {
            var handle = GCHandle.FromIntPtr((nint)context->opaque);
            hook = handle.IsAllocated ? handle.Target as FfmpegTlsIoOpen : null;
        }

        if (hook is null || hook._defaultIoOpen == 0)
            return ffmpeg.AVERROR_BUG;

        var open = (delegate* unmanaged[Cdecl]<AVFormatContext*, AVIOContext**, byte*, int, AVDictionary**, int>)hook._defaultIoOpen;
        if (hook._options == null || !NetworkTlsRules.TakesTlsOptions(Marshal.PtrToStringUTF8((nint)url)))
            return open(context, pb, url, flags, options);

        // The caller's dictionary keeps the options FFmpeg did not use; a NULL one gets a local copy.
        AVDictionary* local = null;
        AVDictionary** target = options != null ? options : &local;
        _ = ffmpeg.av_dict_copy(target, hook._options, ffmpeg.AV_DICT_DONT_OVERWRITE);
        var result = open(context, pb, url, flags, target);
        ffmpeg.av_dict_free(&local);
        return result;
    }
}
