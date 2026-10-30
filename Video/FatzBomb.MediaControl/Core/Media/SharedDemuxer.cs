using FatzBomb.MediaControl.Core.Network;
using FFmpeg.AutoGen;

namespace FatzBomb.MediaControl.Core.Media;

/// <summary>
/// Wraps a <see cref="MediaDemuxer"/> for the video and audio consumers of one source. A network
/// source is read once, by a read-ahead that fans its packets out to the registered streams; a
/// local file is read inline by its one consumer.
/// </summary>
/// <remarks>
/// <para>
/// - <b>Network read-ahead (Stage 039A slice 2)</b>: a source opened with
///   <see cref="Open(string, FfmpegNetworkOptions)"/> is read by a <see cref="NetworkReadAhead"/>
///   reader thread instead of the consumers. Reads take packets from its per-stream queues
///   (waiting for them unless the thread limited its waits), <see cref="Seek"/> is served from the
///   queues when the target is buffered and otherwise hands the seek to the reader,
///   <see cref="PrepareLoopWrap"/> and <see cref="ClearAudioEof"/> keep the audio gapless across a
///   loop, and the consumer lock below is not used.
/// </para>
/// <para>
/// - <b>Local files</b> have one consumer per cursor: the video pipeline's picture (which
///   discards every other stream) or an audio engine on its own cursor. A read returns the next
///   packet of the consumer's stream and drops any other (ENG-039 P5-5 removed the inline
///   fan-out, which no local source used). End of stream is per consumer, and
///   <see cref="Seek"/> clears it.
/// </para>
/// </remarks>
internal sealed unsafe class SharedDemuxer : IDisposable
{
    private readonly MediaDemuxer _demuxer = new();
    private readonly Lock _sync = new();
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

    public void DiscardAllStreamsExcept(ReadOnlySpan<int> keep) => _demuxer.DiscardAllStreamsExcept(keep);

    /// <inheritdoc cref="MediaDemuxer.SelectVideoStream"/>
    public int SelectVideoStream() => _demuxer.SelectVideoStream();

    /// <inheritdoc cref="MediaDemuxer.SelectAudioStream"/>
    public int SelectAudioStream() => _demuxer.SelectAudioStream();

    /// <summary>
    /// Network sources: discards every stream no consumer registered, and re-enables the
    /// registered ones (a consumer that opened first may have discarded them). FFmpeg's HLS and
    /// DASH demuxers otherwise download the segments of every variant (039A smoke). Local files
    /// are left as they are: their one consumer discards the streams it does not play.
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
    /// keeping its packets and discards its stream again (ENG-034: HLS and DASH otherwise keep
    /// downloading its segments). The reader applies the discard between its reads.
    /// </summary>
    public void UnregisterAudioStream()
    {
        if (_audioStreamIndex < 0)
            return;

        // The picture may share the stream index only in theory; never discard what it plays.
        _readAhead?.Unregister(_audioStreamIndex, discard: _audioStreamIndex != _videoStreamIndex);
        _audioStreamIndex = -1;
    }

    /// <summary>
    /// Takes the in-stream title change playback has reached (see
    /// <see cref="MediaDemuxer.TryTakeStreamMetadata"/>). Never waits for network I/O.
    /// </summary>
    public bool TryTakeStreamMetadata(double playedUpToSeconds, out StreamMetadata metadata) =>
        _demuxer.TryTakeStreamMetadata(playedUpToSeconds, out metadata);

    /// <summary>
    /// Reads the next video packet into <paramref name="packet"/>. Returns 0 on success,
    /// <see cref="ffmpeg.AVERROR_EOF"/> at end of stream, or a negative error code. EOF is
    /// per consumer: audio's is not affected.
    /// </summary>
    public int ReadVideoPacket(AVPacket* packet)
    {
        if (_readAhead is { } readAhead)
            return readAhead.Read(_videoStreamIndex, packet);

        lock (_sync)
            return ReadInline(_videoStreamIndex, ref _videoEof, packet);
    }

    /// <summary>
    /// Reads the next audio packet into <paramref name="packet"/>. Returns 0 on success,
    /// <see cref="ffmpeg.AVERROR_EOF"/> at end of stream, or a negative error code. EOF is
    /// per consumer: video's is not affected.
    /// </summary>
    public int ReadAudioPacket(AVPacket* packet)
    {
        if (_readAhead is { } readAhead)
            return readAhead.Read(_audioStreamIndex, packet);

        lock (_sync)
            return ReadInline(_audioStreamIndex, ref _audioEof, packet);
    }

    /// <summary>
    /// Coordinated seek: seeks the container once and clears the consumers' EOF flags. Called by <c>VideoPipeline.SeekCore</c> for
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
            _videoEof = false;
            _audioEof = false;
            return _demuxer.Seek(streamIndex, timestamp, flags);
        }
    }

    /// <summary>
    /// Loop-wrap support, called by <c>VideoPipeline.TryPreSeekLoopStart</c> instead of
    /// <c>decoder.Seek(0)</c> so position-0 video frames are ready at once. With the network
    /// read-ahead the audio keeps the tail packets it has not played yet and finds its end before
    /// the next lap's packets (gapless). A local file's audio has its own cursor, so only the
    /// picture's cursor goes back to 0 and its EOF is cleared.
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
            if (_videoStreamIndex >= 0)
                _ = _demuxer.Seek(_videoStreamIndex, 0, ffmpeg.AVSEEK_FLAG_BACKWARD);

            _videoEof = false;
        }
    }

    /// <summary>
    /// Clears audio EOF only. The cursor is already at 0 from
    /// <see cref="PrepareLoopWrap"/>. Does NOT seek. Called by
    /// <c>VideoAudioEngine.WrapDecoderForLoop</c> (via <c>SeekDecoder(0)</c> in shared mode,
    /// which only a network source uses) when audio's queue drains and it discovers EOF.
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

    // Under _sync: the next packet of the stream, dropping any other (a local cursor discards the
    // streams its consumer does not play, so there are normally none).
    private int ReadInline(int streamIndex, ref bool endOfStream, AVPacket* packet)
    {
        if (endOfStream)
            return ffmpeg.AVERROR_EOF;

        while (true)
        {
            var result = _demuxer.ReadPacket(packet);
            if (result < 0)
            {
                if (result == ffmpeg.AVERROR_EOF)
                    endOfStream = true;
                return result;
            }

            if (packet->stream_index == streamIndex)
                return 0;

            ffmpeg.av_packet_unref(packet);
        }
    }
}
