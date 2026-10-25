using FatzBomb.MediaControl.Core.Network;
using FatzBomb.MediaControl.Core.Video;
using FFmpeg.AutoGen;

namespace FatzBomb.MediaControl.Core.Media;

/// <summary>
/// Owns a single FFmpeg <see cref="AVFormatContext"/> and exposes the container
/// open / stream-discovery / packet-read / seek / close primitives. This is the
/// input/demux seam: it carries no decode, timing, or
/// audio concerns — callers keep their own codec contexts, packets, and frames.
/// </summary>
/// <remarks>
/// Behavior-preserving extraction. Each primitive mirrors the
/// exact FFmpeg call the caller used inline before, including result codes, so
/// EOF / error handling at the call site is unchanged. A demuxer instance owns
/// exactly one container and is single-threaded from the perspective of its
/// owner (one decode thread), matching the previous inline usage.
/// <para>
/// Network sources (Stage 039A) open through FFmpeg's network protocols with an interrupt
/// callback (<see cref="FfmpegInterrupt"/>): <see cref="Abort"/> makes every blocking call return
/// at once from any thread, the open is bounded by the policy's connect timeout and cancellable,
/// and each read is bounded by <c>rw_timeout</c>. A read error other than end of file on a
/// network source (the stream dropped after FFmpeg's own reconnects) is reported once and then
/// returned as end of file, so playback ends instead of retrying a dead connection forever.
/// </para>
/// <para>
/// An https source is verified per the host's <see cref="NetworkTlsPolicy"/> (ENG-034):
/// <c>tls_verify</c> and <c>ca_file</c> go to the first connection and, through
/// <see cref="FfmpegTlsIoOpen"/>, to every playlist, segment and key opened after it. A rejected
/// certificate fails the open with "certificate verification failed for &lt;host&gt;" and
/// FFmpeg's TLS error text.
/// </para>
/// </remarks>
internal sealed unsafe class MediaDemuxer : IDisposable
{
    private const string NetworkDiagnosticCategory = "Network";
    private const int MaxNetworkLogDiagnostics = 20;
    private AVFormatContext* _formatContext;
    private FfmpegInterrupt? _interrupt;
    private FfmpegTlsIoOpen? _tlsIoOpen;
    private readonly List<string> _tlsFailureLines = [];
    private string? _networkUrl;
    private bool _networkReadFailed;
    private bool _networkEndOfStream;
    private int _networkLogDiagnostics;
    private Action<int, string>? _networkLogObserver;

    /// <summary>True while a network source is open.</summary>
    public bool IsNetwork => _networkUrl != null && _formatContext != null;

    /// <summary>The FFmpeg error of the last failed network open, or null.</summary>
    public string? LastNetworkError { get; private set; }

    /// <summary>How long the network read in progress has been blocked (zero when none is).</summary>
    public TimeSpan NetworkReadStall => _interrupt?.CurrentOperationDuration ?? TimeSpan.Zero;

    /// <summary>Whether a network read reached the end of the stream (or the stream dropped).</summary>
    public bool NetworkEndOfStream => _networkEndOfStream;

    /// <summary>True once a container has been opened and stream info probed.</summary>
    public bool IsOpen => _formatContext != null;

    /// <summary>Number of streams in the open container, or 0 when closed.</summary>
    public uint StreamCount => _formatContext != null ? _formatContext->nb_streams : 0;

    /// <summary>
    /// Raw format-level duration in <see cref="ffmpeg.AV_TIME_BASE"/> units
    /// (<c>_fmt->duration</c>), or 0 when closed. Callers apply their own
    /// stream-duration preference; this only surfaces the format value.
    /// </summary>
    public long FormatDurationTicks => _formatContext != null ? _formatContext->duration : 0;

