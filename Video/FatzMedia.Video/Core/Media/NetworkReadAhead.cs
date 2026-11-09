using System.Diagnostics;
using FFmpeg.AutoGen;

namespace FatzMedia.Video.Core.Media;

/// <summary>
/// The network read-ahead of one <see cref="SharedDemuxer"/> (Stage 039A slice 2): a background
/// reader thread owns every network read and seek of the format context and demuxes packets of
/// the registered streams into a <see cref="ReadAheadBuffer{T}"/>, up to the policy's duration or
/// byte cap. The picture and sound consumers only take packets from the queues, so they share one
/// download, and transport calls never wait for a stalled read.
/// </summary>
/// <remarks>
/// <para>
/// A consumer read waits for a packet unless the calling thread limited its waits
/// (<see cref="NoWait"/>, <see cref="WaitAtMost"/>, <see cref="CancelWaitsWith"/>,
/// <see cref="AbandonWaitsWhen"/>); a wait also gives up when a newer seek supersedes it
/// (<see cref="SupersedeWaitsStartedBefore"/>). A wait that gives up returns
/// <see cref="ffmpeg.AVERROR_EXIT"/>, which consumers already take for "not the end" (ENG-035).
/// An abort (Close, Dispose) makes reads return end of file. The playback loops never wait, so
/// Pause and Stop never wait for the network either.
/// </para>
/// <para>
/// A seek is served from the queues when <see cref="ReadAheadBuffer{T}.TrySeekWithin"/> allows it,
/// without any network request. Any other seek empties the queues and is handed to the reader,
/// which gives up the read in progress (an interrupt condition on the format context, no timing
/// window) and seeks; <see cref="Seek"/> returns at once and the next reads wait for the new
/// position's packets. A failed reader seek is reported as a diagnostic and reading goes on from
/// where the cursor is, as a failed container seek did before.
/// </para>
/// <para>
/// The reader stops at a cap only while no consumer is starved (found its queue empty since the
/// last packet arrived), so a consumer can never wait on a full buffer; twice the byte cap is a
/// hard limit. Lock order: this object's lock is a leaf, held for queue work only, never across
/// FFmpeg I/O.
/// </para>
/// <para>
/// A seekable source survives a dropped network: when a read fails, or the source ends early
/// (before its duration, with network failures logged), the reader reconnects by seeking back to
/// the last packet it queued, with growing pauses, for up to
/// <see cref="NetworkReadAheadPolicy.ReconnectFor"/>. Packets it already queued are skipped, so
/// playback goes on where the buffer ran out. Only when the window passes does the stream end.
/// </para>
/// </remarks>
sealed unsafe class NetworkReadAhead : IDisposable
{
    const string DiagnosticCategory = "Network";
    const int HardByteCapMultiplier = 2;
    // AVPacket and its buffer reference, on top of the payload.
    const long PacketOverheadBytes = 192;
    static readonly TimeSpan ConsumerWaitSlice = TimeSpan.FromMilliseconds(50);
    static readonly TimeSpan ReaderIdleSlice = TimeSpan.FromMilliseconds(250);
    static readonly TimeSpan ReaderJoinWarning = TimeSpan.FromSeconds(2);
    // An end this far before the duration, with network failures logged, is a dropped network.
    const double EarlyEndToleranceSeconds = 1.0;
    const double FirstReconnectDelaySeconds = 0.5;
    const double MaxReconnectDelaySeconds = 2.0;
    // A reconnect counts once this much new media was read: bytes FFmpeg had already received
    // before the drop do not end it.
    const double ReconnectedAfterSeconds = 1.0;

    // The calling thread's wait limits (WaitScope): a Stopwatch deadline (0 = none), a token and a condition.
    [ThreadStatic] static long _waitDeadline;
    [ThreadStatic] static CancellationToken _waitCancellation;
    [ThreadStatic] static Func<bool>? _waitAbandon;

    // How long reads on this thread have waited for packets in total (Stopwatch ticks).

