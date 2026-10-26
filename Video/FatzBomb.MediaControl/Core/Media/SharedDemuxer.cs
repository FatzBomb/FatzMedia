using FatzBomb.MediaControl.Core.Network;
using FFmpeg.AutoGen;

namespace FatzBomb.MediaControl.Core.Media;

/// <summary>
/// Wraps a <see cref="MediaDemuxer"/> with per-stream packet fan-out: a single
/// <c>av_read_frame</c> cursor dispatches packets into independent per-stream
/// queues, so registered consumers can read from one container without each
/// opening it separately.
/// </summary>
/// <remarks>
/// Coordinates video and audio packet consumers with per-stream EOF tracking.
/// <para>
/// - <b>Single <see cref="Seek"/></b> that flushes all registered queues and clears
///   their EOF states. No <c>SeekVideo</c>/<c>SeekAudio</c> split (the reverted S3's split
///   caused the second seek to override the first).
/// </para>
/// <para>
/// - <b>Per-consumer EOF</b>: <see cref="ReadVideoPacket"/> only sets
///   <c>_videoEof</c>; <see cref="ReadAudioPacket"/> only sets <c>_audioEof</c>.
///   No cross-stream propagation (the reverted S3's propagation caused
///   premature audio EOF).
/// </para>
/// <para>
/// - <b><see cref="PrepareLoopWrap"/></b>: drains remaining registered-stream
///   packets, then seeks to 0. Called by <c>VideoPipeline.TryPreSeekLoopStart</c>
///   instead of <c>decoder.Seek(0)</c> so position-0 video frames are ready
///   immediately. Main-pipeline audio now owns an independent demux cursor.
/// </para>
/// <para>
/// - <b><see cref="ClearAudioEof"/></b>: called by <c>VideoAudioEngine</c> in shared-demuxer
///   mode to re-arm audio after a loop wrap.
/// </para>
/// <para>
/// - <b>Network read-ahead (Stage 039A slice 2)</b>: a source opened with
///   <see cref="Open(string, FfmpegNetworkOptions)"/> is read by a <see cref="NetworkReadAhead"/>
///   reader thread instead of the consumers. Reads take packets from its queues (waiting for them
///   unless the thread limited its waits), <see cref="Seek"/> is served from the queues when the
///   target is buffered and otherwise hands the seek to the reader, and the consumer lock below is
///   not used. Local files keep the inline cursor described above.
/// </para>
/// </remarks>
internal sealed unsafe class SharedDemuxer : IDisposable
{
    private readonly MediaDemuxer _demuxer = new();
    private readonly Lock _sync = new();
    private readonly Queue<IntPtr> _videoPackets = new();
    private readonly Queue<IntPtr> _audioPackets = new();
    private bool _videoEof;
    private bool _audioEof;
    private int _videoStreamIndex = -1;
    private int _audioStreamIndex = -1;
    // The network read-ahead (slice 2), or null for a local file. Read once per call: Close clears it.
    private volatile NetworkReadAhead? _readAhead;
    private NetworkVideoProbe _networkProbe;

    public bool IsOpen => _demuxer.IsOpen;
    public uint StreamCount => _demuxer.StreamCount;
    public long FormatDurationTicks => _demuxer.FormatDurationTicks;

    public bool Open(string path) => _demuxer.Open(path);

    /// <summary>
    /// Opens an http/https source (Stage 039A; see <see cref="MediaDemuxer.Open(string, FfmpegNetworkOptions)"/>)
    /// with a read-ahead bounded by <see cref="FfmpegNetworkOptions.ReadAhead"/> (slice 2). The reader
    /// starts with the first read or seek, once the consumers registered their streams.
    /// </summary>
    public bool Open(string url, FfmpegNetworkOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        DisposeReadAhead();
        if (!_demuxer.Open(url, options))
            return false;

        // Probed once, before the reader owns the format context (avio_size can do I/O).
        _networkProbe = _demuxer.GetNetworkProbe();
        _readAhead = new NetworkReadAhead(
            _demuxer,
            options.ReadAhead ?? NetworkReadAheadPolicy.Default,
            url,
            seekable: NetworkVideoPolicy.Resolve(_networkProbe).IsSeekable);
        return true;
    }

