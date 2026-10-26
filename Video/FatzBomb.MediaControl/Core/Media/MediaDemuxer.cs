using FatzBomb.MediaControl.Core.Network;
using FatzBomb.MediaControl.Core.Video;
using FFmpeg.AutoGen;
using System.Diagnostics;
using System.Runtime.InteropServices;

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
/// <para>
/// In-stream titles (ENG-034, https audio): a progressive source with ICY headers, or an Ogg
/// stream, is polled on the reading thread (FFmpeg's HTTP context is not thread-safe) for
/// FFmpeg's <c>icy_metadata_packet</c> and for Ogg comment updates. Each change is recorded with
/// the time of the packet read when it was seen, so it can be announced when playback gets there
/// (<see cref="TryTakeStreamMetadata"/>) rather than when the read-ahead downloaded it.
/// </para>
/// <para>
/// A progressive MP4 on a server without byte ranges is opened only if its first packet can be
/// read (ENG-034): when the index (<c>moov</c>) follows the media data FFmpeg cannot go back to
/// it, and the open fails with <see cref="NetworkVideoPolicy.UnstreamableMp4Error"/> instead of
/// the first read ending the stream. The probed packet is returned by the next read.
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
    private int _networkFailures;
    private Action<int, string>? _networkLogObserver;
    // In-stream metadata (ENG-034): polled on the reading thread, taken by a consumer under the gate.
    private const int MaxStreamMetadataMarks = 16;
    private readonly Lock _metadataGate = new();
    private readonly Queue<StreamMetadataMark> _metadataMarks = new();
    private bool _pollsIcyMetadata;
    private bool _pollsOggMetadata;
    private string? _lastIcyPacket;
    // The packet the open read to check that a non-seekable MP4 streams; the next read returns it.
    private AVPacket* _probedPacket;

    /// <summary>True while a network source is open.</summary>
    public bool IsNetwork => _networkUrl != null && _formatContext != null;

    /// <summary>The FFmpeg error of the last failed network open, or null.</summary>
    public string? LastNetworkError { get; private set; }

    /// <summary>How long the network read in progress has been blocked (zero when none is).</summary>
    public TimeSpan NetworkReadStall => _interrupt?.CurrentOperationDuration ?? TimeSpan.Zero;

    /// <summary>
    /// How many network failures FFmpeg logged since the open (error lines and skipped HLS
    /// segments). The read-ahead tells a dropped network from the real end of the source with it.
    /// </summary>
    public int NetworkFailureCount => Volatile.Read(ref _networkFailures);

    /// <summary>
    /// Whether <see cref="ReadPacket"/> reports a failed network read (default). The read-ahead
    /// turns it off while it can reconnect, and reports the failure itself when it gives up.
    /// </summary>
    public bool ReportReadFailures { get; set; } = true;

    /// <summary>The error of the network read that failed (the stream dropped), or null; cleared by a successful seek.</summary>
    public string? FailedReadError { get; private set; }

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
    /// Format-level start time in <see cref="ffmpeg.AV_TIME_BASE"/> units (<c>_fmt->start_time</c>),
    /// or 0 when closed or unknown. Packet timestamps run from here to here plus the duration.
    /// </summary>
    public long FormatStartTicks => _formatContext != null && _formatContext->start_time != ffmpeg.AV_NOPTS_VALUE ? _formatContext->start_time : 0;

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
        FailedReadError = null;
        _networkEndOfStream = false;
        _networkLogDiagnostics = 0;
        _networkFailures = 0;
        ResetStreamMetadata();
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
                _ = _interrupt.EndOperation();
                if (result >= 0)
                {
                    _formatContext = context;

                    // Probing reads ahead (several HLS segments); each read is bounded by
                    // rw_timeout and the probe by twice the connect timeout.
                    _interrupt.BeginOperation(options.Retry.ConnectTimeout * 2);
                    result = ffmpeg.avformat_find_stream_info(_formatContext, null);
                    _ = _interrupt.EndOperation();
                }

                if (result >= 0)
                    result = ProbeStreamableMp4(options.Retry.ConnectTimeout);
            }

            if (result < 0)
            {
                LastNetworkError = _interrupt.IsAborted || options.Cancellation.IsCancellationRequested
                    ? "cancelled"
                    : result == UnstreamableMp4Result
                        ? NetworkVideoPolicy.UnstreamableMp4Error
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
            StartStreamMetadata();
            return true;
        }
        finally
        {
            ffmpeg.av_dict_free(&dictionary);
        }
    }

    // ProbeStreamableMp4's result for an MP4 that cannot be streamed from this server.
    private const int UnstreamableMp4Result = int.MinValue;

    /// <summary>
    /// A progressive MP4 that the server cannot serve by byte ranges plays only if its media data
    /// can still be read: reads one packet (bounded by the connect timeout) and keeps it for the
    /// next <see cref="ReadPacket"/>. Returns 0 (anything else passes),
    /// <see cref="UnstreamableMp4Result"/>, or FFmpeg's error when the read timed out.
    /// </summary>
    private int ProbeStreamableMp4(TimeSpan timeout)
    {
        AVIOContext* io = _formatContext->pb;
        if (io == null || (io->seekable & ffmpeg.AVIO_SEEKABLE_NORMAL) != 0 || !IsDemuxer("mov"))
            return 0;

        AVPacket* packet = ffmpeg.av_packet_alloc();
        if (packet == null)
            return 0;

        var started = Stopwatch.GetTimestamp();
        _interrupt!.BeginOperation(timeout);
        var result = ffmpeg.av_read_frame(_formatContext, packet);
        _ = _interrupt.EndOperation();
        if (result >= 0)
        {
            _probedPacket = packet;
            return 0;
        }

        ffmpeg.av_packet_free(&packet);
        // A slow first packet is a network failure (retried), not an unstreamable file.
        return Stopwatch.GetElapsedTime(started) >= timeout ? result : UnstreamableMp4Result;
    }

    private void FreeProbedPacket()
    {
        if (_probedPacket == null)
            return;

        AVPacket* packet = _probedPacket;
        _probedPacket = null;
        ffmpeg.av_packet_free(&packet);
    }

    /// <summary>Whether the open container's demuxer list (e.g. "mov,mp4,m4a,3gp,3g2,mj2") names <paramref name="name"/>.</summary>
    private bool IsDemuxer(string name)
    {
        if (_formatContext == null || _formatContext->iformat == null)
            return false;

        var names = Marshal.PtrToStringUTF8((nint)_formatContext->iformat->name);
        return names is not null && names.Split(',').Contains(name, StringComparer.Ordinal);
    }

    /// <summary>
    /// Makes every blocking network call on this demuxer return at once, from any thread and
    /// without a lock (Close, Dispose, or a cancelled open). Local files are unaffected.
    /// </summary>
    public void Abort() => _interrupt?.Abort();

    /// <summary>
    /// Makes a network read or seek that started before <paramref name="timestamp"/> (a
    /// <see cref="System.Diagnostics.Stopwatch"/> timestamp) return at once with
    /// <see cref="ffmpeg.AVERROR_EXIT"/>; later ones are unaffected and the stream stays usable
    /// (ENG-035: a newer seek supersedes I/O for the old position). From any thread, lock-free.
    /// Local files are unaffected.
    /// </summary>
    public void SupersedeOperationsStartedBefore(long timestamp) => _interrupt?.SupersedeOperationsStartedBefore(timestamp);

    /// <summary>
    /// Gives up the network read or seek in progress, with <see cref="ffmpeg.AVERROR_EXIT"/> as for
    /// a superseded one, while <paramref name="condition"/> holds (see
    /// <see cref="FfmpegInterrupt.SetOperationCancellation"/>). Network sources only; null removes it.
    /// </summary>
    public void SetOperationCancellation(Func<bool>? condition) => _interrupt?.SetOperationCancellation(condition);

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
        // The ICY headers as the open read them: the reader may be using the HTTP context by now.
        return new NetworkVideoProbe(isSegmented, length, byteSeeking, duration, _pollsIcyMetadata);
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
    /// The video stream to play. A local file keeps FFmpeg's best stream. A network source with
    /// several video renditions (HLS variants, DASH representations) plays the one
    /// <see cref="NetworkVariantPolicy"/> picks, so the picture and audio cursors agree on it.
    /// </summary>
    public int SelectVideoStream()
    {
        if (IsNetwork)
        {
            var chosen = NetworkVariantPolicy.Select(GetVideoVariantCandidates());
            if (chosen >= 0)
                return chosen;
        }

        return FindBestStream(AVMediaType.AVMEDIA_TYPE_VIDEO);
    }

    /// <summary>
    /// The audio stream to play. A network source takes the audio of the chosen video variant's
    /// program (its own audio, or the shared audio rendition HLS adds to each program that uses
    /// it); FFmpeg falls back to every stream when that program has none. A local file keeps
    /// FFmpeg's best audio stream, as before.
    /// </summary>
    public int SelectAudioStream()
    {
        if (_formatContext == null || !IsNetwork)
            return FindBestStream(AVMediaType.AVMEDIA_TYPE_AUDIO);

        var video = SelectVideoStream();
        return ffmpeg.av_find_best_stream(_formatContext, AVMediaType.AVMEDIA_TYPE_AUDIO, -1, video >= 0 ? video : -1, null, 0);
    }

    /// <summary>
    /// Discards every stream but <paramref name="keep"/> (negative entries are ignored). For HLS
    /// and DASH this is what stops FFmpeg from downloading the segments of the other variants.
    /// </summary>
    public void DiscardAllStreamsExcept(ReadOnlySpan<int> keep)
    {
        for (var index = 0; index < (int)StreamCount; index++)
        {
            if (!keep.Contains(index))
                SetStreamDiscard(index, AVDiscard.AVDISCARD_ALL);
        }
    }

    private List<NetworkVariantCandidate> GetVideoVariantCandidates()
    {
        var candidates = new List<NetworkVariantCandidate>();
        for (var index = 0; index < (int)StreamCount; index++)
        {
            AVStream* stream = _formatContext->streams[index];
            if (stream->codecpar->codec_type != AVMediaType.AVMEDIA_TYPE_VIDEO
                || (stream->disposition & ffmpeg.AV_DISPOSITION_ATTACHED_PIC) != 0)
            {
                continue;
            }

            // HLS and DASH tag each stream with its variant's advertised bandwidth.
            AVDictionaryEntry* entry = ffmpeg.av_dict_get(stream->metadata, "variant_bitrate", null, 0);
            var bandwidth = entry != null && long.TryParse(
                System.Runtime.InteropServices.Marshal.PtrToStringUTF8((nint)entry->value),
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out var advertised)
                ? advertised
                : stream->codecpar->bit_rate;
            candidates.Add(new NetworkVariantCandidate(index, bandwidth, stream->codecpar->width, stream->codecpar->height));
        }

        return candidates;
    }

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
        // Only a change is written: the network read-ahead's reader may be reading meanwhile.
        AVStream* stream = GetStream(index);
        if (stream != null && stream->discard != discard)
            stream->discard = discard;
    }

    /// <summary>
    /// Reads the next packet into <paramref name="packet"/> via
    /// <c>av_read_frame</c>, returning its result code unchanged (0 on success,
    /// <see cref="ffmpeg.AVERROR_EOF"/> at end of stream, other negatives on
    /// error). Returns <see cref="ffmpeg.AVERROR_EOF"/> when closed, and
    /// <see cref="ffmpeg.AVERROR_EXIT"/> for a network read a newer seek superseded
    /// (<see cref="SupersedeOperationsStartedBefore"/>). A network read gives up after
    /// <paramref name="timeout"/> when one is given (a reconnect within its window, ENG-034) and
    /// counts as failed ("timed out").
    /// </summary>
    public int ReadPacket(AVPacket* packet, TimeSpan? timeout = null)
    {
        if (_formatContext == null)
            return ffmpeg.AVERROR_EOF;

        if (_networkUrl == null)
            return ffmpeg.av_read_frame(_formatContext, packet);

        if (_networkReadFailed || _interrupt!.IsAborted)
            return ffmpeg.AVERROR_EOF;

        if (_probedPacket != null)
        {
            ffmpeg.av_packet_move_ref(packet, _probedPacket);
            FreeProbedPacket();
            return 0;
        }

        int result;
        bool superseded;
        bool timedOut;
        using (FfmpegLogCapture.ObserveNetwork(_networkLogObserver!))
        {
            _interrupt.BeginOperation(timeout);
            result = ffmpeg.av_read_frame(_formatContext, packet);
            timedOut = result < 0 && _interrupt.IsDeadlinePassed;
            superseded = _interrupt.EndOperation();
        }

        if (result >= 0)
        {
            PollStreamMetadata(packet);
            return result;
        }

        // A newer seek gave up this read: the stream is fine, the caller seeks next.
        if (superseded && !_interrupt.IsAborted)
            return ffmpeg.AVERROR_EXIT;

        _networkEndOfStream = true;
        if (result == ffmpeg.AVERROR_EOF || _interrupt.IsAborted)
            return ffmpeg.AVERROR_EOF;

        // The stream dropped (FFmpeg's reconnects, if enabled, are exhausted): report it once and
        // end the stream instead of retrying a dead connection on every read.
        _networkReadFailed = true;
        FailedReadError = timedOut ? "timed out" : FfmpegNetwork.DescribeError(result);
        if (!ReportReadFailures)
            return ffmpeg.AVERROR_EOF;

        DiagnosticRouter.Raise(
            MediaDiagnosticLevel.Error,
            NetworkDiagnosticCategory,
            $"Network read failed: {FailedReadError}; the stream ends here.",
            sourcePath: _networkUrl);
        return ffmpeg.AVERROR_EOF;
    }

    /// <summary>
    /// Wraps <c>av_seek_frame</c> against <paramref name="streamIndex"/>.
    /// Returns the FFmpeg result code (&lt; 0 on failure). Returns a negative
    /// value when closed. A network seek gives up after <paramref name="timeout"/> when one is
    /// given (a reconnect within its window, ENG-034).
    /// </summary>
    public int Seek(int streamIndex, long timestamp, int flags, TimeSpan? timeout = null)
    {
        if (_formatContext == null)
            return ffmpeg.AVERROR_EOF;

        if (_networkUrl == null)
            return ffmpeg.av_seek_frame(_formatContext, streamIndex, timestamp, flags);

        if (_interrupt!.IsAborted)
            return ffmpeg.AVERROR_EXIT;

        FreeProbedPacket();
        // A network seek reconnects with a byte range (or reloads HLS segments): it is a read.
        int result;
        using (FfmpegLogCapture.ObserveNetwork(_networkLogObserver!))
        {
            _interrupt.BeginOperation(timeout);
            result = ffmpeg.av_seek_frame(_formatContext, streamIndex, timestamp, flags);
            _ = _interrupt.EndOperation();
        }

        if (result >= 0)
        {
            _networkReadFailed = false;
            FailedReadError = null;
            _networkEndOfStream = false;
            // FFmpeg keeps the I/O error of a dropped connection on the context and reports the
            // real end of the source as that error (demux.c: "a demuxer might have returned EOF
            // because of an IO error"). The seek reconnected, so the error is over (ENG-034: after
            // a reconnect, the end of a progressive file read as a new drop).
            if (_formatContext->pb != null)
                _formatContext->pb->error = 0;
        }

        return result;
    }

    /// <summary>Closes the container and releases the format context.</summary>
    public void Close()
    {
        FreeProbedPacket();
        ResetStreamMetadata();
        if (_formatContext != null)
        {
            fixed (AVFormatContext** pfmt = &_formatContext)
                ffmpeg.avformat_close_input(pfmt);
        }

        _networkUrl = null;
        _networkReadFailed = false;
        FailedReadError = null;
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

    /// <summary>
    /// Takes the in-stream title change that playback has reached: the latest one recorded at or
    /// before <paramref name="playedUpToSeconds"/> (the played position on the container's
    /// timeline). Thread-safe; never waits for network I/O.
    /// </summary>
    public bool TryTakeStreamMetadata(double playedUpToSeconds, out StreamMetadata metadata)
    {
        metadata = default;
        var found = false;
        lock (_metadataGate)
        {
            while (_metadataMarks.TryPeek(out StreamMetadataMark mark) && mark.AtSeconds <= playedUpToSeconds)
            {
                metadata = _metadataMarks.Dequeue().Metadata;
                found = true;
            }
        }

        return found;
    }

    /// <summary>Starts polling for in-stream titles after a successful open, and records the title the open already read.</summary>
    private void StartStreamMetadata()
    {
        var isSegmented = _formatContext->iformat != null
            && NetworkVideoPolicy.IsSegmentedDemuxer(Marshal.PtrToStringUTF8((nint)_formatContext->iformat->name));
        _pollsIcyMetadata = !isSegmented && ReadIoOption("icy_metadata_headers") is { Length: > 0 };
        _pollsOggMetadata = IsDemuxer("ogg");
        if (_pollsOggMetadata)
        {
            // The tags read with the header play from the start.
            for (var index = 0; index < (int)StreamCount; index++)
                _formatContext->streams[index]->event_flags |= ffmpeg.AVSTREAM_EVENT_FLAG_METADATA_UPDATED;
        }

        PollStreamMetadata(null);
    }

    private void ResetStreamMetadata()
    {
        _pollsIcyMetadata = false;
        _pollsOggMetadata = false;
        _lastIcyPacket = null;
        lock (_metadataGate)
            _metadataMarks.Clear();
    }

    /// <summary>
    /// On the reading thread, after every packet: records a changed ICY title or Ogg comment
    /// block, timed at <paramref name="packet"/> (null: the start). FFmpeg parses an ICY block
    /// when its I/O buffer reads past it, so the packet is at most that buffer (32 KB) before it.
    /// </summary>
    private void PollStreamMetadata(AVPacket* packet)
    {
        if (!_pollsIcyMetadata && !_pollsOggMetadata)
            return;

        StreamMetadata metadata = default;
        if (_pollsIcyMetadata)
        {
            if (ReadIoOption("icy_metadata_packet") is { Length: > 0 } icy && icy != _lastIcyPacket)
            {
                _lastIcyPacket = icy;
                metadata = IcyMetadataParser.ParseIcy(icy);
            }
        }

        if (_pollsOggMetadata && TakeOggCommentUpdate() is { } ogg && metadata == default)
            metadata = ogg;

        if (metadata == default)
            return;

        var at = packet == null ? double.NegativeInfinity : PacketSeconds(packet);
        lock (_metadataGate)
        {
            if (_metadataMarks.Count >= MaxStreamMetadataMarks)
                _ = _metadataMarks.Dequeue();
            _metadataMarks.Enqueue(new StreamMetadataMark(at, metadata));
        }
    }

    /// <summary>The title and artist of a stream whose Ogg comments FFmpeg updated (a new chained stream), clearing the flag.</summary>
    private StreamMetadata? TakeOggCommentUpdate()
    {
        StreamMetadata? update = null;
        for (var index = 0; index < (int)StreamCount; index++)
        {
            AVStream* stream = _formatContext->streams[index];
            if ((stream->event_flags & ffmpeg.AVSTREAM_EVENT_FLAG_METADATA_UPDATED) == 0)
                continue;

            stream->event_flags &= ~ffmpeg.AVSTREAM_EVENT_FLAG_METADATA_UPDATED;
            if (stream->discard == AVDiscard.AVDISCARD_ALL || update is not null)
                continue;

            List<string> comments = [];
            foreach (var key in OggTitleKeys)
            {
                AVDictionaryEntry* entry = ffmpeg.av_dict_get(stream->metadata, key, null, 0);
                if (entry != null && Marshal.PtrToStringUTF8((nint)entry->value) is { } value)
                    comments.Add($"{key}={value}");
            }

            StreamMetadata parsed = IcyMetadataParser.ParseOggComments(comments);
            if (parsed != default)
                update = parsed;
        }

        return update;
    }

    private double PacketSeconds(AVPacket* packet)
    {
        AVStream* stream = GetStream(packet->stream_index);
        var timestamp = packet->pts != ffmpeg.AV_NOPTS_VALUE ? packet->pts : packet->dts;
        return stream == null || timestamp == ffmpeg.AV_NOPTS_VALUE
            ? double.NegativeInfinity
            : timestamp * ffmpeg.av_q2d(stream->time_base);
    }

    /// <summary>
    /// Reads a string option of the open source's I/O context or the protocol under it (FFmpeg's
    /// HTTP <c>icy_metadata_*</c>). Only on the thread that reads, or before the reader starts.
    /// </summary>
    private string? ReadIoOption(string name)
    {
        if (_formatContext == null || _formatContext->pb == null)
            return null;

        byte* value = null;
        if (ffmpeg.av_opt_get(_formatContext->pb, name, ffmpeg.AV_OPT_SEARCH_CHILDREN, &value) < 0 || value == null)
            return null;

        try
        {
            return Marshal.PtrToStringUTF8((nint)value);
        }
        finally
        {
            ffmpeg.av_free(value);
        }
    }

    private static readonly string[] OggTitleKeys = ["TITLE", "ARTIST"];

    private readonly record struct StreamMetadataMark(double AtSeconds, StreamMetadata Metadata);

    private void OnNetworkLog(int level, string line)
    {
        // FFmpeg's HTTP layer logs each reconnect ("Will reconnect at ... in N second(s)") on the
        // reading thread; those are the mid-stream retries of the host's policy. Other lines (a
        // short body, a skipped HLS segment) are warnings: whether the stream ends is reported
        // once, by ReadPacket. A TLS line about the certificate is kept for the open's error.
        if (_tlsFailureLines.Count < 3 && line.StartsWith("[tls @", StringComparison.Ordinal) && NetworkTlsRules.IsCertificateFailure(line))
            _tlsFailureLines.Add(line);

        // Lines that follow the engine's own cancellation (a newer seek, Close) are expected
        // ("Immediate exit requested", "Failed to send close message"): not reported.
        if (_interrupt?.IsCancelledByOwner == true)
            return;

        if (level <= ffmpeg.AV_LOG_ERROR || line.Contains("Failed to open segment", StringComparison.Ordinal))
            _ = Interlocked.Increment(ref _networkFailures);

        if (_networkLogDiagnostics >= MaxNetworkLogDiagnostics)
            return;

        _networkLogDiagnostics++;
        var isRetry = line.Contains("reconnect", StringComparison.OrdinalIgnoreCase);
        DiagnosticRouter.Raise(
            isRetry || level <= ffmpeg.AV_LOG_ERROR ? MediaDiagnosticLevel.Warning : MediaDiagnosticLevel.Info,
            NetworkDiagnosticCategory,
            isRetry ? $"Network retry: {line}" : $"FFmpeg ({(level <= ffmpeg.AV_LOG_ERROR ? "error" : "warning")}): {line}",
            sourcePath: _networkUrl);
    }
}