    readonly MediaDemuxer _demuxer;
    readonly string? _sourcePath;
    readonly bool _reportSeekFailures;
    readonly object? _diagnosticOwner;
    readonly double _maxSeconds;
    readonly long _maxBytes;
    readonly double _resumeSeconds;
    readonly Lock _sync = new();
    readonly ReadAheadBuffer<nint> _buffer = new(ReleasePacket);
    readonly ManualResetEventSlim _readerWake = new(false);
    // One data-arrived event per registered stream (ENG-034: with a shared one, a consumer that
    // reset it could swallow the other's wake-up). Changed under _sync; the array is replaced, not
    // mutated, so the lock-free signals (Abort, a superseded wait) can read it.
    readonly Dictionary<int, ManualResetEventSlim> _arrivals = [];
    ManualResetEventSlim[] _arrivalEvents = [];
    // Streams to discard again (an unregistered consumer), applied by the reader between reads.
    readonly List<int> _pendingDiscards = [];
    Thread? _thread;
    bool _stopping;
    bool _disposed;
    volatile bool _aborted;
    bool _atEnd;
    bool _readerPaused;
    bool _hardCapReported;
    // The reader thread ran and returned (an abort or a failure): no one serves a seek any more.
    bool _readerExited;
    // Bumped by every seek the reader must make; the reader's I/O for an older one is given up.
    int _generation;
    int _readerGeneration;
    SeekRequest? _pendingSeek;
    long _supersededBefore;
    readonly double _reconnectSeconds;
    readonly double _endSeconds;
    // Reader state for reconnecting, under _sync. The last packet queued per stream since the
    // reader's last seek; after a reconnect, per stream, the timestamp up to which packets are
    // already queued; when the drop started (Stopwatch ticks, 0 = none) and the retries so far.
    readonly Dictionary<int, LastPacket> _lastQueued = [];
    readonly Dictionary<int, long> _queuedUpTo = [];
    int _failuresAtSeek;
    // The reader's last seek: where a reconnect goes when nothing was queued since (a seek during
    // the outage). Before any seek, the start.
    SeekRequest _lastSeek = new(-1, 0, ffmpeg.AVSEEK_FLAG_BACKWARD);
    long _dropStarted;
    // Reader thread only: the next read is the reconnect's first (bounded by its window).
    bool _boundNextReadToReconnectWindow;
    double _droppedAt;
    int _reconnectAttempts;

    /// <param name="demuxer">The open network source; the reader takes over its I/O.</param>
    /// <param name="policy">The caps and the resume threshold.</param>
    /// <param name="sourcePath">The URL, for diagnostics.</param>
    /// <param name="seekable">
    /// Whether the source is seekable with a known duration. Only then is a failed reader seek
    /// reported (a live source's Stop seeks to the start, which fails by design and playback goes
    /// on at the live edge) and a dropped network reconnected.
    /// </param>
    public NetworkReadAhead(MediaDemuxer demuxer, NetworkReadAheadPolicy policy, string? sourcePath, bool seekable = true)
    {
        ArgumentNullException.ThrowIfNull(demuxer);
        ArgumentNullException.ThrowIfNull(policy);
        _demuxer = demuxer;
        _sourcePath = sourcePath;
        _reportSeekFailures = seekable;
        // Packet timestamps are absolute: the source ends at its start time plus the duration.
        _endSeconds = (demuxer.FormatStartTicks + demuxer.FormatDurationTicks) / (double)ffmpeg.AV_TIME_BASE;
        _reconnectSeconds = seekable && demuxer.FormatDurationTicks > 0 ? policy.ReconnectFor.TotalSeconds : 0;
        // The reader reports a dropped stream itself, once it gives up reconnecting. Set either
        // way: the demuxer outlives this reader and is reopened for the next source.
        demuxer.ReportReadFailures = _reconnectSeconds <= 0;
        _maxSeconds = policy.MaxDuration.TotalSeconds;
        _maxBytes = policy.MaxBytes;
        _resumeSeconds = policy.ResumeDuration.TotalSeconds;
        // The reader thread raises diagnostics for the player that opened the source.
        _diagnosticOwner = DiagnosticRouter.Owner;
        _demuxer.SetOperationCancellation(IsReaderOperationStale);
    }

    /// <summary>Gets the media time buffered ahead on the lead stream (the picture when there is one), in seconds.</summary>
    public double BufferedSeconds
    {
        get
        {
            lock (_sync)
                return _buffer.BufferedSeconds;
        }
    }

    /// <summary>Gets the packet memory held, in bytes.</summary>
    public long BufferedBytes
    {
        get
        {
            lock (_sync)
                return _buffer.Bytes;
        }
    }

    /// <summary>Gets the progress towards ending a buffering spell, <c>0..1</c> (see <see cref="NetworkReadAheadPolicy.ResumeDuration"/>).</summary>
    public double ResumeFill
    {
        get
        {
            lock (_sync)
                return _atEnd && _pendingSeek is null ? 1 : _buffer.ResumeFill(_resumeSeconds, _maxSeconds, _maxBytes);
        }
    }

    /// <summary>Gets how long a consumer has found its queue empty (zero when none is waiting for data).</summary>
    public TimeSpan ConsumerStall
    {
        get
        {
            lock (_sync)
                return _buffer.LongestStarvation(Stopwatch.GetTimestamp());
        }
    }

    /// <summary>Gets whether the reader reached the end of the source (and no seek is pending).</summary>
    public bool IsAtEnd
    {
        get
        {
            lock (_sync)
                return _atEnd && _pendingSeek is null;
        }
    }