    /// <summary>The network read-ahead of the open source, or null (a local file, or a URL opened without it).</summary>
    internal NetworkReadAhead? ReadAhead => _readAhead;

    /// <summary>Progress of the read-ahead towards ending a buffering spell, <c>0..1</c>; 1 without one.</summary>
    public double ReadAheadResumeFill => _readAhead?.ResumeFill ?? 1;

    /// <summary>True while a network source is open.</summary>
    public bool IsNetwork => _demuxer.IsNetwork;

    /// <summary>The FFmpeg error of the last failed network open, or null.</summary>
    public string? LastNetworkError => _demuxer.LastNetworkError;

    /// <summary>
    /// How long the network read in progress has been blocked; with the read-ahead, how long a
    /// consumer has found its queue empty. Never waits for network I/O.
    /// </summary>
    public TimeSpan NetworkReadStall => _readAhead is { } readAhead ? readAhead.ConsumerStall : _demuxer.NetworkReadStall;

    /// <summary>Whether the network reader reached the end of the stream. Never waits for network I/O.</summary>
    public bool NetworkEndOfStream => _readAhead is { } readAhead ? readAhead.IsAtEnd : _demuxer.NetworkEndOfStream;

    /// <summary>
    /// Aborts blocking network I/O without taking the queue lock, so a Close does not wait for a
    /// stalled read to time out (Stage 039A).
    /// </summary>
    public void Abort()
    {
        _readAhead?.Abort();
        _demuxer.Abort();
    }

    /// <summary>
    /// Gives up the network I/O (or, with the read-ahead, the consumer waits) that started before
    /// <paramref name="timestamp"/> (ENG-035). With the read-ahead the reader goes on: a newer seek
    /// that needs the network repositions it itself.
    /// </summary>
    public void SupersedeOperationsStartedBefore(long timestamp)
    {
        if (_readAhead is { } readAhead)
            readAhead.SupersedeWaitsStartedBefore(timestamp);
        else
            _demuxer.SupersedeOperationsStartedBefore(timestamp);
    }

    /// <summary>What FFmpeg reported for the open network source.</summary>
    public NetworkVideoProbe GetNetworkProbe()
    {
        if (_readAhead is not null)
            return _networkProbe;

        lock (_sync)
            return _demuxer.GetNetworkProbe();
    }

    public int FindBestStream(AVMediaType mediaType) => _demuxer.FindBestStream(mediaType);

    public AVStream* GetStream(int index) => _demuxer.GetStream(index);

    public void SetStreamDiscard(int index, AVDiscard discard) => _demuxer.SetStreamDiscard(index, discard);

    /// <inheritdoc cref="MediaDemuxer.SelectVideoStream"/>
    public int SelectVideoStream() => _demuxer.SelectVideoStream();

    /// <inheritdoc cref="MediaDemuxer.SelectAudioStream"/>
    public int SelectAudioStream() => _demuxer.SelectAudioStream();

    /// <summary>
    /// Network sources: discards every stream no consumer registered, and re-enables the
    /// registered ones (a consumer that opened first may have discarded them). FFmpeg's HLS and
    /// DASH demuxers otherwise download the segments of every variant (039A smoke). Local files
    /// are left as they are: their unregistered packets are read and dropped, as before.
    /// </summary>
    public void DiscardUnregisteredNetworkStreams()
    {
        if (!_demuxer.IsNetwork)
            return;

        for (var index = 0; index < (int)_demuxer.StreamCount; index++)
        {
            var registered = index == _videoStreamIndex || index == _audioStreamIndex;
            _demuxer.SetStreamDiscard(index, registered ? AVDiscard.AVDISCARD_DEFAULT : AVDiscard.AVDISCARD_ALL);
        }
    }

    public void RegisterVideoStream(int index)
    {
        _videoStreamIndex = index;
        _readAhead?.Register(index, lead: true);
    }

    public void RegisterAudioStream(int index)
    {
        _audioStreamIndex = index;
        _readAhead?.Register(index, lead: false);
    }

    /// <summary>
    /// Forgets the audio consumer (its engine closed or failed to open), so the read-ahead stops
    /// keeping its packets. Its stream is not discarded again: the reader may be reading.
    /// </summary>
    public void UnregisterAudioStream()
    {
        if (_audioStreamIndex < 0)
            return;

        _readAhead?.Unregister(_audioStreamIndex);
        _audioStreamIndex = -1;
    }

