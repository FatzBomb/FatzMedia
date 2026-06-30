
using FatzBombMedia.MediaControl.Core.Abstractions;
using FFmpeg.AutoGen;
using ManagedBass;
using ManagedBass.Fx;
using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace FatzBombMedia.MediaControl.Core.Audio;

internal sealed unsafe class VideoAudioEngine : IAudioEngine
{
    private readonly Lock _sync = new();
    private AVFormatContext* _formatContext;
    private AVCodecContext* _codecContext;
    private AVStream* _audioStream;
    private SwrContext* _resampler;
    private AVFrame* _frame;
    private int _audioStreamIndex = -1;
    private byte* _audioBuffer;
    private int _audioBufferSize;
    private int _bassStream;
    private int _sampleRate;
    private int _sourceChannels;
    private int _channels;
    private double _streamStartSeconds;
    private double? _discardAudioBeforeSeconds;
    private double _positionBaseSeconds;
    private double _volume = 1.0;
    private int _pitch;
    private bool _normalize = true;
    private string? _tempPath;
    private long _pushedAudioFrameCount;
    private bool _disposed;

    public AudioEngineState State { get; private set; } = AudioEngineState.Stopped;
    public int PlayHandle { get; private set; }
    public bool Normalize
    {
        get
        {
            lock (_sync)
            {
                return _normalize;
            }
        }
        set
        {
            lock (_sync)
            {
                _normalize = value;
                _ = ApplyVolume();
            }
        }
    }

    public double NormalizationGain { get; private set; } = 1.0;
    public bool IsNormalizationReady => true;
    public int CurrentTempo { get; private set; }
    public double Duration { get; private set; }
    public bool IsOpen
    {
        get
        {
            lock (_sync)
            {
                return IsOpenCore;
            }
        }
    }

    public double Position
    {
        get
        {
            lock (_sync)
            {
                if (PlayHandle == 0)
                {
                    return _positionBaseSeconds;
                }

                var bytes = Bass.ChannelGetPosition(PlayHandle);
                var seconds = Bass.ChannelBytes2Seconds(PlayHandle, bytes);
                return _positionBaseSeconds + (double.IsFinite(seconds) ? Math.Max(0, seconds) : 0);
            }
        }
        set => Seek(value);
    }

    public event EventHandler<double>? NormalizationGainChanged;
    public event EventHandler<AudioEngineState>? StateChanged;
    public event EventHandler? Opened;
    public event EventHandler? Closed;
    public event EventHandler<double>? PositionChanged;
    event EventHandler? IAudioEngine.PlaybackEnded
    {
        add { }
        remove { }
    }

    public bool Init(int device = -1, int sampleRate = 44100) => true;

    private bool IsOpenCore => _formatContext != null && _audioStreamIndex >= 0 && PlayHandle != 0;

    public bool Open(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        lock (_sync)
        {
            if (_disposed)
            {
                return false;
            }

            CloseCore();

            fixed (AVFormatContext** context = &_formatContext)
            {
                if (ffmpeg.avformat_open_input(context, filePath, null, null) < 0)
                {
                    return false;
                }
            }

            if (ffmpeg.avformat_find_stream_info(_formatContext, null) < 0)
            {
                CloseCore();
                return false;
            }

            _audioStreamIndex = ffmpeg.av_find_best_stream(_formatContext, AVMediaType.AVMEDIA_TYPE_AUDIO, -1, -1, null, 0);
            if (_audioStreamIndex < 0)
            {
                CloseCore();
                return false;
            }

            _audioStream = _formatContext->streams[_audioStreamIndex];
            _streamStartSeconds = GetStreamStartSeconds(_audioStream);
            if (!InitializeDecoder() || !CreateBassStreams())
            {
                CloseCore();
                return false;
            }

            Duration = _formatContext->duration > 0 ? _formatContext->duration / (double)ffmpeg.AV_TIME_BASE : 0;
            State = AudioEngineState.Stopped;
            Opened?.Invoke(this, EventArgs.Empty);
            Debug.WriteLine($"[VideoAudioEngine] Opened audio stream: {_sampleRate}Hz, {_sourceChannels}ch -> {_channels}ch, duration {Duration:F2}s");
            return true;
        }
    }

    public bool Open(IArchiveEntryInfo archiveEntry)
    {
        ArgumentNullException.ThrowIfNull(archiveEntry);
        using var ms = new MemoryStream((int)Math.Min(archiveEntry.Size ?? 0, int.MaxValue));
        archiveEntry.WriteTo(ms);
        return OpenBytes(ms.ToArray());
    }