    /// <summary>Gets whether the reader is idle at a cap or at the end (tests).</summary>
    internal bool IsReaderPaused
    {
        get
        {
            lock (_sync)
                return _readerPaused && _pendingSeek is null;
        }
    }

    /// <summary>Gets how many seeks were served from the buffer (tests and diagnostics).</summary>
    internal int BufferSeeks { get; private set; }

    /// <summary>Gets how many seeks went to the network (tests and diagnostics).</summary>
    internal int NetworkSeeks { get; private set; }

    /// <summary>
    /// Gets how long consumer reads on the calling thread have waited for packets, in total, as a
    /// <see cref="Stopwatch"/> duration in ticks. A time-budgeted decode subtracts it, so its budget
    /// measures decoding rather than the download (a seek to a target outside the buffer).
    /// </summary>
    [field: ThreadStatic]
    public static long WaitedTicksOnThisThread { get; private set; }

    /// <summary>Reads and seeks on this thread do not wait for the network until the scope ends.</summary>
    public static WaitScope NoWait() => new(1, CancellationToken.None);

    /// <summary>Reads and seeks on this thread wait at most <paramref name="limit"/> in total until the scope ends.</summary>
    public static WaitScope WaitAtMost(TimeSpan limit) =>
        new(Stopwatch.GetTimestamp() + (long)(Math.Max(0, limit.TotalSeconds) * Stopwatch.Frequency) + 1, CancellationToken.None);

    /// <summary>Reads on this thread stop waiting once <paramref name="cancellation"/> is cancelled, until the scope ends.</summary>
    public static WaitScope CancelWaitsWith(CancellationToken cancellation) => new(0, cancellation);

    /// <summary>
    /// Reads on this thread stop waiting while <paramref name="abandon"/> holds, until the scope
    /// ends: a seek gives up its waits once a newer seek is queued, including waits that start
    /// after the request (ENG-035's timestamp only reaches the ones already running). Polled at
    /// least every 50 ms; it must be cheap.
    /// </summary>
    public static WaitScope AbandonWaitsWhen(Func<bool> abandon) => new(0, CancellationToken.None, abandon ?? throw new ArgumentNullException(nameof(abandon)));

    /// <summary>
    /// Keeps the packets of <paramref name="streamIndex"/>; the lead stream's time is what the
    /// duration cap measures. A stream whose track ends before the source does is ended there (see
    /// <see cref="ReadAheadBuffer{T}.StreamEndToleranceSeconds"/>).
    /// </summary>
    public void Register(int streamIndex, bool lead)
    {
        if (streamIndex < 0)
            return;

        var end = StreamEndSeconds(streamIndex);
        lock (_sync)
        {
            _buffer.Register(streamIndex, lead, end);
            _ = _pendingDiscards.Remove(streamIndex);
            if (!_arrivals.ContainsKey(streamIndex))
            {
                _arrivals[streamIndex] = new ManualResetEventSlim(false);
                Volatile.Write(ref _arrivalEvents, [.. _arrivals.Values]);
            }
        }
    }

    /// <summary>
    /// Stops keeping <paramref name="streamIndex"/> (its consumer closed) and drops its packets;
    /// with <paramref name="discard"/> the reader also stops demuxing it (for HLS and DASH, stops
    /// downloading it).
    /// </summary>
    public void Unregister(int streamIndex, bool discard = false)
    {
        lock (_sync)
        {
            _buffer.Unregister(streamIndex);
            if (_arrivals.Remove(streamIndex, out var arrival))
            {
                Volatile.Write(ref _arrivalEvents, [.. _arrivals.Values]);
                // A consumer still waiting finds its stream gone and ends.
                arrival.Set();
            }

            if (discard && streamIndex >= 0)
            {
                // No reader thread: nothing reads the format context meanwhile.
                if (_thread is null || _readerExited)
                    _demuxer.SetStreamDiscard(streamIndex, AVDiscard.AVDISCARD_ALL);
                else
                    _pendingDiscards.Add(streamIndex);
            }
        }

        _readerWake.Set();
    }

    /// <summary>Where the track of <paramref name="streamIndex"/> ends on the container timeline, or null when unknown.</summary>
    double? StreamEndSeconds(int streamIndex)
    {
        var stream = _demuxer.GetStream(streamIndex);
        if (stream == null || stream->duration <= 0 || stream->duration == ffmpeg.AV_NOPTS_VALUE)
            return null;

        var start = stream->start_time != ffmpeg.AV_NOPTS_VALUE ? stream->start_time : 0;
        var end = (start + stream->duration) * ffmpeg.av_q2d(stream->time_base);
        return double.IsFinite(end) && end > 0 ? end : null;
    }

