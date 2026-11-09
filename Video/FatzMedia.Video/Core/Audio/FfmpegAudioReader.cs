using FatzMedia.Video.Core.Media;
using FFmpeg.AutoGen;

namespace FatzMedia.Video.Core.Audio;

/// <summary>
/// Decodes a file's best audio stream to interleaved 32-bit float PCM at the source sample
/// rate, downmixed to at most two channels: the same format <see cref="VideoAudioEngine"/>
/// pushes to BASS. It owns its demuxer and decoder, so it can run on a background thread while
/// the same file plays.
/// </summary>
internal sealed unsafe class FfmpegAudioReader : IDisposable
{
    private const int AvErrorAgain = -11;

    private readonly MediaDemuxer _demuxer = new();
    private AVStream* _stream;
    private AVCodecContext* _codecContext;
    private SwrContext* _resampler;
    private AVFrame* _frame;
    private AVPacket* _packet;
    private int _streamIndex = -1;
    private double _streamStartSeconds;
    private float[] _samples = [];
    private bool _draining;

    private FfmpegAudioReader()
    {
    }

    /// <summary>Source sample rate in Hz.</summary>
    public int SampleRate { get; private set; }

    /// <summary>Output channel count: 1 for mono sources, otherwise 2.</summary>
    public int Channels { get; private set; }

    /// <summary>Container duration in seconds, or 0 when unknown.</summary>
    public double DurationSeconds { get; private set; }

    /// <summary>Opens <paramref name="path"/>; returns <see langword="null"/> when it has no decodable audio.</summary>
    public static FfmpegAudioReader? Open(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var reader = new FfmpegAudioReader();
        if (reader.OpenCore(path))
            return reader;

        reader.Dispose();
        return null;
    }

    /// <summary>
    /// Decodes the next audio frame. The returned interleaved samples stay valid until the next
    /// call; an empty span means the end of the stream.
    /// </summary>
    public ReadOnlySpan<float> ReadFrame()
    {
        while (true)
        {
            var received = ffmpeg.avcodec_receive_frame(_codecContext, _frame);
            if (received == 0)
            {
                ReadOnlySpan<float> samples = ConvertFrame();
                if (!samples.IsEmpty)
                    return samples;

                continue;
            }

            if (received != AvErrorAgain || _draining || !SendNextPacket())
                return [];
        }
    }

    /// <summary>
    /// Moves to the keyframe at or before <paramref name="seconds"/> and discards decoder state.
    /// Audio frames are short, so the next frame starts at most a fraction of a second early.
    /// </summary>
    public bool Seek(double seconds)
    {
        var timeBase = ffmpeg.av_q2d(_stream->time_base);
        if (timeBase <= 0)
            return false;

        var timestamp = (long)((Math.Max(0, seconds) + _streamStartSeconds) / timeBase);
        if (_demuxer.Seek(_streamIndex, timestamp, ffmpeg.AVSEEK_FLAG_BACKWARD) < 0)
            return false;

        ffmpeg.avcodec_flush_buffers(_codecContext);
        _draining = false;
        return true;
    }

    public void Dispose()
    {
        if (_packet != null)
        {
            fixed (AVPacket** packet = &_packet)
                ffmpeg.av_packet_free(packet);
        }

        if (_frame != null)
        {
            fixed (AVFrame** frame = &_frame)
                ffmpeg.av_frame_free(frame);
        }

        if (_resampler != null)
        {
            fixed (SwrContext** resampler = &_resampler)
                ffmpeg.swr_free(resampler);
        }

        if (_codecContext != null)
        {
            fixed (AVCodecContext** context = &_codecContext)
                ffmpeg.avcodec_free_context(context);
        }

        _stream = null;
        _demuxer.Dispose();
    }

    private bool OpenCore(string path)
    {
        if (!_demuxer.Open(path))
            return false;

        _streamIndex = _demuxer.FindBestStream(AVMediaType.AVMEDIA_TYPE_AUDIO);
        _stream = _demuxer.GetStream(_streamIndex);
        if (_stream == null)
            return false;

        _demuxer.DiscardAllStreamsExcept([_streamIndex]);

        if (!FfmpegAudioDecoderSetup.TryOpen(_stream, out _codecContext, out _resampler, out var sampleRate, out var channels))
            return false;

        SampleRate = sampleRate;
        Channels = channels;

        _frame = ffmpeg.av_frame_alloc();
        _packet = ffmpeg.av_packet_alloc();
        if (_frame == null || _packet == null)
            return false;

        _streamStartSeconds = _stream->start_time == ffmpeg.AV_NOPTS_VALUE
            ? 0
            : Math.Max(0, _stream->start_time * ffmpeg.av_q2d(_stream->time_base));
        var formatDuration = _demuxer.FormatDurationTicks;
        DurationSeconds = formatDuration > 0 ? formatDuration / (double)ffmpeg.AV_TIME_BASE : 0;
        return true;
    }

    private bool SendNextPacket()
    {
        while (true)
        {
            if (_demuxer.ReadPacket(_packet) < 0)
            {
                // End of input: enter draining mode so the decoder flushes its last frames.
                _draining = true;
                return ffmpeg.avcodec_send_packet(_codecContext, null) >= 0;
            }

            var isTarget = _packet->stream_index == _streamIndex;
            var sent = isTarget ? ffmpeg.avcodec_send_packet(_codecContext, _packet) : AvErrorAgain;
            ffmpeg.av_packet_unref(_packet);

            // A corrupt packet is skipped rather than ending the stream.
            if (isTarget && sent >= 0)
                return true;
        }
    }

    private ReadOnlySpan<float> ConvertFrame()
    {
        var capacity = ffmpeg.swr_get_out_samples(_resampler, _frame->nb_samples);
        if (capacity <= 0)
            return [];

        var required = capacity * Channels;
        if (_samples.Length < required)
            _samples = new float[required];

        int converted;
        fixed (float* destination = _samples)
        {
            var output = (byte*)destination;
            converted = ffmpeg.swr_convert(_resampler, &output, capacity, _frame->extended_data, _frame->nb_samples);
        }

        return converted > 0 ? _samples.AsSpan(0, converted * Channels) : [];
    }
}