    private bool OpenBytes(byte[] data)
    {
        var tempPath = CreateTempMediaPath(data);
        var success = Open(tempPath);
        if (success)
        {
            lock (_sync)
            {
                _tempPath = tempPath;
            }
        }
        else
        {
            TryDeleteTempPath(tempPath);
        }

        return success;
    }

    public bool DecodeAndPushAudio()
    {
        lock (_sync)
        {
            return DecodeAndPushAudioCore();
        }
    }

    private bool DecodeAndPushAudioCore()
    {
        if (!IsOpenCore || _formatContext == null)
        {
            return false;
        }

        AVPacket* packet = ffmpeg.av_packet_alloc();
        if (packet == null)
        {
            return false;
        }

        try
        {
            while (ffmpeg.av_read_frame(_formatContext, packet) >= 0)
            {
                if (packet->stream_index == _audioStreamIndex)
                {
                    return ProcessAudioPacket(packet);
                }

                ffmpeg.av_packet_unref(packet);
            }

            return false;
        }
        finally
        {
            ffmpeg.av_packet_unref(packet);
            ffmpeg.av_packet_free(&packet);
        }
    }

    public bool Play()
    {
        lock (_sync)
        {
            return PlayCore();
        }
    }

    private bool PlayCore()
    {
        if (PlayHandle == 0)
        {
            return false;
        }

        var success = Bass.ChannelPlay(PlayHandle);
        if (success)
        {
            SetState(AudioEngineState.Playing);
        }

        return success;
    }

    public bool Pause()
    {
        lock (_sync)
        {
            return PauseCore();
        }
    }

    private bool PauseCore()
    {
        if (PlayHandle == 0)
        {
            return false;
        }

        var success = Bass.ChannelPause(PlayHandle);
        if (success)
        {
            SetState(AudioEngineState.Paused);
        }

        return success;
    }

    public bool Stop()
    {
        lock (_sync)
        {
            return StopCore();
        }
    }

    private bool StopCore()
    {
        _positionBaseSeconds = 0;
        if (PlayHandle != 0)
        {
            _ = Bass.ChannelStop(PlayHandle);
            _ = Bass.ChannelSetPosition(PlayHandle, 0);
        }

        if (_bassStream != 0)
        {
            _ = Bass.ChannelSetPosition(_bassStream, 0);
        }

        _ = SeekDecoder(0);
        SetState(AudioEngineState.Stopped);
        PositionChanged?.Invoke(this, 0);
        return true;
    }

    public bool Seek(double seconds) => Seek(seconds, resumePlayback: true);

    public bool Seek(double seconds, bool resumePlayback)
    {
        lock (_sync)
        {
            return SeekCore(seconds, resumePlayback);
        }
    }

    private bool SeekCore(double seconds, bool resumePlayback)
    {
        if (!IsOpenCore)
        {
            return false;
        }

        var target = Duration > 0 ? Math.Clamp(seconds, 0, Duration) : Math.Max(0, seconds);
        var wasPlaying = State == AudioEngineState.Playing;
        if (PlayHandle != 0)
        {
            _ = Bass.ChannelPause(PlayHandle);
            _ = Bass.ChannelSetPosition(PlayHandle, 0);
        }

        if (_bassStream != 0)
        {
            _ = Bass.ChannelSetPosition(_bassStream, 0);
        }

        if (!SeekDecoder(target))
        {
            return false;
        }

        _positionBaseSeconds = target;
        if (resumePlayback && wasPlaying && PlayHandle != 0)
        {
            _ = Bass.ChannelPlay(PlayHandle);
            SetState(AudioEngineState.Playing);
        }

        PositionChanged?.Invoke(this, target);
        return true;
    }

    public bool PrebufferAudio(int minimumFrames = 8, int maxDecodeAttempts = 128)
    {
        lock (_sync)
        {
            if (!IsOpenCore || minimumFrames <= 0 || maxDecodeAttempts <= 0)
            {
                return false;
            }

            var startingFrameCount = _pushedAudioFrameCount;
            var attempts = 0;
            while (_pushedAudioFrameCount - startingFrameCount < minimumFrames && attempts++ < maxDecodeAttempts)
            {
                if (!DecodeAndPushAudioCore())
                {
                    break;
                }
            }

            return _pushedAudioFrameCount > startingFrameCount;
        }
    }

    public void Close()
    {
        lock (_sync)
        {
            CloseCore();
        }
    }