    /// <summary>
    /// Takes the next packet of <paramref name="streamIndex"/> into <paramref name="packet"/>: 0, or
    /// <see cref="ffmpeg.AVERROR_EOF"/> at the end (and after an abort), or
    /// <see cref="ffmpeg.AVERROR_EXIT"/> when the wait for one gave up.
    /// </summary>
    public int Read(int streamIndex, AVPacket* packet)
    {
        long waitStarted = 0;
        while (true)
        {
            ManualResetEventSlim? arrival;
            lock (_sync)
            {
                if (_disposed || _aborted || !_buffer.IsRegistered(streamIndex))
                    return ffmpeg.AVERROR_EOF;

                EnsureReaderStartedNoLock();
                var now = Stopwatch.GetTimestamp();
                switch (_buffer.TryTake(streamIndex, now, out var item))
                {
                    case ReadAheadTake.Packet:
                        var buffered = (AVPacket*)item;
                        ffmpeg.av_packet_move_ref(packet, buffered);
                        ffmpeg.av_packet_free(&buffered);
                        // Only an idle reader needs waking: a busy one checks the caps on its next pass.
                        if (_readerPaused)
                            _readerWake.Set();
                        return 0;
                    case ReadAheadTake.EndOfStream:
                        return ffmpeg.AVERROR_EOF;
                }

                if (_atEnd && _pendingSeek is null)
                {
                    // A stream re-armed for a loop wrap waits for the picture's wrap to bring the
                    // next lap; ending it again would make its consumer wrap once per read.
                    if (_buffer.IsAwaitingNextLap(streamIndex))
                        return ffmpeg.AVERROR_EXIT;

                    _buffer.MarkEnded(streamIndex);
                    return ffmpeg.AVERROR_EOF;
                }

                // Starved: the reader goes on past a cap for this consumer.
                _readerWake.Set();
                if (waitStarted == 0)
                    waitStarted = now;
                if (ShouldGiveUpWait(waitStarted, now))
                    return ffmpeg.AVERROR_EXIT;

                if (!_arrivals.TryGetValue(streamIndex, out arrival))
                    return ffmpeg.AVERROR_EOF;

                arrival.Reset();
            }

            WaitForData(arrival);
        }
    }

    /// <summary>
    /// Seeks <paramref name="streamIndex"/> to <paramref name="timestamp"/> (its time base; a negative
    /// index means <see cref="ffmpeg.AV_TIME_BASE"/> units, as for <c>av_seek_frame</c>). Served from
    /// the buffer when possible; otherwise the queues are emptied and the reader seeks. Returns 0, or
    /// <see cref="ffmpeg.AVERROR_EXIT"/> after an abort.
    /// </summary>
    public int Seek(int streamIndex, long timestamp, int flags)
    {
        lock (_sync)
        {
            if (_disposed || _aborted)
                return ffmpeg.AVERROR_EXIT;

            EnsureReaderStartedNoLock();
            var seekStream = streamIndex >= 0 ? streamIndex : _buffer.LeadStream;
            if (_pendingSeek is null && _buffer.TrySeekWithin(seekStream, ToSeconds(streamIndex, timestamp)))
            {
                BufferSeeks++;
                _readerWake.Set();
                return 0;
            }

            _buffer.Flush();
            RequestReaderSeekNoLock(new SeekRequest(streamIndex, timestamp, flags));
            return 0;
        }
    }

    /// <summary>
    /// The picture's side of a gapless loop wrap: the reader is at the end, the other streams keep
    /// their tail packets and end marker, the picture's queue is emptied, and the reader seeks
    /// <paramref name="videoStreamIndex"/> to 0 so the next lap's packets follow the tails.
    /// </summary>
    public void RequestLoopWrap(int videoStreamIndex)
    {
        lock (_sync)
        {
            if (_disposed || _aborted || videoStreamIndex < 0)
                return;

            EnsureReaderStartedNoLock();
            _buffer.FlushStream(videoStreamIndex);
            RequestReaderSeekNoLock(new SeekRequest(videoStreamIndex, 0, ffmpeg.AVSEEK_FLAG_BACKWARD));
        }
    }

    /// <summary>Re-arms a stream after its end (the sound's side of a loop wrap).</summary>
    public void ClearEnd(int streamIndex)
    {
        lock (_sync)
            _buffer.ClearEnd(streamIndex);
    }

    /// <summary>
    /// Makes a consumer wait that started before <paramref name="timestamp"/> (a
    /// <see cref="Stopwatch"/> timestamp) give up; the reader is unaffected (ENG-035: a newer seek).
    /// Lock-free.
    /// </summary>
    public void SupersedeWaitsStartedBefore(long timestamp)
    {
        var current = Interlocked.Read(ref _supersededBefore);
        while (timestamp > current)
        {
            var seen = Interlocked.CompareExchange(ref _supersededBefore, timestamp, current);
            if (seen == current)
                break;

            current = seen;
        }

        SignalAllArrivals();
    }

