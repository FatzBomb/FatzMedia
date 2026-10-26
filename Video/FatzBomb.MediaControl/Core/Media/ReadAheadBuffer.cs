using System.Diagnostics;

namespace FatzBomb.MediaControl.Core.Media;

/// <summary>What the read-ahead buffer knows about one demuxed packet (Stage 039A slice 2).</summary>
/// <param name="StreamIndex">The packet's stream.</param>
/// <param name="PresentationSeconds">Its presentation time in seconds (container timeline), when known.</param>
/// <param name="DecodeSeconds">Its decode time in seconds, when known (monotonic in read order).</param>
/// <param name="DurationSeconds">Its duration in seconds, or 0 when unknown.</param>
/// <param name="Bytes">The memory it holds.</param>
/// <param name="IsKeyframe">Whether decoding can start at it.</param>
internal readonly record struct ReadAheadPacketInfo(
    int StreamIndex,
    double? PresentationSeconds,
    double? DecodeSeconds,
    double DurationSeconds,
    long Bytes,
    bool IsKeyframe);

/// <summary>The outcome of <see cref="ReadAheadBuffer{T}.TryTake"/>.</summary>
internal enum ReadAheadTake
{
    /// <summary>A packet was taken.</summary>
    Packet,

    /// <summary>The stream reached its end (the reader's end of file); repeated until cleared.</summary>
    EndOfStream,

    /// <summary>Nothing is buffered for the stream yet.</summary>
    Empty,
}

/// <summary>
/// The packet queues of a network read-ahead (Stage 039A slice 2), without threads or FFmpeg: one
/// queue per registered stream, in read order, with an end-of-stream marker the reader appends to
/// every queue. It accounts the buffered media time (of the lead stream, the picture when there is
/// one) and bytes, decides whether a seek can be served from the queued packets, and records how
/// long a consumer has found its queue empty. Not thread-safe: the owner locks.
/// </summary>
/// <remarks>
/// A seek is served from the buffer only when it is forward and fully buffered: the seek stream
/// has a keyframe at or before the target and packets up to the target, and every other stream's
/// next packet is not after the target (an audio consumer that already took the target's packets
/// could not play them again). The packets before that keyframe are dropped; the other streams
/// drop the packets that end at or before it, as a container seek to the keyframe would.
/// <para>
/// A stream registered with its own end (a track shorter than the source, ENG-034) gets its
/// end-of-stream marker once the reader has read <see cref="StreamEndToleranceSeconds"/> past that
/// end, instead of counting as starved until the reader reaches the end of the source.
/// </para>
/// </remarks>
/// <typeparam name="T">The packet handle (an <c>AVPacket*</c> in the engine).</typeparam>
internal sealed class ReadAheadBuffer<T>(Action<T> release)
{
    /// <summary>How far another stream's next packet may start after a seek target and still play it.</summary>
    internal const double OtherStreamToleranceSeconds = 0.05;

    /// <summary>
    /// How far past a stream's own end the reader must be before that stream is ended: more than
    /// the interleaving of its last packets with the other streams'.
    /// </summary>
    internal const double StreamEndToleranceSeconds = 2.0;

    private readonly Action<T> _release = release ?? throw new ArgumentNullException(nameof(release));
    private readonly List<StreamQueue> _streams = [];
    private int _leadStream = -1;

    /// <summary>Gets the packet memory held, in bytes.</summary>
    public long Bytes { get; private set; }

    /// <summary>Gets the stream whose time the duration cap measures, or -1.</summary>
    public int LeadStream => _leadStream >= 0 ? _leadStream : _streams.Count > 0 ? _streams[0].Index : -1;

    /// <summary>Gets the media time buffered on the lead stream, in seconds.</summary>
    public double BufferedSeconds => Find(LeadStream)?.BufferedSeconds ?? 0;

    /// <summary>
    /// Gets whether a consumer found its queue empty and no packet arrived for it since (the reader
    /// must not stop at a cap then).
    /// </summary>
    public bool HasStarvedStream => _streams.Exists(stream => stream.StarvedSince != 0);

    /// <summary>Gets how many streams were ended at their own end so far (the owner wakes their consumers).</summary>
    public int StreamEndsQueued { get; private set; }

