using FFmpeg.AutoGen;
using System.Diagnostics;

namespace FatzBomb.MediaControl.Core.Media;

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
/// </remarks>
internal sealed unsafe class NetworkReadAhead : IDisposable
{
    private const string DiagnosticCategory = "Network";
    private const int HardByteCapMultiplier = 2;
    // AVPacket and its buffer reference, on top of the payload.
    private const long PacketOverheadBytes = 192;
    private static readonly TimeSpan ConsumerWaitSlice = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan ReaderIdleSlice = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan ReaderJoinWarning = TimeSpan.FromSeconds(2);

    // The calling thread's wait limits (WaitScope): a Stopwatch deadline (0 = none), a token and a condition.
    [ThreadStatic] private static long t_waitDeadline;
    [ThreadStatic] private static CancellationToken t_waitCancellation;
    [ThreadStatic] private static Func<bool>? t_waitAbandon;
    // How long reads on this thread have waited for packets in total (Stopwatch ticks).
    [ThreadStatic] private static long t_waitedTicks;

    private readonly MediaDemuxer _demuxer;
    private readonly string? _sourcePath;
    private readonly bool _reportSeekFailures;
    private readonly object? _diagnosticOwner;
    private readonly double _maxSeconds;
    private readonly long _maxBytes;
    private readonly double _resumeSeconds;
    private readonly Lock _sync = new();
    private readonly ReadAheadBuffer<nint> _buffer = new(ReleasePacket);
    private readonly ManualResetEventSlim _readerWake = new(false);
    private readonly ManualResetEventSlim _dataArrived = new(false);
    private Thread? _thread;
    private bool _stopping;
    private bool _disposed;
    private volatile bool _aborted;
    private bool _atEnd;
    private bool _readerPaused;
    private bool _hardCapReported;
    // The reader thread ran and returned (an abort or a failure): no one serves a seek any more.
    private bool _readerExited;
    // Bumped by every seek the reader must make; the reader's I/O for an older one is given up.
    private int _generation;
    private int _readerGeneration;
    private SeekRequest? _pendingSeek;
    private long _supersededBefore;

    /// <param name="demuxer">The open network source; the reader takes over its I/O.</param>
    /// <param name="policy">The caps and the resume threshold.</param>
    /// <param name="sourcePath">The URL, for diagnostics.</param>
    /// <param name="reportSeekFailures">
    /// Whether a failed reader seek is reported: not for a live source, where Stop's seek to the
    /// start fails by design and playback goes on at the live edge.
    /// </param>
    public NetworkReadAhead(MediaDemuxer demuxer, NetworkReadAheadPolicy policy, string? sourcePath, bool reportSeekFailures = true)
    {
        ArgumentNullException.ThrowIfNull(demuxer);
        ArgumentNullException.ThrowIfNull(policy);
        _demuxer = demuxer;
        _sourcePath = sourcePath;
        _reportSeekFailures = reportSeekFailures;
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
    public static long WaitedTicksOnThisThread => t_waitedTicks;

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

    /// <summary>Keeps the packets of <paramref name="streamIndex"/>; the lead stream's time is what the duration cap measures.</summary>
    public void Register(int streamIndex, bool lead)
    {
        lock (_sync)
            _buffer.Register(streamIndex, lead);
    }

    /// <summary>Stops keeping <paramref name="streamIndex"/> (its consumer closed) and drops its packets.</summary>
    public void Unregister(int streamIndex)
    {
        lock (_sync)
            _buffer.Unregister(streamIndex);
        _readerWake.Set();
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

                _dataArrived.Reset();
            }

            WaitForData();
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

        _dataArrived.Set();
    }

    /// <summary>Aborts the reader's network I/O and every wait, from any thread, without a lock.</summary>
    public void Abort()
    {
        _aborted = true;
        _demuxer.Abort();
        _dataArrived.Set();
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

    private bool IsReaderOperationStale() => Volatile.Read(ref _readerGeneration) != Volatile.Read(ref _generation);

    private void RequestReaderSeekNoLock(SeekRequest request)
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

    private bool ShouldGiveUpWait(long waitStarted, long now) =>
        waitStarted < Interlocked.Read(ref _supersededBefore)
        || (t_waitDeadline != 0 && now >= t_waitDeadline)
        || t_waitCancellation.IsCancellationRequested
        || t_waitAbandon?.Invoke() == true;

    private void WaitForData()
    {
        var timeout = ConsumerWaitSlice;
        if (t_waitDeadline != 0)
        {
            TimeSpan remaining = Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), t_waitDeadline);
            if (remaining < timeout)
                timeout = remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }

        var started = Stopwatch.GetTimestamp();
        try
        {
            _ = _dataArrived.Wait(timeout, t_waitCancellation);
        }
        catch (OperationCanceledException)
        {
            // Checked again by the caller.
        }

        t_waitedTicks += Stopwatch.GetTimestamp() - started;
    }

    private void EnsureReaderStartedNoLock()
    {
        if (_thread is not null || _stopping)
            return;

        _thread = new Thread(Run) { IsBackground = true, Name = "FatzBombMedia.NetworkReadAhead" };
        _thread.Start();
    }

    private bool ShouldPauseNoLock(out bool reportHardCap)
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

    private void Run()
    {
        using DiagnosticRouter.OwnerScope owner = DiagnosticRouter.EnterOwner(_diagnosticOwner);
        AVPacket* packet = ffmpeg.av_packet_alloc();
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

            _dataArrived.Set();
        }
    }

    private void ReadLoop(AVPacket* packet)
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

                generation = _generation;
                seek = _pendingSeek;
                _pendingSeek = null;
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

            var read = _demuxer.ReadPacket(packet);
            lock (_sync)
            {
                if (generation != _generation || _stopping)
                {
                    ffmpeg.av_packet_unref(packet);
                    continue;
                }

                if (read >= 0)
                {
                    EnqueueNoLock(packet);
                }
                else if (read != ffmpeg.AVERROR_EXIT)
                {
                    // End of file, a dropped stream (reported once by the demuxer) or an abort.
                    _atEnd = true;
                    _buffer.AddEndOfStream();
                    _dataArrived.Set();
                }
            }
        }
    }

    private void EnqueueNoLock(AVPacket* packet)
    {
        var streamIndex = packet->stream_index;
        AVPacket* copy = _buffer.IsRegistered(streamIndex) ? ffmpeg.av_packet_alloc() : null;
        if (copy == null)
        {
            ffmpeg.av_packet_unref(packet);
            return;
        }

        ReadAheadPacketInfo info = Describe(packet);
        ffmpeg.av_packet_move_ref(copy, packet);
        _ = _buffer.Add((nint)copy, info);
        _dataArrived.Set();
    }

    private ReadAheadPacketInfo Describe(AVPacket* packet)
    {
        AVStream* stream = _demuxer.GetStream(packet->stream_index);
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

    private double ToSeconds(int streamIndex, long timestamp)
    {
        if (streamIndex < 0)
            return timestamp / (double)ffmpeg.AV_TIME_BASE;

        AVStream* stream = _demuxer.GetStream(streamIndex);
        return stream != null ? timestamp * ffmpeg.av_q2d(stream->time_base) : double.NaN;
    }

    private static void ReleasePacket(nint handle)
    {
        var packet = (AVPacket*)handle;
        ffmpeg.av_packet_free(&packet);
    }

    private readonly record struct SeekRequest(int StreamIndex, long Timestamp, int Flags);

    /// <summary>
    /// Limits how long consumer reads on the current thread wait for the network (see
    /// <see cref="NoWait"/>, <see cref="WaitAtMost"/>, <see cref="CancelWaitsWith"/>,
    /// <see cref="AbandonWaitsWhen"/>); disposing it
    /// restores the enclosing limits. Nested scopes keep the tighter deadline. A default scope
    /// changes nothing.
    /// </summary>
    internal readonly struct WaitScope : IDisposable
    {
        private readonly bool _active;
        private readonly long _previousDeadline;
        private readonly CancellationToken _previousCancellation;
        private readonly Func<bool>? _previousAbandon;

        public WaitScope(long deadline, CancellationToken cancellation, Func<bool>? abandon = null)
        {
            _active = true;
            _previousDeadline = t_waitDeadline;
            _previousCancellation = t_waitCancellation;
            _previousAbandon = t_waitAbandon;
            if (abandon is not null)
                t_waitAbandon = abandon;
            if (deadline != 0 && (t_waitDeadline == 0 || deadline < t_waitDeadline))
                t_waitDeadline = deadline;
            if (cancellation.CanBeCanceled)
                t_waitCancellation = cancellation;
        }

        public void Dispose()
        {
            if (!_active)
                return;

            t_waitDeadline = _previousDeadline;
            t_waitCancellation = _previousCancellation;
            t_waitAbandon = _previousAbandon;
        }
    }
}
