using FFmpeg.AutoGen;

namespace FatzMedia.Video.Core.Audio;

/// <summary>
/// Opens an audio stream's decoder and a resampler to interleaved 32-bit float PCM at the
/// source sample rate, downmixed to at most two channels. Shared by
/// <see cref="FfmpegAudioReader"/> and <see cref="VideoAudioEngine"/>.
/// </summary>
internal static unsafe class FfmpegAudioDecoderSetup
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
        out int channels)
    {
        codecContext = null;
        resampler = null;
        sampleRate = 0;
        channels = 0;

        AVCodec* codec = ffmpeg.avcodec_find_decoder(stream->codecpar->codec_id);
        if (codec == null)
            return false;

        AVCodecContext* context = ffmpeg.avcodec_alloc_context3(codec);
        if (context == null)
            return false;

        if (ffmpeg.avcodec_parameters_to_context(context, stream->codecpar) < 0
            || ffmpeg.avcodec_open2(context, codec, null) < 0
            || !TryCreateResampler(context, out var swr, out var rate, out var outputChannels))
        {
            ffmpeg.avcodec_free_context(&context);
            return false;
        }

        codecContext = context;
        resampler = swr;
        sampleRate = rate;
        channels = outputChannels;
        return true;
    }

    private static bool TryCreateResampler(AVCodecContext* context, out SwrContext* resampler, out int sampleRate, out int channels)
    {
        resampler = null;
        channels = 0;
        sampleRate = context->sample_rate;
        if (sampleRate <= 0)
            return false;

        var sourceChannels = Math.Max(1, context->ch_layout.nb_channels);
        channels = sourceChannels > 2 ? 2 : sourceChannels;

        AVChannelLayout inputLayout = context->ch_layout;
        if (inputLayout.nb_channels <= 0 || ffmpeg.av_channel_layout_check(&inputLayout) == 0)
            ffmpeg.av_channel_layout_default(&inputLayout, sourceChannels);

        var outputLayout = new AVChannelLayout();
        ffmpeg.av_channel_layout_default(&outputLayout, channels);

        SwrContext* swr = null;
        if (ffmpeg.swr_alloc_set_opts2(&swr, &outputLayout, AVSampleFormat.AV_SAMPLE_FMT_FLT, sampleRate, &inputLayout, context->sample_fmt, sampleRate, 0, null) < 0)
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
