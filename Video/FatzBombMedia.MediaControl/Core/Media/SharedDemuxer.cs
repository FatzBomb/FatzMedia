using FFmpeg.AutoGen;

namespace FatzBombMedia.MediaControl.Core.Media;

/// <summary>
/// Wraps a <see cref="MediaDemuxer"/> with per-stream packet fan-out: a single
/// <c>av_read_frame</c> cursor dispatches packets into independent per-stream
/// queues, so two consumers (video, audio) can read from one container without
/// each opening it separately.
/// </summary>
/// <remarks>
/// Introduced by stage 012A.S3 (revised). Key fixes from the reverted attempt:
/// <para>
/// - <b>Single <see cref="Seek"/></b> that flushes both queues and clears both
///   EOFs. No <c>SeekVideo</c>/<c>SeekAudio</c> split (the reverted S3's split
///   caused the second seek to override the first).
/// </para>
/// <para>
/// - <b>Per-consumer EOF</b>: <see cref="ReadVideoPacket"/> only sets
///   <c>_videoEof</c>; <see cref="ReadAudioPacket"/> only sets <c>_audioEof</c>.
///   No cross-stream propagation (the reverted S3's propagation caused
///   premature audio EOF).
/// </para>
/// <para>
/// - <b><see cref="PrepareLoopWrap"/></b>: drains remaining audio packets into
///   the audio queue, then seeks to 0. Called by
///   <c>VideoPipeline.TryPreSeekLoopStart</c> instead of <c>decoder.Seek(0)</c>.
///   Preserves gapless video (position-0 frames ready immediately) AND gapless
///   audio (near-EOF packets buffered in queue, BASS never stops).
/// </para>
/// <para>
/// - <b><see cref="ClearAudioEof"/></b>: clears audio EOF only (cursor already
///   at 0 from <see cref="PrepareLoopWrap"/>). Called by
///   <c>VideoAudioEngine.WrapDecoderForLoop</c>.
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

    public bool IsOpen => _demuxer.IsOpen;
    public uint StreamCount => _demuxer.StreamCount;
    public long FormatDurationTicks => _demuxer.FormatDurationTicks;

    public bool Open(string path) => _demuxer.Open(path);

    public int FindBestStream(AVMediaType mediaType) => _demuxer.FindBestStream(mediaType);

    public AVStream* GetStream(int index) => _demuxer.GetStream(index);

    public void SetStreamDiscard(int index, AVDiscard discard) => _demuxer.SetStreamDiscard(index, discard);

    public void RegisterVideoStream(int index) => _videoStreamIndex = index;

    public void RegisterAudioStream(int index) => _audioStreamIndex = index;

    /// <summary>
    /// Reads the next video packet into <paramref name="packet"/>. Non-video
    /// packets are buffered for the audio consumer (if registered) or skipped.
    /// Returns 0 on success, <see cref="ffmpeg.AVERROR_EOF"/> at end of stream,
    /// or a negative error code. EOF is per-consumer: only <c>_videoEof</c> is
    /// set — audio's EOF is not affected.
    /// </summary>
    public int ReadVideoPacket(AVPacket* packet)
    {
        lock (_sync)
        {
            if (_videoPackets.Count > 0)
            {
                AVPacket* buffered = (AVPacket*)_videoPackets.Dequeue();
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
                    int result = _demuxer.ReadPacket(temp);
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
    /// packets are buffered for the video consumer (if registered) or skipped.
    /// Returns 0 on success, <see cref="ffmpeg.AVERROR_EOF"/> at end of stream,
    /// or a negative error code. EOF is per-consumer: only <c>_audioEof</c> is
    /// set — video's EOF is not affected.
    /// </summary>
    public int ReadAudioPacket(AVPacket* packet)
    {
        lock (_sync)
        {
            if (_audioPackets.Count > 0)
            {
                AVPacket* buffered = (AVPacket*)_audioPackets.Dequeue();
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
                    int result = _demuxer.ReadPacket(temp);
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
    /// Coordinated seek: seeks the container once, flushes BOTH packet queues,
    /// and clears BOTH EOF flags. Called by <c>VideoPipeline.SeekCore</c> for
    /// settle (commit) seeks — NOT by the engines directly.
    /// </summary>
    public int Seek(int streamIndex, long timestamp, int flags)
    {
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

            // Both streams are now at EOF.
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
        lock (_sync)
        {
            _audioEof = false;
        }
    }

    public void Close()
    {
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
            AVPacket* pkt = (AVPacket*)queue.Dequeue();
            ffmpeg.av_packet_free(&pkt);
        }
    }
}