    /// <summary>
    /// Opens <paramref name="path"/> and probes stream info. Any previously open
    /// container is closed first. Returns false (and leaves the demuxer closed)
    /// on failure. Mirrors the previous inline
    /// <c>avformat_open_input</c> + <c>avformat_find_stream_info</c> sequence.
    /// </summary>
    public bool Open(string path) =>
        FfmpegNetwork.IsNetworkUrl(path) ? Open(path, FfmpegNetworkOptions.Default) : OpenFile(path);

    private bool OpenFile(string path)
    {
        Close();

        fixed (AVFormatContext** pfmt = &_formatContext)
        {
            if (ffmpeg.avformat_open_input(pfmt, path, null, null) < 0)
                return false;
        }

        if (ffmpeg.avformat_find_stream_info(_formatContext, null) < 0)
        {
            Close();
            return false;
        }

        return true;
    }

    /// <summary>
    /// Opens an http/https source (Stage 039A): one attempt bounded by the policy's connect
    /// timeout and cancellable through <paramref name="options"/> or <see cref="Abort"/>. Retries
    /// between attempts belong to the caller. Returns false and records
    /// <see cref="LastNetworkError"/> on failure.
    /// </summary>
    public bool Open(string url, FfmpegNetworkOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        ArgumentNullException.ThrowIfNull(options);
        Close();

        FfmpegNetwork.EnsureInitialized();
        FfmpegLogCapture.EnsureInstalled();
        LastNetworkError = null;
        _networkReadFailed = false;
        _networkEndOfStream = false;
        _networkLogDiagnostics = 0;
        _networkUrl = url;
        _interrupt ??= new FfmpegInterrupt();
        _interrupt.Reset(options.Cancellation);
        _networkLogObserver ??= OnNetworkLog;
        _tlsFailureLines.Clear();
        IReadOnlyList<KeyValuePair<string, string>> tlsOptions = NetworkTlsRules.CreateFfmpegOptions(options.Tls);
        _tlsIoOpen ??= new FfmpegTlsIoOpen();
        _tlsIoOpen.SetOptions(tlsOptions);

        AVDictionary* dictionary = null;
        foreach (KeyValuePair<string, string> option in NetworkVideoPolicy.CreateFfmpegOptions(options.Retry))
            _ = ffmpeg.av_dict_set(&dictionary, option.Key, option.Value, 0);
        // http too: a redirect to https must be verified like a direct https URL.
        if (NetworkTlsRules.TakesTlsOptions(url))
        {
            foreach (KeyValuePair<string, string> option in tlsOptions)
                _ = ffmpeg.av_dict_set(&dictionary, option.Key, option.Value, 0);
        }

        try
        {
            AVFormatContext* context = ffmpeg.avformat_alloc_context();
            if (context == null)
            {
                LastNetworkError = "Could not allocate an FFmpeg format context.";
                _networkUrl = null;
                return false;
            }

            _interrupt.Install(context);
            _tlsIoOpen.Install(context);
            int result;
            using (FfmpegLogCapture.ObserveNetwork(_networkLogObserver))
            {
                // The connect timeout bounds the whole connect (DNS, TCP, TLS, request, headers);
                // avformat_open_input frees the context when it fails.
                _interrupt.BeginOperation(options.Retry.ConnectTimeout);
                result = ffmpeg.avformat_open_input(&context, url, null, &dictionary);
                _interrupt.EndOperation();
                if (result >= 0)
                {
                    _formatContext = context;

                    // Probing reads ahead (several HLS segments); each read is bounded by
                    // rw_timeout and the probe by twice the connect timeout.
                    _interrupt.BeginOperation(options.Retry.ConnectTimeout * 2);
                    result = ffmpeg.avformat_find_stream_info(_formatContext, null);
                    _interrupt.EndOperation();
                }
            }

            if (result < 0)
            {
                LastNetworkError = _interrupt.IsAborted || options.Cancellation.IsCancellationRequested
                    ? "cancelled"
                    : _tlsFailureLines.Count > 0
                        ? NetworkTlsRules.DescribeCertificateFailure(
                            Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) ? uri.Host : url,
                            _tlsFailureLines,
                            FfmpegNetwork.DescribeError(result),
                            FfmpegNetwork.GetTlsSetupWarning(url, options.Tls))
                        : FfmpegNetwork.DescribeError(result);
                Close();
                return false;
            }

            _interrupt.ReleaseCancellation();
            return true;
        }
        finally
        {
            ffmpeg.av_dict_free(&dictionary);
        }
    }

    /// <summary>
    /// Makes every blocking network call on this demuxer return at once, from any thread and
    /// without a lock (Close, Dispose, or a cancelled open). Local files are unaffected.
    /// </summary>
    public void Abort() => _interrupt?.Abort();

    /// <summary>
    /// What FFmpeg reported for the open network source: playlist or progressive, length, byte
    /// seeking, and duration (Stage 039A capability row).
    /// </summary>
    public NetworkVideoProbe GetNetworkProbe()
    {
        if (_formatContext == null)
            return default;

        var demuxerName = _formatContext->iformat != null
            ? System.Runtime.InteropServices.Marshal.PtrToStringUTF8((nint)_formatContext->iformat->name)
            : null;
        var isSegmented = NetworkVideoPolicy.IsSegmentedDemuxer(demuxerName);
        AVIOContext* io = _formatContext->pb;
        long? length = null;
        var byteSeeking = false;
        if (!isSegmented && io != null)
        {
            var size = ffmpeg.avio_size(io);
            length = size > 0 ? size : null;
            byteSeeking = (io->seekable & ffmpeg.AVIO_SEEKABLE_NORMAL) != 0;
        }

        var durationTicks = _formatContext->duration;
        var duration = durationTicks > 0 && durationTicks != ffmpeg.AV_NOPTS_VALUE
            ? durationTicks / (double)ffmpeg.AV_TIME_BASE
            : 0;
        return new NetworkVideoProbe(isSegmented, length, byteSeeking, duration);
    }

    /// <summary>
    /// Wraps <c>av_find_best_stream</c> for the given media type. Returns the
    /// stream index, or a negative FFmpeg error when no stream is found or the
    /// demuxer is closed.
    /// </summary>
    public int FindBestStream(AVMediaType mediaType) =>
        _formatContext == null
            ? ffmpeg.AVERROR_STREAM_NOT_FOUND
            : ffmpeg.av_find_best_stream(_formatContext, mediaType, -1, -1, null, 0);

    /// <summary>
    /// Returns the <see cref="AVStream"/> at <paramref name="index"/>, or null
    /// when closed or out of range. The pointer is owned by the format context
    /// and remains valid until <see cref="Close"/>.
    /// </summary>
    public AVStream* GetStream(int index) =>
        _formatContext != null && index >= 0 && (uint)index < _formatContext->nb_streams
            ? _formatContext->streams[index]
            : null;

    /// <summary>
    /// Sets the <c>discard</c> level on the stream at <paramref name="index"/>.
    /// Used to drop non-target streams so <see cref="ReadPacket"/> only returns
    /// packets of interest (mirrors the previous inline discard loop).
    /// </summary>
    public void SetStreamDiscard(int index, AVDiscard discard)
    {
        AVStream* stream = GetStream(index);
        if (stream != null)
            stream->discard = discard;
    }

    /// <summary>
    /// Reads the next packet into <paramref name="packet"/> via
    /// <c>av_read_frame</c>, returning its result code unchanged (0 on success,
    /// <see cref="ffmpeg.AVERROR_EOF"/> at end of stream, other negatives on
    /// error). Returns <see cref="ffmpeg.AVERROR_EOF"/> when closed.
    /// </summary>
    public int ReadPacket(AVPacket* packet)
    {
        if (_formatContext == null)
            return ffmpeg.AVERROR_EOF;

        if (_networkUrl == null)
            return ffmpeg.av_read_frame(_formatContext, packet);

        if (_networkReadFailed || _interrupt!.IsAborted)
            return ffmpeg.AVERROR_EOF;

        int result;
        using (FfmpegLogCapture.ObserveNetwork(_networkLogObserver!))
        {
            _interrupt.BeginOperation();
            result = ffmpeg.av_read_frame(_formatContext, packet);
            _interrupt.EndOperation();
        }

        if (result >= 0)
            return result;

        _networkEndOfStream = true;
        if (result == ffmpeg.AVERROR_EOF || _interrupt.IsAborted)
            return ffmpeg.AVERROR_EOF;

        // The stream dropped (FFmpeg's reconnects, if enabled, are exhausted): report it once and
        // end the stream instead of retrying a dead connection on every read.
        _networkReadFailed = true;
        DiagnosticRouter.Raise(
            MediaDiagnosticLevel.Error,
            NetworkDiagnosticCategory,
            $"Network read failed: {FfmpegNetwork.DescribeError(result)}; the stream ends here.",
            sourcePath: _networkUrl);
        return ffmpeg.AVERROR_EOF;
    }

    /// <summary>
    /// Wraps <c>av_seek_frame</c> against <paramref name="streamIndex"/>.
    /// Returns the FFmpeg result code (&lt; 0 on failure). Returns a negative
    /// value when closed.
    /// </summary>
    public int Seek(int streamIndex, long timestamp, int flags)
    {
        if (_formatContext == null)
            return ffmpeg.AVERROR_EOF;

        if (_networkUrl == null)
            return ffmpeg.av_seek_frame(_formatContext, streamIndex, timestamp, flags);

        if (_interrupt!.IsAborted)
            return ffmpeg.AVERROR_EXIT;

        // A network seek reconnects with a byte range (or reloads HLS segments): it is a read.
        int result;
        using (FfmpegLogCapture.ObserveNetwork(_networkLogObserver!))
        {
            _interrupt.BeginOperation();
            result = ffmpeg.av_seek_frame(_formatContext, streamIndex, timestamp, flags);
            _interrupt.EndOperation();
        }

        if (result >= 0)
        {
            _networkReadFailed = false;
            _networkEndOfStream = false;
        }

        return result;
    }

    /// <summary>Closes the container and releases the format context.</summary>
    public void Close()
    {
        if (_formatContext != null)
        {
            fixed (AVFormatContext** pfmt = &_formatContext)
                ffmpeg.avformat_close_input(pfmt);
        }

        _networkUrl = null;
        _networkReadFailed = false;
        _networkEndOfStream = false;
    }

    public void Dispose()
    {
        Close();
        _interrupt?.Dispose();
        _interrupt = null;
        _tlsIoOpen?.Dispose();
        _tlsIoOpen = null;
    }

    private void OnNetworkLog(int level, string line)
    {
        // FFmpeg's HTTP layer logs each reconnect ("Will reconnect at ... in N second(s)") on the
        // reading thread; those are the mid-stream retries of the host's policy. Other lines (a
        // short body, a skipped HLS segment) are warnings: whether the stream ends is reported
        // once, by ReadPacket. A TLS line about the certificate is kept for the open's error.
        if (_tlsFailureLines.Count < 3 && line.StartsWith("[tls @", StringComparison.Ordinal) && NetworkTlsRules.IsCertificateFailure(line))
            _tlsFailureLines.Add(line);

        if (_networkLogDiagnostics >= MaxNetworkLogDiagnostics)
            return;

        _networkLogDiagnostics++;
        var isRetry = line.Contains("reconnect", StringComparison.OrdinalIgnoreCase);
        DiagnosticRouter.Raise(
            MediaDiagnosticLevel.Warning,
            NetworkDiagnosticCategory,
            isRetry ? $"Network retry: {line}" : $"FFmpeg ({(level <= ffmpeg.AV_LOG_ERROR ? "error" : "warning")}): {line}",
            sourcePath: _networkUrl);
    }
}