    private void CloseCore()
    {
        if (_disposed)
        {
            return;
        }

        if (PlayHandle != 0)
        {
            _ = Bass.ChannelStop(PlayHandle);
            _ = Bass.StreamFree(PlayHandle);
            PlayHandle = 0;
        }

        if (_bassStream != 0)
        {
            _ = Bass.StreamFree(_bassStream);
            _bassStream = 0;
        }

        if (_audioBuffer != null)
        {
            ffmpeg.av_free(_audioBuffer);
            _audioBuffer = null;
        }

        if (_resampler != null)
        {
            fixed (SwrContext** resampler = &_resampler)
            {
                ffmpeg.swr_free(resampler);
            }
        }

        if (_frame != null)
        {
            fixed (AVFrame** frame = &_frame)
            {
                ffmpeg.av_frame_free(frame);
            }
        }

        if (_codecContext != null)
        {
            fixed (AVCodecContext** context = &_codecContext)
            {
                ffmpeg.avcodec_free_context(context);
            }
        }

        if (_formatContext != null)
        {
            fixed (AVFormatContext** context = &_formatContext)
            {
                ffmpeg.avformat_close_input(context);
            }
        }

        _audioStream = null;
        _audioStreamIndex = -1;
        _streamStartSeconds = 0;
        _discardAudioBeforeSeconds = null;
        _positionBaseSeconds = 0;
        _pushedAudioFrameCount = 0;
        Duration = 0;
        CleanupTempPath();
        SetState(AudioEngineState.Stopped);
        Closed?.Invoke(this, EventArgs.Empty);
    }

    public bool SetVolume(double volume01)
    {
        lock (_sync)
        {
            _volume = Math.Clamp(volume01, 0.0, 1.0);
            return ApplyVolume();
        }
    }

    public bool SetPitch(int semitones)
    {
        lock (_sync)
        {
            _pitch = Math.Clamp(semitones, -60, 60);
            return PlayHandle != 0 && Bass.ChannelSetAttribute(PlayHandle, ChannelAttribute.Pitch, _pitch);
        }
    }

    public bool SetTempo(int percent)
    {
        lock (_sync)
        {
            CurrentTempo = Math.Clamp(percent, -95, 5000);
            return PlayHandle != 0 && Bass.ChannelSetAttribute(PlayHandle, ChannelAttribute.Tempo, CurrentTempo);
        }
    }

    public Task<double> AnalyzePeakQuickAsync(CancellationToken cancellationToken)
    {
        NormalizationGainChanged?.Invoke(this, NormalizationGain);
        return Task.FromResult(NormalizationGain);
    }

    public Task<double> AnalyzePeakAsync(CancellationToken cancellationToken)
    {
        NormalizationGainChanged?.Invoke(this, NormalizationGain);
        return Task.FromResult(NormalizationGain);
    }

