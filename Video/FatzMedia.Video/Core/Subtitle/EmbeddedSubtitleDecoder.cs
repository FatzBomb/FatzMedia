using System.Buffers.Binary;
using System.Text;
using FatzMedia.Video.Core.Media;
using FFmpeg.AutoGen;

namespace FatzMedia.Video.Core.Subtitle;

/// <summary>
/// Extracts the supported embedded text subtitle streams of a local file. It reads the whole file,
/// so the player runs it on a background task after the video opened (ENG-039 P5-1). It owns a
/// separate demuxer so reading the complete subtitle track cannot consume or buffer packets needed
/// by the live audio/video demuxer.
/// </summary>
internal static unsafe class EmbeddedSubtitleDecoder
{
    /// <summary>Decodes the text subtitle tracks of <paramref name="path"/>.</summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled; checked before every packet.</exception>
    public static IReadOnlyList<EmbeddedSubtitleTrack> DecodeTracks(string path, CancellationToken cancellationToken = default)
    {
        using var demuxer = new MediaDemuxer();
        if (!demuxer.Open(path))
            return [];

        Dictionary<int, (AVRational TimeBase, AVCodecID CodecId)> streams = FindTextSubtitleStreams(demuxer);
        if (streams.Count == 0)
            return [];

        var playbackStartSeconds = GetPlaybackStartSeconds(demuxer);

        for (uint index = 0; index < demuxer.StreamCount; index++)
        {
            if (!streams.ContainsKey((int)index))
                demuxer.SetStreamDiscard((int)index, AVDiscard.AVDISCARD_ALL);
        }

        AVPacket* packet = ffmpeg.av_packet_alloc();
        if (packet == null)
            return [];

        try
        {
            var cuesByStream = streams.Keys.ToDictionary(static index => index, static _ => new List<SubtitleCue>());
            while (!cancellationToken.IsCancellationRequested && demuxer.ReadPacket(packet) >= 0)
            {
                if (streams.TryGetValue(packet->stream_index, out (AVRational TimeBase, AVCodecID CodecId) stream))
                    TryAddCue(packet, stream.TimeBase, stream.CodecId, playbackStartSeconds, cuesByStream[packet->stream_index]);

                ffmpeg.av_packet_unref(packet);
            }

            cancellationToken.ThrowIfCancellationRequested();
            return [.. streams.Select(stream => new EmbeddedSubtitleTrack(
                    stream.Key,
                    GetCodecName(stream.Value.CodecId),
                    ResolveCueEnds(cuesByStream[stream.Key])))
                .Where(static track => track.Cues.Count > 0)];
        }
        finally
        {
            ffmpeg.av_packet_free(&packet);
        }
    }

    private static Dictionary<int, (AVRational TimeBase, AVCodecID CodecId)> FindTextSubtitleStreams(MediaDemuxer demuxer)
    {
        var streams = new Dictionary<int, (AVRational TimeBase, AVCodecID CodecId)>();
        for (uint index = 0; index < demuxer.StreamCount; index++)
        {
            AVStream* stream = demuxer.GetStream((int)index);
            if (stream != null
                && stream->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_SUBTITLE
                && IsSupportedTextCodec(stream->codecpar->codec_id))
            {
                streams.Add((int)index, (stream->time_base, stream->codecpar->codec_id));
            }
        }

        return streams;
    }

    private static bool IsSupportedTextCodec(AVCodecID codecId) => codecId is
        AVCodecID.AV_CODEC_ID_ASS or
        AVCodecID.AV_CODEC_ID_MOV_TEXT or
        AVCodecID.AV_CODEC_ID_SSA or
        AVCodecID.AV_CODEC_ID_SUBRIP or
        AVCodecID.AV_CODEC_ID_TEXT or
        AVCodecID.AV_CODEC_ID_WEBVTT;

    private static string GetCodecName(AVCodecID codecId) => codecId.ToString().Replace("AV_CODEC_ID_", string.Empty, StringComparison.Ordinal);

