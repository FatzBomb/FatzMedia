using FFmpeg.AutoGen;

namespace FatzMedia.Video.Core.Audio;

/// <summary>
/// Opens an audio stream's decoder and a resampler to interleaved 32-bit float PCM at the
/// source sample rate, downmixed to at most two channels. Shared by
/// <see cref="FfmpegAudioReader"/> and <see cref="VideoAudioEngine"/>. A caller that wants a file's samples
/// exactly (the audio fallback) can ask for 16-bit integer output where the decoder produces 16-bit integers
/// and nothing is downmixed, which is lossless and half the size of float.
/// </summary>
static unsafe class FfmpegAudioDecoderSetup
{
    /// <summary>
    /// Opens the decoder and resampler. On success the caller owns <paramref name="codecContext"/>
    /// and <paramref name="resampler"/>; on failure both are freed and null.
    /// </summary>
    public static bool TryOpen(
        AVStream* stream,
        out AVCodecContext* codecContext,
        out SwrContext* resampler,
        out int sampleRate,
        out int channels) =>
        TryOpen(stream, allowInt16: false, out codecContext, out resampler, out sampleRate, out channels, out _);

    /// <summary>
    /// Opens the decoder and resampler; with <paramref name="allowInt16"/>, interleaved 16-bit integers
    /// (<paramref name="isInt16"/>) instead of float when the decoder's own format is 16-bit integer and the
    /// channels are not downmixed, so the samples are unchanged.
    /// </summary>
    public static bool TryOpen(
        AVStream* stream,
        bool allowInt16,
        out AVCodecContext* codecContext,
        out SwrContext* resampler,
        out int sampleRate,
        out int channels,
        out bool isInt16)
    {
        isInt16 = false;
        codecContext = null;
        resampler = null;
        sampleRate = 0;
        channels = 0;

        var codec = ffmpeg.avcodec_find_decoder(stream->codecpar->codec_id);
        if (codec == null)
            return false;

        var context = ffmpeg.avcodec_alloc_context3(codec);
        if (context == null)
            return false;

        if (ffmpeg.avcodec_parameters_to_context(context, stream->codecpar) < 0
            || ffmpeg.avcodec_open2(context, codec, null) < 0
            || !TryCreateResampler(context, allowInt16, out var swr, out var rate, out var outputChannels, out var int16))
        {
            ffmpeg.avcodec_free_context(&context);
            return false;
        }

        codecContext = context;
        resampler = swr;
        sampleRate = rate;
        channels = outputChannels;
        isInt16 = int16;
        return true;
    }

    /// <summary>True when the decoder's samples are already 16-bit integers (planar or interleaved).</summary>
    internal static bool IsInt16Format(AVSampleFormat format) =>
        format is AVSampleFormat.AV_SAMPLE_FMT_S16 or AVSampleFormat.AV_SAMPLE_FMT_S16P;

    static bool TryCreateResampler(AVCodecContext* context, bool allowInt16, out SwrContext* resampler, out int sampleRate, out int channels, out bool isInt16)
    {
        resampler = null;
        channels = 0;
        isInt16 = false;
        sampleRate = context->sample_rate;
        if (sampleRate <= 0)
            return false;

        var sourceChannels = Math.Max(1, context->ch_layout.nb_channels);
        channels = sourceChannels > 2 ? 2 : sourceChannels;
        // Integers only when nothing is mixed: the same 16-bit values, so no precision is lost.
        isInt16 = allowInt16 && sourceChannels <= 2 && IsInt16Format(context->sample_fmt);

        var inputLayout = context->ch_layout;
        if (inputLayout.nb_channels <= 0 || ffmpeg.av_channel_layout_check(&inputLayout) == 0)
            ffmpeg.av_channel_layout_default(&inputLayout, sourceChannels);

        var outputLayout = new AVChannelLayout();
        ffmpeg.av_channel_layout_default(&outputLayout, channels);

        SwrContext* swr = null;
        if (ffmpeg.swr_alloc_set_opts2(&swr, &outputLayout, isInt16 ? AVSampleFormat.AV_SAMPLE_FMT_S16 : AVSampleFormat.AV_SAMPLE_FMT_FLT, sampleRate, &inputLayout, context->sample_fmt, sampleRate, 0, null) < 0)
            return false;

        if (swr == null)
            return false;

        if (ffmpeg.swr_init(swr) < 0)
        {
            ffmpeg.swr_free(&swr);
            return false;
        }

        resampler = swr;
        return true;
    }
}