    /// <summary>
    /// Reads the next video packet into <paramref name="packet"/>. Non-video
    /// packets are buffered for the audio consumer when registered.
    /// Returns 0 on success, <see cref="ffmpeg.AVERROR_EOF"/> at end of stream,
    /// or a negative error code. EOF is per-consumer: only <c>_videoEof</c> is
    /// set — audio's EOF is not affected.
    /// </summary>
    public int ReadVideoPacket(AVPacket* packet)
    {
        if (_readAhead is { } readAhead)
            return readAhead.Read(_videoStreamIndex, packet);

        lock (_sync)
        {
            if (_videoPackets.Count > 0)
            {
                var buffered = (AVPacket*)_videoPackets.Dequeue();
                ffmpeg.av_packet_move_ref(packet, buffered);
                ffmpeg.av_packet_free(&buffered);
                return 0;
            }

            if (_videoEof)
                return ffmpeg.AVERROR_EOF;

            AVPacket* temp = ffmpeg.av_packet_alloc();
            if (temp == null)
                return -1;

            try
            {
                while (true)
                {
                    var result = _demuxer.ReadPacket(temp);
                    if (result < 0)
                    {
                        if (result == ffmpeg.AVERROR_EOF)
                            _videoEof = true;
                        return result;
                    }

                    if (temp->stream_index == _videoStreamIndex)
                    {
                        ffmpeg.av_packet_move_ref(packet, temp);
                        return 0;
                    }

                    if (_audioStreamIndex >= 0 && temp->stream_index == _audioStreamIndex)
                        BufferPacket(_audioPackets, temp);
                }
            }
            finally
            {
                ffmpeg.av_packet_free(&temp);
            }
        }
    }

    /// <summary>
    /// Reads the next audio packet into <paramref name="packet"/>. Non-audio
    /// packets are buffered for the video consumer when registered.
    /// Returns 0 on success, <see cref="ffmpeg.AVERROR_EOF"/> at end of stream,
    /// or a negative error code. EOF is per-consumer: only <c>_audioEof</c> is
    /// set — video's EOF is not affected.
    /// </summary>
    public int ReadAudioPacket(AVPacket* packet)
    {
        if (_readAhead is { } readAhead)
            return readAhead.Read(_audioStreamIndex, packet);

        lock (_sync)
        {
            if (_audioPackets.Count > 0)
            {
                var buffered = (AVPacket*)_audioPackets.Dequeue();
                ffmpeg.av_packet_move_ref(packet, buffered);
                ffmpeg.av_packet_free(&buffered);
                return 0;
            }

            if (_audioEof)
                return ffmpeg.AVERROR_EOF;

            AVPacket* temp = ffmpeg.av_packet_alloc();
            if (temp == null)
                return -1;

            try
            {
                while (true)
                {
                    var result = _demuxer.ReadPacket(temp);
                    if (result < 0)
                    {
                        if (result == ffmpeg.AVERROR_EOF)
                            _audioEof = true;
                        return result;
                    }

                    if (temp->stream_index == _audioStreamIndex)
                    {
                        ffmpeg.av_packet_move_ref(packet, temp);
                        return 0;
                    }

                    if (_videoStreamIndex >= 0 && temp->stream_index == _videoStreamIndex)
                        BufferPacket(_videoPackets, temp);
                }
            }
            finally
            {
                ffmpeg.av_packet_free(&temp);
            }
        }
    }

    /// <summary>
    /// Coordinated seek: seeks the container once, flushes all registered packet queues,
    /// and clears their EOF flags. Called by <c>VideoPipeline.SeekCore</c> for
    /// settle (commit) seeks — NOT by the engines directly.
    /// </summary>
    /// <remarks>
    /// With the network read-ahead the seek is served from the buffered packets when the target is
    /// buffered (no network request); otherwise the queues are emptied and the reader seeks, and
    /// this returns at once (0) while the next reads wait for the new position.
    /// </remarks>
    public int Seek(int streamIndex, long timestamp, int flags)
    {
        if (_readAhead is { } readAhead)
            return readAhead.Seek(streamIndex, timestamp, flags);

        lock (_sync)
        {
            FlushQueue(_videoPackets);
            FlushQueue(_audioPackets);
            _videoEof = false;
            _audioEof = false;
            return _demuxer.Seek(streamIndex, timestamp, flags);
        }
    }