    public void Dispose()
    {
        Close();
        lock (_sync)
        {
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }

    private bool InitializeDecoder()
    {
        AVCodec* codec = ffmpeg.avcodec_find_decoder(_audioStream->codecpar->codec_id);
        if (codec == null)
        {
            return false;
        }

        _codecContext = ffmpeg.avcodec_alloc_context3(codec);
        if (_codecContext == null || ffmpeg.avcodec_parameters_to_context(_codecContext, _audioStream->codecpar) < 0)
        {
            return false;
        }

        if (ffmpeg.avcodec_open2(_codecContext, codec, null) < 0)
        {
            return false;
        }

        _frame = ffmpeg.av_frame_alloc();
        if (_frame == null)
        {
            return false;
        }

        _sampleRate = _codecContext->sample_rate;
        _sourceChannels = Math.Max(1, _codecContext->ch_layout.nb_channels);
        _channels = _sourceChannels > 2 ? 2 : _sourceChannels;

        AVChannelLayout inputLayout = _codecContext->ch_layout;
        if (inputLayout.nb_channels <= 0 || ffmpeg.av_channel_layout_check(&inputLayout) == 0)
        {
            ffmpeg.av_channel_layout_default(&inputLayout, _sourceChannels);
        }

        var outputLayout = new AVChannelLayout();
        ffmpeg.av_channel_layout_default(&outputLayout, _channels);

        SwrContext* resampler = null;
        if (ffmpeg.swr_alloc_set_opts2(
            &resampler,
            &outputLayout,
            AVSampleFormat.AV_SAMPLE_FMT_FLT,
            _sampleRate,
            &inputLayout,
            _codecContext->sample_fmt,
            _sampleRate,
            0,
            null) < 0)
        {
            return false;
        }

        _resampler = resampler;
        if (_resampler == null || ffmpeg.swr_init(_resampler) < 0)
        {
            return false;
        }

        _audioBufferSize = _sampleRate * _channels * sizeof(float);
        _audioBuffer = (byte*)ffmpeg.av_malloc((ulong)_audioBufferSize);
        return _audioBuffer != null;
    }

    private bool CreateBassStreams()
    {
        BassFlags flags = BassFlags.Decode | BassFlags.Float;
        if (_channels == 1)
        {
            flags |= BassFlags.Mono;
        }

        _bassStream = Bass.CreateStream(_sampleRate, _channels, flags, StreamProcedureType.Push);
        if (_bassStream == 0)
        {
            Debug.WriteLine($"[VideoAudioEngine] Failed to create BASS push stream: {Bass.LastError}");
            return false;
        }

        PlayHandle = BassFx.TempoCreate(_bassStream, BassFlags.Default);
        if (PlayHandle == 0)
        {
            Debug.WriteLine($"[VideoAudioEngine] Failed to create tempo stream: {Bass.LastError}");
            return false;
        }

        _ = ApplyVolume();
        _ = Bass.ChannelSetAttribute(PlayHandle, ChannelAttribute.Pitch, _pitch);
        _ = Bass.ChannelSetAttribute(PlayHandle, ChannelAttribute.Tempo, CurrentTempo);
        return true;
    }

    private bool ProcessAudioPacket(AVPacket* packet)
    {
        var processed = false;
        if (ffmpeg.avcodec_send_packet(_codecContext, packet) < 0)
        {
            return false;
        }

        while (ffmpeg.avcodec_receive_frame(_codecContext, _frame) == 0)
        {
            PushAudioFrame();
            processed = true;
        }

        return processed;
    }

    private void PushAudioFrame()
    {
        var framePosition = GetFramePositionSeconds(_frame);
        if (_discardAudioBeforeSeconds.HasValue)
        {
            if (framePosition < _discardAudioBeforeSeconds.Value)
            {
                return;
            }

            _discardAudioBeforeSeconds = null;
        }

        var output = _audioBuffer;
        var samples = ffmpeg.swr_convert(
            _resampler,
            &output,
            _audioBufferSize / (_channels * sizeof(float)),
            _frame->extended_data,
            _frame->nb_samples);

        if (samples <= 0)
        {
            return;
        }

        var byteCount = samples * _channels * sizeof(float);
        var data = ArrayPool<byte>.Shared.Rent(byteCount);
        try
        {
            Marshal.Copy((IntPtr)output, data, 0, byteCount);
            if (Bass.StreamPutData(_bassStream, data, byteCount) > 0)
            {
                _pushedAudioFrameCount++;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(data);
        }
    }

    private bool SeekDecoder(double seconds)
    {
        if (_formatContext == null || _audioStream == null)
        {
            return false;
        }

        var timestamp = (long)((seconds + _streamStartSeconds) / ffmpeg.av_q2d(_audioStream->time_base));
        if (ffmpeg.av_seek_frame(_formatContext, _audioStreamIndex, timestamp, ffmpeg.AVSEEK_FLAG_BACKWARD) < 0)
        {
            return false;
        }

        if (_codecContext != null)
        {
            ffmpeg.avcodec_flush_buffers(_codecContext);
        }

        _discardAudioBeforeSeconds = seconds;
        return true;
    }

    private double GetFramePositionSeconds(AVFrame* frame)
    {
        if (_audioStream == null || frame == null)
            return 0;

        var timeBase = ffmpeg.av_q2d(_audioStream->time_base);
        var timestamp = frame->best_effort_timestamp;
        if (timestamp == ffmpeg.AV_NOPTS_VALUE)
            timestamp = frame->pts;

        return timestamp == ffmpeg.AV_NOPTS_VALUE ? _positionBaseSeconds : Math.Max(0, (timestamp * timeBase) - _streamStartSeconds);
    }

    private static double GetStreamStartSeconds(AVStream* stream) => stream == null || stream->start_time == ffmpeg.AV_NOPTS_VALUE
            ? 0
            : Math.Max(0, stream->start_time * ffmpeg.av_q2d(stream->time_base));

    private bool ApplyVolume()
    {
        if (PlayHandle == 0)
            return false;

        var effectiveVolume = _volume * (_normalize ? NormalizationGain : 1.0);
        return Bass.ChannelSetAttribute(PlayHandle, ChannelAttribute.Volume, effectiveVolume);
    }

    private void SetState(AudioEngineState state)
    {
        if (State == state)
            return;

        State = state;
        StateChanged?.Invoke(this, state);
    }

    private static string CreateTempMediaPath(byte[] data)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "FatzBombMediaControl");
        _ = Directory.CreateDirectory(tempDir);
        var tempPath = Path.Combine(tempDir, Guid.NewGuid().ToString("N") + ".media");
        File.WriteAllBytes(tempPath, data);
        return tempPath;
    }

    private void CleanupTempPath()
    {
        if (string.IsNullOrWhiteSpace(_tempPath))
        {
            return;
        }

        try
        {
            TryDeleteTempPath(_tempPath);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VideoAudioEngine] Failed to delete temp audio source: {ex.Message}");
        }
        finally
        {
            _tempPath = null;
        }
    }

    private static void TryDeleteTempPath(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
