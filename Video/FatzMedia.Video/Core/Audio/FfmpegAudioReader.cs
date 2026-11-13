using System.Runtime.InteropServices;
using FatzMedia.Video.Core.Media;
using FFmpeg.AutoGen;

namespace FatzMedia.Video.Core.Audio;

/// <summary>
/// Decodes a file's best audio stream to interleaved 32-bit float PCM at the source sample
/// rate, downmixed to at most two channels: the same format <see cref="VideoAudioEngine"/>
/// pushes to its output. With <c>allowInt16Pcm</c>, a source the decoder produces as 16-bit integers (and
/// that needs no downmix) comes out as 16-bit integers instead, unchanged and half the size. It owns its demuxer and decoder, so it can run on a background thread while
/// the same file plays.
/// </summary>
sealed unsafe class FfmpegAudioReader : IDisposable
{
    const int AvErrorAgain = -11;

    readonly MediaDemuxer _demuxer = new();
    AVStream* _stream;
    AVCodecContext* _codecContext;
    SwrContext* _resampler;
    AVFrame* _frame;
    AVPacket* _packet;
    int _streamIndex = -1;
    double _streamStartSeconds;
    float[] _samples = [];
    short[] _shorts = [];
    bool _draining;

    FfmpegAudioReader()
    {
    }

    /// <summary>Source sample rate in Hz.</summary>
    public int SampleRate { get; private set; }

    /// <summary>Output channel count: 1 for mono sources, otherwise 2.</summary>
    public int Channels { get; private set; }

    /// <summary>Container duration in seconds, or 0 when unknown.</summary>
    public double DurationSeconds { get; private set; }

    /// <summary>True when the samples are 16-bit integers (<see cref="ReadFrameBytes"/>), false for 32-bit float.</summary>
    public bool IsInt16 { get; private set; }

    /// <summary>Bytes per sample of the output: 2 for 16-bit integers, 4 for float.</summary>
    public int BytesPerSample => IsInt16 ? sizeof(short) : sizeof(float);

    /// <summary>Opens <paramref name="path"/>; returns <see langword="null"/> when it has no decodable audio.</summary>
    public static FfmpegAudioReader? Open(string path) => Open(path, allowInt16Pcm: false);

    /// <summary>
    /// Opens <paramref name="path"/>, with 16-bit integer output where the source is 16-bit and needs no
    /// downmix (<paramref name="allowInt16Pcm"/>); returns <see langword="null"/> when it has no decodable audio.
    /// </summary>
    public static FfmpegAudioReader? Open(string path, bool allowInt16Pcm)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var reader = new FfmpegAudioReader();
        if (reader.OpenCore(path, allowInt16Pcm))
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
        if (IsInt16)
            throw new InvalidOperationException("The reader outputs 16-bit integers: use ReadFrameBytes.");

        var count = ReadFrameCore();
        return count > 0 ? _samples.AsSpan(0, count) : [];
    }

    /// <summary>
    /// Decodes the next audio frame as little-endian interleaved bytes of the output format
    /// (<see cref="IsInt16"/>, <see cref="BytesPerSample"/>). The bytes stay valid until the next call;
    /// an empty span means the end of the stream.
    /// </summary>
    public ReadOnlySpan<byte> ReadFrameBytes()
    {
        var count = ReadFrameCore();
        if (count <= 0)
            return [];

        return IsInt16
            ? MemoryMarshal.AsBytes(_shorts.AsSpan(0, count))
            : MemoryMarshal.AsBytes(_samples.AsSpan(0, count));
    }

    // The next frame's samples (all channels) in the buffer of the output format; 0 at the end of the stream.
    int ReadFrameCore()
    {
        while (true)
        {
            var received = ffmpeg.avcodec_receive_frame(_codecContext, _frame);
            if (received == 0)
            {
                var count = ConvertFrame();
                if (count > 0)
                    return count;

                continue;
            }

            if (received != AvErrorAgain || _draining || !SendNextPacket())
                return 0;
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

    bool OpenCore(string path, bool allowInt16Pcm)
    {
        if (!_demuxer.Open(path))
            return false;

        _streamIndex = _demuxer.FindBestStream(AVMediaType.AVMEDIA_TYPE_AUDIO);
        _stream = _demuxer.GetStream(_streamIndex);
        if (_stream == null)
            return false;

        _demuxer.DiscardAllStreamsExcept([_streamIndex]);

        if (!FfmpegAudioDecoderSetup.TryOpen(_stream, allowInt16Pcm, out _codecContext, out _resampler, out var sampleRate, out var channels, out var isInt16))
            return false;

        IsInt16 = isInt16;

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

    bool SendNextPacket()
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

    int ConvertFrame()
    {
        var capacity = ffmpeg.swr_get_out_samples(_resampler, _frame->nb_samples);
        if (capacity <= 0)
            return 0;

        var required = capacity * Channels;
        int converted;
        if (IsInt16)
        {
            if (_shorts.Length < required)
                _shorts = new short[required];

            converted = Convert(_shorts, capacity);
        }
        else
        {
            if (_samples.Length < required)
                _samples = new float[required];

            converted = Convert(_samples, capacity);
        }

        return converted > 0 ? converted * Channels : 0;
    }

    int Convert<T>(T[] buffer, int capacity) where T : unmanaged
    {
        fixed (T* destination = buffer)
        {
            var output = (byte*)destination;
            return ffmpeg.swr_convert(_resampler, &output, capacity, _frame->extended_data, _frame->nb_samples);
        }
    }
}