    private static double GetPlaybackStartSeconds(MediaDemuxer demuxer)
    {
        // VideoPipeline uses VideoAudioEngine.Position when audio exists. VideoAudioEngine subtracts
        // its audio stream start time, so embedded cues must use that same origin to stay in sync.
        foreach (AVMediaType mediaType in new[] { AVMediaType.AVMEDIA_TYPE_AUDIO, AVMediaType.AVMEDIA_TYPE_VIDEO })
        {
            AVStream* stream = demuxer.GetStream(demuxer.FindBestStream(mediaType));
            if (stream != null && stream->start_time != ffmpeg.AV_NOPTS_VALUE)
                return stream->start_time * ffmpeg.av_q2d(stream->time_base);
        }

        return 0;
    }

    private static void TryAddCue(AVPacket* packet, AVRational timeBase, AVCodecID codecId, double playbackStartSeconds, List<SubtitleCue> cues)
    {
        if (packet->data == null || packet->size <= 0 || packet->pts == ffmpeg.AV_NOPTS_VALUE)
            return;

        var text = ExtractText(new ReadOnlySpan<byte>(packet->data, packet->size), codecId);
        if (text.Length == 0)
            return;

        var startSeconds = NormalizeTimestampSeconds(packet->pts, timeBase, playbackStartSeconds);
        var durationSeconds = packet->duration > 0 ? packet->duration * ffmpeg.av_q2d(timeBase) : 0;
        var start = TimeSpan.FromSeconds(Math.Max(0, startSeconds));
        TimeSpan end = durationSeconds > 0
            ? TimeSpan.FromSeconds(Math.Max(start.TotalSeconds + 0.1, start.TotalSeconds + durationSeconds))
            : start;
        cues.Add(new SubtitleCue(cues.Count + 1, start, end, text));
    }

    internal static double NormalizeTimestampSeconds(long timestamp, AVRational timeBase, double playbackStartSeconds) =>
        Math.Max(0, (timestamp * ffmpeg.av_q2d(timeBase)) - playbackStartSeconds);

    internal static IReadOnlyList<SubtitleCue> ResolveCueEnds(IReadOnlyList<SubtitleCue> cues)
    {
        SubtitleCue[] ordered = [.. cues.OrderBy(static cue => cue.Start).ThenBy(static cue => cue.Sequence)];
        var resolved = new SubtitleCue[ordered.Length];
        for (var index = 0; index < ordered.Length; index++)
        {
            SubtitleCue cue = ordered[index];
            TimeSpan end = cue.End;
            if (end <= cue.Start)
                end = index + 1 < ordered.Length && ordered[index + 1].Start > cue.Start
                    ? ordered[index + 1].Start
                    : cue.Start + TimeSpan.FromSeconds(3);

            resolved[index] = cue with { Sequence = index + 1, End = end };
        }

        return resolved;
    }

    private static string ExtractText(ReadOnlySpan<byte> payload, AVCodecID codecId)
    {
        if (codecId == AVCodecID.AV_CODEC_ID_MOV_TEXT)
        {
            if (payload.Length < 2)
                return string.Empty;

            var textLength = BinaryPrimitives.ReadUInt16BigEndian(payload);
            return textLength > payload.Length - 2 ? string.Empty : Encoding.UTF8.GetString(payload.Slice(2, textLength)).Trim();
        }

        var text = Encoding.UTF8.GetString(payload).Trim('\0', '\r', '\n', ' ');
        return codecId is AVCodecID.AV_CODEC_ID_ASS or AVCodecID.AV_CODEC_ID_SSA
            ? NormalizeAssText(text)
            : text;
    }

    private static string NormalizeAssText(string value)
    {
        if (value.StartsWith("Dialogue:", StringComparison.OrdinalIgnoreCase))
        {
            var dialogue = value["Dialogue:".Length..].Split(',', 10, StringSplitOptions.None);
            return AssParser.NormalizeDialogueText(dialogue.Length == 10 ? dialogue[9] : value);
        }

        // Matroska ASS packets omit start/end times, leaving nine fields.
        var packet = value.Split(',', 9, StringSplitOptions.None);
        return AssParser.NormalizeDialogueText(packet.Length == 9 ? packet[8] : value);
    }
}