    /// <summary>
    /// Keeps the packets of <paramref name="streamIndex"/>; the lead stream is the one the duration
    /// cap measures. <paramref name="endSeconds"/> is where the stream's own track ends, when known.
    /// </summary>
    public void Register(int streamIndex, bool lead, double? endSeconds = null)
    {
        if (streamIndex < 0)
            return;

        StreamQueue? stream = Find(streamIndex);
        if (stream is null)
        {
            stream = new StreamQueue(streamIndex);
            _streams.Add(stream);
        }

        stream.EndSeconds = endSeconds is { } end && double.IsFinite(end) ? end : null;
        if (lead)
            _leadStream = streamIndex;
    }

    /// <summary>Stops keeping <paramref name="streamIndex"/> and drops its packets.</summary>
    public void Unregister(int streamIndex)
    {
        if (Find(streamIndex) is not { } stream)
            return;

        Clear(stream);
        _ = _streams.Remove(stream);
        if (_leadStream == streamIndex)
            _leadStream = -1;
    }

    public bool IsRegistered(int streamIndex) => Find(streamIndex) is not null;

    /// <summary>Queues <paramref name="item"/>; returns false (and releases it) for a stream nobody registered.</summary>
    public bool Add(T item, ReadAheadPacketInfo info)
    {
        if (Find(info.StreamIndex) is not { } stream)
        {
            _release(item);
            return false;
        }

        stream.Entries.Enqueue(new Entry(item, info, IsEnd: false));
        // Its consumer finds a packet next time: no longer starved, so a paused consumer that
        // starved earlier does not keep the reader going past the caps.
        stream.StarvedSince = 0;
        // A packet of the stream itself (the next lap of a loop) re-arms its own end.
        stream.EndQueued = false;
        stream.Bytes += info.Bytes;
        Bytes += info.Bytes;
        stream.NoteAdded(info);
        EndStreamsReadPast(stream, info.DecodeSeconds ?? info.PresentationSeconds);
        return true;
    }

    /// <summary>Appends the end-of-stream marker to every queue (the reader reached the end of the source).</summary>
    public void AddEndOfStream()
    {
        foreach (StreamQueue stream in _streams)
        {
            // A stream already ended at its own end has its marker.
            if (!stream.EndQueued)
                stream.Entries.Enqueue(new Entry(default!, default, IsEnd: true));
            stream.EndQueued = true;
            stream.StarvedSince = 0;
        }
    }

    /// <summary>
    /// Ends the other streams whose own end the reader is now well past (see
    /// <see cref="StreamEndToleranceSeconds"/>). A stream is never ended by its own packets: they
    /// show its end was underestimated (a duration FFmpeg estimated from the bitrate).
    /// </summary>
    private void EndStreamsReadPast(StreamQueue readStream, double? readSeconds)
    {
        if (readSeconds is not { } read)
            return;

        foreach (StreamQueue stream in _streams)
        {
            if (ReferenceEquals(stream, readStream) || stream.EndQueued || stream.Ended || stream.EndSeconds is not { } end || read < end + StreamEndToleranceSeconds)
                continue;

            stream.Entries.Enqueue(new Entry(default!, default, IsEnd: true));
            stream.EndQueued = true;
            stream.StarvedSince = 0;
            StreamEndsQueued++;
        }
    }

    /// <summary>
    /// Takes the next packet of <paramref name="streamIndex"/>. <paramref name="now"/> is a
    /// <see cref="Stopwatch"/> timestamp, recorded when the queue is found empty.
    /// </summary>
    public ReadAheadTake TryTake(int streamIndex, long now, out T item)
    {
        item = default!;
        if (Find(streamIndex) is not { } stream)
            return ReadAheadTake.Empty;

        if (stream.Ended)
            return ReadAheadTake.EndOfStream;

        if (!stream.Entries.TryDequeue(out Entry entry))
        {
            if (stream.StarvedSince == 0)
                stream.StarvedSince = now == 0 ? 1 : now;
            return ReadAheadTake.Empty;
        }

        stream.StarvedSince = 0;
        stream.AwaitingNextLap = false;
        if (entry.IsEnd)
        {
            stream.Ended = true;
            return ReadAheadTake.EndOfStream;
        }

        stream.Bytes -= entry.Info.Bytes;
        Bytes -= entry.Info.Bytes;
        stream.NoteTaken(entry.Info);
        item = entry.Item;
        return ReadAheadTake.Packet;
    }

    /// <summary>Whether the consumer of <paramref name="streamIndex"/> took its end-of-stream marker.</summary>
    public bool IsEnded(int streamIndex) => Find(streamIndex)?.Ended == true;