    /// <summary>Aborts the reader's network I/O and every wait, from any thread, without a lock.</summary>
    public void Abort()
    {
        _aborted = true;
        _demuxer.Abort();
        SignalAllArrivals();
        _readerWake.Set();
    }

    /// <summary>Aborts and joins the reader and releases every queued packet. The format context stays open.</summary>
    public void Dispose()
    {
        Thread? thread;
        lock (_sync)
        {
            if (_disposed || _stopping)
                return;

            _stopping = true;
            thread = _thread;
        }

        Abort();
        if (thread is not null && !ReferenceEquals(thread, Thread.CurrentThread) && !thread.Join(ReaderJoinWarning))
        {
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Warning, DiagnosticCategory, "The network reader did not stop within 2 s of the abort; waiting for it.", sourcePath: _sourcePath);
            thread.Join();
        }

        lock (_sync)
        {
            _disposed = true;
            _pendingSeek = null;
            _buffer.Flush();
        }

        _demuxer.SetOperationCancellation(null);
    }

    bool IsReaderOperationStale() => Volatile.Read(ref _readerGeneration) != Volatile.Read(ref _generation);

    void RequestReaderSeekNoLock(SeekRequest request)
    {
        // A reader that exited cannot seek: leave the queues empty at the end rather than queue a
        // request that reads would wait on forever.
        if (_readerExited)
            return;

        Volatile.Write(ref _generation, _generation + 1);
        _pendingSeek = request;
        NetworkSeeks++;
        _readerWake.Set();
    }

    bool ShouldGiveUpWait(long waitStarted, long now) =>
        waitStarted < Interlocked.Read(ref _supersededBefore)
        || (_waitDeadline != 0 && now >= _waitDeadline)
        || _waitCancellation.IsCancellationRequested
        || _waitAbandon?.Invoke() == true;

    /// <summary>Wakes every waiting consumer. Lock-free.</summary>
    void SignalAllArrivals()
    {
        foreach (var arrival in Volatile.Read(ref _arrivalEvents))
            arrival.Set();
    }

    static void WaitForData(ManualResetEventSlim arrival)
    {
        var timeout = ConsumerWaitSlice;
        if (_waitDeadline != 0)
        {
            var remaining = Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), _waitDeadline);
            if (remaining < timeout)
                timeout = remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }

        var started = Stopwatch.GetTimestamp();
        try
        {
            _ = arrival.Wait(timeout, _waitCancellation);
        }
        catch (OperationCanceledException)
        {
            // Checked again by the caller.
        }

        WaitedTicksOnThisThread += Stopwatch.GetTimestamp() - started;
    }

    void EnsureReaderStartedNoLock()
    {
        if (_thread is not null || _stopping)
            return;

        _thread = new Thread(Run) { IsBackground = true, Name = "FatzMedia.Video.NetworkReadAhead" };
        _thread.Start();
    }

    bool ShouldPauseNoLock(out bool reportHardCap)
    {
        reportHardCap = false;
        if (_buffer.Bytes >= _maxBytes * HardByteCapMultiplier)
        {
            reportHardCap = !_hardCapReported;
            _hardCapReported = true;
            return true;
        }

        return _buffer.IsFull(_maxSeconds, _maxBytes) && !_buffer.HasStarvedStream;
    }

    void Run()
    {
        using var owner = DiagnosticRouter.EnterOwner(_diagnosticOwner);
        var packet = ffmpeg.av_packet_alloc();
        try
        {
            if (packet != null)
                ReadLoop(packet);
        }
        catch (Exception exception)
        {
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Error, DiagnosticCategory, $"Network reader failed: {exception.Message}; the stream ends here.", sourcePath: _sourcePath, innerException: exception);
        }
        finally
        {
            ffmpeg.av_packet_free(&packet);
            lock (_sync)
            {
                // Whatever stopped the reader, consumers find the end instead of waiting.
                if (!_atEnd && !_stopping)
                {
                    _atEnd = true;
                    _buffer.AddEndOfStream();
                }

                _readerPaused = true;
                _readerExited = true;
                _pendingSeek = null;
            }

            SignalAllArrivals();
        }
    }

    void ReadLoop(AVPacket* packet)
    {
        while (true)
        {
            SeekRequest? seek;
            int generation;
            bool idle;
            var reportHardCap = false;
            long bytes;
            lock (_sync)
            {
                if (_stopping || _aborted)
                    return;

                // The format context is the reader's: stream discards change between its reads,
                // and under _sync so a consumer that registered again meanwhile is never discarded.
                foreach (var index in _pendingDiscards)
                {
                    if (!_buffer.IsRegistered(index))
                        _demuxer.SetStreamDiscard(index, AVDiscard.AVDISCARD_ALL);
                }

                _pendingDiscards.Clear();

                generation = _generation;
                seek = _pendingSeek;
                _pendingSeek = null;
                if (seek is { } next)
                    ForgetQueuedNoLock(next);
                idle = seek is null && (_atEnd || ShouldPauseNoLock(out reportHardCap));
                _readerPaused = idle;
                bytes = _buffer.Bytes;
                if (idle)
                    _readerWake.Reset();
            }

            if (reportHardCap)
            {
                DiagnosticRouter.Raise(
                    MediaDiagnosticLevel.Warning,
                    DiagnosticCategory,
                    $"Network read-ahead stopped at {bytes / (1024 * 1024)} MB: one stream is starved while another is buffered far ahead.",
                    sourcePath: _sourcePath);
            }

            if (idle)
            {
                _ = _readerWake.Wait(ReaderIdleSlice);
                continue;
            }

            Volatile.Write(ref _readerGeneration, generation);
            if (seek is { } request)
            {
                var result = _demuxer.Seek(request.StreamIndex, request.Timestamp, request.Flags);
                bool failed;
                lock (_sync)
                {
                    failed = _reportSeekFailures && result < 0 && generation == _generation && !_aborted;
                    if (result >= 0 && generation == _generation)
                        _atEnd = false;
                }

                if (failed)
                    DiagnosticRouter.Raise(MediaDiagnosticLevel.Warning, DiagnosticCategory, $"Network seek failed: {FfmpegNetwork.DescribeError(result)}; playback goes on from the current position.", sourcePath: _sourcePath);

                continue;
            }

            // The first read after a reconnect (where HLS connects) is bounded by what is left of
            // the window (ENG-034: a connect that hangs would otherwise hold the reader for
            // rw_timeout). Once data flows again, reads are not cut short by the window.
            var dropStarted = Interlocked.Read(ref _dropStarted);
            TimeSpan? readTimeout = !_boundNextReadToReconnectWindow || dropStarted == 0
                ? null
                : TimeSpan.FromSeconds(Math.Max(0.05, _reconnectSeconds - Stopwatch.GetElapsedTime(dropStarted).TotalSeconds));
            var read = _demuxer.ReadPacket(packet, readTimeout);
            if (read >= 0)
                _boundNextReadToReconnectWindow = false;
            var dropped = false;
            string? unreportedFailure = null;
            double? resumedAfter = null;
            lock (_sync)
            {
                if (generation != _generation || _stopping)
                {
                    ffmpeg.av_packet_unref(packet);
                    continue;
                }

                if (read >= 0)
                {
                    resumedAfter = EnqueueNoLock(packet);
                }
                else if (read != ffmpeg.AVERROR_EXIT)
                {
                    // End of file, a dropped stream or an abort.
                    dropped = !_aborted && IsDroppedNoLock();
                    if (!dropped)
                    {
                        // The demuxer leaves failures to this reader while it can reconnect;
                        // one it cannot reconnect (nothing read since a seek) is reported here.
                        if (!_aborted && _reconnectSeconds > 0)
                            unreportedFailure = _demuxer.FailedReadError;
                        EndNoLock();
                    }
                }
            }

            if (unreportedFailure is not null)
                DiagnosticRouter.Raise(MediaDiagnosticLevel.Error, DiagnosticCategory, $"Network read failed: {unreportedFailure}; the stream ends here.", sourcePath: _sourcePath);

            if (resumedAfter is { } seconds)
                DiagnosticRouter.Raise(MediaDiagnosticLevel.Info, DiagnosticCategory, $"Network back: reading again after {seconds:F1} s.", sourcePath: _sourcePath);

            if (dropped && !Reconnect(generation))
            {
                var error = _demuxer.FailedReadError ?? "the connection was lost";
                DiagnosticRouter.Raise(
                    MediaDiagnosticLevel.Error,
                    DiagnosticCategory,
                    $"Network read failed: {error}; no connection for {_reconnectSeconds:F0} s, the stream ends here.",
                    sourcePath: _sourcePath);
                lock (_sync)
                {
                    if (generation == _generation && !_stopping)
                        EndNoLock();
                }
            }
        }
    }

    void EndNoLock()
    {
        _atEnd = true;
        _buffer.AddEndOfStream();
        SignalAllArrivals();
    }

    /// <summary>
    /// Whether a failed or ended read is a dropped network the reader can reconnect: the source
    /// is seekable and the read failed, or the source ended with network failures logged since
    /// the reader's last seek, before its duration (an HLS demuxer skips the segments it cannot
    /// fetch and then ends) or before anything was read. After a reconnect the count starts
    /// again, so a source whose real end falls short of its duration ends after one try.
    /// </summary>
    bool IsDroppedNoLock()
    {
        if (_reconnectSeconds <= 0)
            return false;

        if (_demuxer.FailedReadError is not null)
            return true;

        if (_demuxer.NetworkFailureCount == _failuresAtSeek)
            return false;

        if (_lastQueued.Count == 0)
            return true;

        var readUpTo = 0.0;
        foreach (var last in _lastQueued.Values)
            readUpTo = Math.Max(readUpTo, last.EndSeconds);
        return readUpTo < _endSeconds - EarlyEndToleranceSeconds;
    }

    /// <summary>
    /// Seeks back to the last packets queued (or repeats the reader's last seek when nothing was
    /// queued since) until the source answers, pausing between attempts (0.5 s, doubling up to
    /// 2 s) within the reconnect window. Returns true to read on (it reconnected, or a consumer
    /// seek or a stop took over), false when the window passed.
    /// </summary>
    bool Reconnect(int generation)
    {
        var now = Stopwatch.GetTimestamp();
        bool firstDrop;
        double resumeAt;
        int attempt;
        SeekRequest target;
        lock (_sync)
        {
            resumeAt = double.MaxValue;
            foreach (var last in _lastQueued.Values)
                resumeAt = Math.Min(resumeAt, last.StartSeconds);
            target = _lastQueued.Count > 0
                ? new SeekRequest(-1, (long)(Math.Max(0, resumeAt) * ffmpeg.AV_TIME_BASE), ffmpeg.AVSEEK_FLAG_BACKWARD)
                : _lastSeek;
            if (_lastQueued.Count == 0)
                resumeAt = ToSeconds(target.StreamIndex, target.Timestamp);

            firstDrop = _dropStarted == 0;
            if (firstDrop)
            {
                _dropStarted = now;
                _droppedAt = double.IsFinite(resumeAt) ? resumeAt : 0;
            }
            attempt = _reconnectAttempts++;
        }

        if (firstDrop)
        {
            DiagnosticRouter.Raise(
                MediaDiagnosticLevel.Warning,
                DiagnosticCategory,
                $"Network lost at {FormatTime(resumeAt)}: {_demuxer.FailedReadError ?? "the source stopped answering"}; reconnecting for up to {_reconnectSeconds:F0} s.",
                sourcePath: _sourcePath);
        }

        while (true)
        {
            var delay = Math.Min(MaxReconnectDelaySeconds, FirstReconnectDelaySeconds * Math.Pow(2, attempt));
            var elapsed = Stopwatch.GetElapsedTime(Interlocked.Read(ref _dropStarted)).TotalSeconds;
            if (elapsed + delay > _reconnectSeconds)
                return false;

            if (!SleepUnlessInterrupted(delay, generation))
                return true;

            // The attempt's own I/O (a connect that hangs) ends with the window too (ENG-034).
            var remaining = _reconnectSeconds - Stopwatch.GetElapsedTime(Interlocked.Read(ref _dropStarted)).TotalSeconds;
            if (remaining <= 0)
                return false;

            var result = _demuxer.Seek(target.StreamIndex, target.Timestamp, target.Flags, TimeSpan.FromSeconds(remaining));
            lock (_sync)
            {
                if (generation != _generation || _stopping || _aborted)
                    return true;

                if (result >= 0)
                {
                    _boundNextReadToReconnectWindow = true;
                    // Read on from the keyframe before the resume point; what was queued is skipped.
                    _queuedUpTo.Clear();
                    foreach (var (streamIndex, last) in _lastQueued)
                        _queuedUpTo[streamIndex] = last.Timestamp;
                    _failuresAtSeek = _demuxer.NetworkFailureCount;
                    return true;
                }

                attempt = _reconnectAttempts++;
            }
        }
    }

    /// <summary>Waits <paramref name="seconds"/>; false as soon as a stop or a newer seek needs the reader.</summary>
    bool SleepUnlessInterrupted(double seconds, int generation)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(seconds * Stopwatch.Frequency);
        while (true)
        {
            if (_stopping || _aborted || Volatile.Read(ref _generation) != generation)
                return false;

            var remaining = deadline - Stopwatch.GetTimestamp();
            if (remaining <= 0)
                return true;

            Thread.Sleep(TimeSpan.FromSeconds(Math.Min(remaining / (double)Stopwatch.Frequency, ConsumerWaitSlice.TotalSeconds)));
        }
    }

    void ForgetQueuedNoLock(SeekRequest seek)
    {
        _lastSeek = seek;
        _lastQueued.Clear();
        _queuedUpTo.Clear();
        _failuresAtSeek = _demuxer.NetworkFailureCount;
        _dropStarted = 0;
        _reconnectAttempts = 0;
    }

    static string FormatTime(double seconds) =>
        seconds is >= 0 and < double.MaxValue ? TimeSpan.FromSeconds(Math.Floor(seconds)).ToString(seconds >= 3600 ? @"h\:mm\:ss" : @"m\:ss") : "?";

    /// <summary>
    /// Queues a packet of a registered stream. Returns how long the network was lost when this
    /// packet completes a reconnect (see <see cref="ReconnectedAfterSeconds"/>), otherwise null.
    /// </summary>
    double? EnqueueNoLock(AVPacket* packet)
    {
        var streamIndex = packet->stream_index;
        var timestamp = packet->dts != ffmpeg.AV_NOPTS_VALUE ? packet->dts : packet->pts;
        if (!_buffer.IsRegistered(streamIndex)
            || (timestamp != ffmpeg.AV_NOPTS_VALUE && _queuedUpTo.TryGetValue(streamIndex, out var queuedUpTo) && timestamp <= queuedUpTo))
        {
            ffmpeg.av_packet_unref(packet);
            return null;
        }

        var copy = ffmpeg.av_packet_alloc();
        if (copy == null)
        {
            ffmpeg.av_packet_unref(packet);
            return null;
        }

        var info = Describe(packet);
        _ = _queuedUpTo.Remove(streamIndex);
        if (timestamp != ffmpeg.AV_NOPTS_VALUE && (info.DecodeSeconds ?? info.PresentationSeconds) is { } start)
            _lastQueued[streamIndex] = new LastPacket(timestamp, start, start + info.DurationSeconds);

        double? resumedAfter = null;
        if (_dropStarted != 0 && (info.DecodeSeconds ?? info.PresentationSeconds) >= _droppedAt + ReconnectedAfterSeconds)
        {
            resumedAfter = Stopwatch.GetElapsedTime(_dropStarted).TotalSeconds;
            _dropStarted = 0;
            _reconnectAttempts = 0;
        }

        ffmpeg.av_packet_move_ref(copy, packet);
        var endsBefore = _buffer.StreamEndsQueued;
        _ = _buffer.Add((nint)copy, info);
        if (_buffer.StreamEndsQueued != endsBefore)
            SignalAllArrivals();
        else if (_arrivals.TryGetValue(streamIndex, out var arrival))
            arrival.Set();
        return resumedAfter;
    }

    ReadAheadPacketInfo Describe(AVPacket* packet)
    {
        var stream = _demuxer.GetStream(packet->stream_index);
        var timeBase = stream != null ? ffmpeg.av_q2d(stream->time_base) : 0;
        double? presentation = packet->pts != ffmpeg.AV_NOPTS_VALUE && timeBase > 0 ? packet->pts * timeBase : null;
        double? decode = packet->dts != ffmpeg.AV_NOPTS_VALUE && timeBase > 0 ? packet->dts * timeBase : null;
        var duration = packet->duration > 0 && timeBase > 0 ? packet->duration * timeBase : 0;
        return new ReadAheadPacketInfo(
            packet->stream_index,
            presentation ?? decode,
            decode ?? presentation,
            duration,
            packet->size + PacketOverheadBytes,
            (packet->flags & ffmpeg.AV_PKT_FLAG_KEY) != 0);
    }

    double ToSeconds(int streamIndex, long timestamp)
    {
        if (streamIndex < 0)
            return timestamp / (double)ffmpeg.AV_TIME_BASE;

        var stream = _demuxer.GetStream(streamIndex);
        return stream != null ? timestamp * ffmpeg.av_q2d(stream->time_base) : double.NaN;
    }

    static void ReleasePacket(nint handle)
    {
        var packet = (AVPacket*)handle;
        ffmpeg.av_packet_free(&packet);
    }

    readonly record struct SeekRequest(int StreamIndex, long Timestamp, int Flags);

    readonly record struct LastPacket(long Timestamp, double StartSeconds, double EndSeconds);

    /// <summary>
    /// Limits how long consumer reads on the current thread wait for the network (see
    /// <see cref="NoWait"/>, <see cref="WaitAtMost"/>, <see cref="CancelWaitsWith"/>,
    /// <see cref="AbandonWaitsWhen"/>); disposing it
    /// restores the enclosing limits. Nested scopes keep the tighter deadline. A default scope
    /// changes nothing.
    /// </summary>
    internal readonly struct WaitScope : IDisposable
    {
        readonly bool _active;
        readonly long _previousDeadline;
        readonly CancellationToken _previousCancellation;
        readonly Func<bool>? _previousAbandon;

        public WaitScope(long deadline, CancellationToken cancellation, Func<bool>? abandon = null)
        {
            _active = true;
            _previousDeadline = _waitDeadline;
            _previousCancellation = _waitCancellation;
            _previousAbandon = _waitAbandon;
            if (abandon is not null)
                _waitAbandon = abandon;
            if (deadline != 0 && (_waitDeadline == 0 || deadline < _waitDeadline))
                _waitDeadline = deadline;
            if (cancellation.CanBeCanceled)
                _waitCancellation = cancellation;
        }

        public void Dispose()
        {
            if (!_active)
                return;

            _waitDeadline = _previousDeadline;
            _waitCancellation = _previousCancellation;
            _waitAbandon = _previousAbandon;
        }
    }
}
