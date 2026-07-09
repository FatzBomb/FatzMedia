using FFmpeg.AutoGen;

namespace FatzBombMedia.MediaControl.Core.Media;

/// <summary>
/// Owns a single FFmpeg <see cref="AVFormatContext"/> and exposes the container
/// open / stream-discovery / packet-read / seek / close primitives. This is the
/// input/demux seam introduced by stage 012A: it carries no decode, timing, or
/// audio concerns — callers keep their own codec contexts, packets, and frames.
/// </summary>
/// <remarks>
/// Behavior-preserving extraction only (012A.S1). Each primitive mirrors the
/// exact FFmpeg call the caller used inline before, including result codes, so
/// EOF / error handling at the call site is unchanged. A demuxer instance owns
/// exactly one container and is single-threaded from the perspective of its
/// owner (one decode thread), matching the previous inline usage.
/// </remarks>
internal sealed unsafe class MediaDemuxer : IDisposable
{
    private AVFormatContext* _formatContext;

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
    public bool Open(string path)
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
    public int ReadPacket(AVPacket* packet) =>
        _formatContext == null ? ffmpeg.AVERROR_EOF : ffmpeg.av_read_frame(_formatContext, packet);

    /// <summary>
    /// Wraps <c>av_seek_frame</c> against <paramref name="streamIndex"/>.
    /// Returns the FFmpeg result code (&lt; 0 on failure). Returns a negative
    /// value when closed.
    /// </summary>
    public int Seek(int streamIndex, long timestamp, int flags) =>
        _formatContext == null ? ffmpeg.AVERROR_EOF : ffmpeg.av_seek_frame(_formatContext, streamIndex, timestamp, flags);

    /// <summary>Closes the container and releases the format context.</summary>
    public void Close()
    {
        if (_formatContext != null)
        {
            fixed (AVFormatContext** pfmt = &_formatContext)
                ffmpeg.avformat_close_input(pfmt);
        }
    }

    public void Dispose() => Close();
}