    /// <summary>Ends a stream whose queue is empty while the reader is at the end of the source.</summary>
    public void MarkEnded(int streamIndex)
    {
        if (Find(streamIndex) is { } stream)
        {
            stream.Ended = true;
            stream.StarvedSince = 0;
        }
    }

    /// <summary>
    /// Re-arms a stream after its end (a gapless loop wrap: the next lap's packets follow). A stream
    /// that had ended waits for the next lap until a packet or marker is taken
    /// (<see cref="IsAwaitingNextLap"/>), so its consumer does not find the same end again.
    /// </summary>
    public void ClearEnd(int streamIndex)
    {
        if (Find(streamIndex) is { Ended: true } stream)
        {
            stream.Ended = false;
            stream.AwaitingNextLap = true;
        }
    }

    /// <summary>Whether <paramref name="streamIndex"/> was re-armed after its end and has taken nothing since.</summary>
    public bool IsAwaitingNextLap(int streamIndex) => Find(streamIndex)?.AwaitingNextLap == true;

    /// <summary>Gets how long the consumer that has waited longest has found its queue empty.</summary>
    public TimeSpan LongestStarvation(long now)
    {
        long earliest = 0;
        foreach (StreamQueue stream in _streams)
        {
            if (stream.StarvedSince != 0 && (earliest == 0 || stream.StarvedSince < earliest))
                earliest = stream.StarvedSince;
        }

        return earliest == 0 || now <= earliest ? TimeSpan.Zero : Stopwatch.GetElapsedTime(earliest, now);
    }

    /// <summary>Drops every queued packet and marker and re-arms every stream (a seek outside the buffer).</summary>
    public void Flush()
    {
        foreach (StreamQueue stream in _streams)
            Clear(stream);
    }

    /// <summary>Drops the packets of one stream and re-arms it (the picture's side of a loop wrap).</summary>
    public void FlushStream(int streamIndex)
    {
        if (Find(streamIndex) is { } stream)
            Clear(stream);
    }

    /// <summary>Whether a cap is reached: <paramref name="maxSeconds"/> of lead-stream time or <paramref name="maxBytes"/>.</summary>
    public bool IsFull(double maxSeconds, long maxBytes) => BufferedSeconds >= maxSeconds || Bytes >= maxBytes;

    /// <summary>
    /// Progress towards ending a buffering spell, <c>0..1</c>: 1 once <paramref name="resumeSeconds"/>
    /// are buffered, a quarter of <paramref name="maxBytes"/> is held, or a cap is reached.
    /// </summary>
    public double ResumeFill(double resumeSeconds, double maxSeconds, long maxBytes)
    {
        if (IsFull(maxSeconds, maxBytes))
            return 1;

        var byTime = resumeSeconds > 0 ? BufferedSeconds / resumeSeconds : 0;
        var byBytes = maxBytes > 0 ? Bytes / (maxBytes / 4.0) : 0;
        return Math.Clamp(Math.Max(byTime, byBytes), 0, 1);
    }

    /// <summary>
    /// Serves a seek of <paramref name="streamIndex"/> to <paramref name="targetSeconds"/> from the
    /// buffer if it can (see the remarks on the class), dropping the packets before the keyframe.
    /// Returns false, changing nothing, when the seek needs the network.
    /// </summary>
    public bool TrySeekWithin(int streamIndex, double targetSeconds)
    {
        if (!double.IsFinite(targetSeconds) || Find(streamIndex) is not { Ended: false } seekStream)
            return false;

        var keyPosition = -1;
        var keySeconds = 0.0;
        var bufferedTo = double.NegativeInfinity;
        var position = 0;
        foreach (Entry entry in seekStream.Entries)
        {
            if (!entry.IsEnd && entry.Info.PresentationSeconds is { } start)
            {
                if (entry.Info.IsKeyframe && start <= targetSeconds)
                {
                    keyPosition = position;
                    keySeconds = start;
                }

                bufferedTo = Math.Max(bufferedTo, start + Math.Max(0, entry.Info.DurationSeconds));
            }

            position++;
        }

        if (keyPosition < 0 || bufferedTo < targetSeconds)
            return false;

        foreach (StreamQueue other in _streams)
        {
            if (ReferenceEquals(other, seekStream))
                continue;

            if (other.Ended || other.NextPresentationSeconds is not { } next || next > targetSeconds + OtherStreamToleranceSeconds)
                return false;
        }

        for (var index = 0; index < keyPosition; index++)
            Drop(seekStream, seekStream.Entries.Dequeue());

        foreach (StreamQueue other in _streams)
        {
            if (ReferenceEquals(other, seekStream))
                continue;

            while (other.Entries.TryPeek(out Entry head)
                && !head.IsEnd
                && head.Info.PresentationSeconds is { } start
                && start + Math.Max(0, head.Info.DurationSeconds) <= keySeconds)
            {
                Drop(other, other.Entries.Dequeue());
            }
        }

        seekStream.RecomputeTail();
        foreach (StreamQueue other in _streams)
            other.RecomputeTail();
        return true;
    }