    /// <summary>
    /// Loop-wrap support: drains remaining audio packets into the audio queue,
    /// then seeks the cursor to 0. Flushes the video packet queue and clears
    /// video EOF. Does NOT touch the audio queue or audio EOF — audio will
    /// discover EOF when its queue drains, then call
    /// <see cref="ClearAudioEof"/>.
    /// Called by <c>VideoPipeline.TryPreSeekLoopStart</c> instead of
    /// <c>decoder.Seek(0)</c>. This preserves gapless video (position-0 frames
    /// ready immediately) AND gapless audio (near-EOF packets buffered).
    /// </summary>
    public void PrepareLoopWrap()
    {
        // The read-ahead already holds the tail packets and the end marker for the audio queue;
        // the reader seeks to 0 and the next lap's packets follow them.
        if (_readAhead is { } readAhead)
        {
            readAhead.RequestLoopWrap(_videoStreamIndex);
            return;
        }

        lock (_sync)
        {
            // Drain phase: read all remaining packets from the underlying demuxer
            // until EOF. Buffer audio packets for the audio consumer. Video packets
            // are unlikely (video already hit EOF) but buffered if they appear.
            AVPacket* temp = ffmpeg.av_packet_alloc();
            if (temp == null)
                return;

            try
            {
                while (_demuxer.ReadPacket(temp) >= 0)
                {
                    if (_audioStreamIndex >= 0 && temp->stream_index == _audioStreamIndex)
                        BufferPacket(_audioPackets, temp);
                    else if (_videoStreamIndex >= 0 && temp->stream_index == _videoStreamIndex)
                        BufferPacket(_videoPackets, temp);
                }
            }
            finally
            {
                ffmpeg.av_packet_free(&temp);
            }

            // The non-video streams are now at EOF.
            _audioEof = true;

            // Seek phase: seek the cursor to 0 on the video stream.
            if (_videoStreamIndex >= 0)
                _ = _demuxer.Seek(_videoStreamIndex, 0, ffmpeg.AVSEEK_FLAG_BACKWARD);

            // Flush video packet queue (stale tail packets). Clear video EOF.
            FlushQueue(_videoPackets);
            _videoEof = false;
        }
    }

    /// <summary>
    /// Clears audio EOF only. The cursor is already at 0 from
    /// <see cref="PrepareLoopWrap"/>. Does NOT seek, does NOT flush video.
    /// Called by <c>VideoAudioEngine.WrapDecoderForLoop</c> (via
    /// <c>SeekDecoder(0)</c> in shared mode) when audio's queue drains and it
    /// discovers EOF.
    /// </summary>
    public void ClearAudioEof()
    {
        if (_readAhead is { } readAhead)
        {
            readAhead.ClearEnd(_audioStreamIndex);
            return;
        }

        lock (_sync)
        {
            _audioEof = false;
        }
    }

    public void Close()
    {
        // The reader must have stopped before the format context closes.
        DisposeReadAhead();
        lock (_sync)
        {
            _demuxer.Close();
            FlushQueue(_videoPackets);
            FlushQueue(_audioPackets);
            _videoEof = false;
            _audioEof = false;
            _videoStreamIndex = -1;
            _audioStreamIndex = -1;
        }
    }

    public void Dispose()
    {
        Close();
        _demuxer.Dispose();
    }

    private void DisposeReadAhead()
    {
        // Disposed before it is forgotten: until then a late consumer read gets the end from it,
        // never the format context the reader is still using.
        NetworkReadAhead? readAhead = _readAhead;
        readAhead?.Dispose();
        _readAhead = null;
        _networkProbe = default;
    }

    private static void BufferPacket(Queue<IntPtr> queue, AVPacket* source)
    {
        AVPacket* buffered = ffmpeg.av_packet_alloc();
        if (buffered != null)
        {
            ffmpeg.av_packet_move_ref(buffered, source);
            queue.Enqueue((IntPtr)buffered);
        }
    }

    private static void FlushQueue(Queue<IntPtr> queue)
    {
        while (queue.Count > 0)
        {
            var pkt = (AVPacket*)queue.Dequeue();
            ffmpeg.av_packet_free(&pkt);
        }
    }
}