    private void Drop(StreamQueue stream, Entry entry)
    {
        if (entry.IsEnd)
            return;

        stream.Bytes -= entry.Info.Bytes;
        Bytes -= entry.Info.Bytes;
        stream.NoteTaken(entry.Info);
        _release(entry.Item);
    }

    private void Clear(StreamQueue stream)
    {
        while (stream.Entries.TryDequeue(out Entry entry))
        {
            if (!entry.IsEnd)
                _release(entry.Item);
        }

        Bytes -= stream.Bytes;
        stream.Reset();
    }

    private StreamQueue? Find(int streamIndex)
    {
        foreach (StreamQueue stream in _streams)
        {
            if (stream.Index == streamIndex)
                return stream;
        }

        return null;
    }

    private readonly record struct Entry(T Item, ReadAheadPacketInfo Info, bool IsEnd);

    private sealed class StreamQueue(int index)
    {
        public int Index { get; } = index;

        public Queue<Entry> Entries { get; } = new();

        public long Bytes { get; set; }

        /// <summary>The consumer took the end-of-stream marker; reads return the end until cleared.</summary>
        public bool Ended { get; set; }

        /// <summary>Where the stream's own track ends (container timeline), when known.</summary>
        public double? EndSeconds { get; set; }

        /// <summary>An end-of-stream marker was queued and no packet of the stream followed it.</summary>
        public bool EndQueued { get; set; }

        /// <summary>Re-armed after its end (a loop wrap) and nothing taken since.</summary>
        public bool AwaitingNextLap { get; set; }

        /// <summary>When the consumer found the queue empty (a Stopwatch timestamp), or 0.</summary>
        public long StarvedSince { get; set; }

        /// <summary>The end of the last packet taken (the consumer's position), when known.</summary>
        public double? LastTakenEnd { get; private set; }

        private double? _tailDecodeEnd;

        /// <summary>The presentation time of the next packet the consumer gets, or where it stands when none is queued.</summary>
        public double? NextPresentationSeconds
        {
            get
            {
                foreach (Entry entry in Entries)
                {
                    if (entry.IsEnd)
                        break;
                    if (entry.Info.PresentationSeconds is { } start)
                        return start;
                }

                return LastTakenEnd;
            }
        }

        public double BufferedSeconds
        {
            get
            {
                if (_tailDecodeEnd is not { } tail || !Entries.TryPeek(out Entry head) || head.IsEnd || DecodeStart(head.Info) is not { } start)
                    return 0;

                return Math.Max(0, tail - start);
            }
        }

        public void NoteAdded(ReadAheadPacketInfo info)
        {
            if (DecodeStart(info) is { } start)
            {
                var end = start + Math.Max(0, info.DurationSeconds);
                _tailDecodeEnd = _tailDecodeEnd is { } tail ? Math.Max(tail, end) : end;
            }
        }

        public void NoteTaken(ReadAheadPacketInfo info)
        {
            if (info.PresentationSeconds is { } start)
                LastTakenEnd = start + Math.Max(0, info.DurationSeconds);
            if (Entries.Count == 0)
                _tailDecodeEnd = null;
        }

        public void RecomputeTail()
        {
            _tailDecodeEnd = null;
            foreach (Entry entry in Entries)
            {
                if (!entry.IsEnd)
                    NoteAdded(entry.Info);
            }
        }

        public void Reset()
        {
            Bytes = 0;
            Ended = false;
            EndQueued = false;
            AwaitingNextLap = false;
            StarvedSince = 0;
            LastTakenEnd = null;
            _tailDecodeEnd = null;
        }

        private static double? DecodeStart(ReadAheadPacketInfo info) => info.DecodeSeconds ?? info.PresentationSeconds;
    }
}
