using FatzBomb.MediaControl.Core.Abstractions;
using FatzBomb.MediaControl.Core.Audio.Fade;
using FatzBomb.MediaControl.Core.Audio.Filters;
using FatzBomb.MediaControl.Core.Files;
using FatzBomb.MediaControl.Core.Media;
using FFmpeg.AutoGen;
using ManagedBass;
using ManagedBass.Fx;
using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace FatzBomb.MediaControl.Core.Audio;

internal sealed unsafe class VideoAudioEngine : IAudioEngine, IAudioLoopControl, IAudioFadeGainTarget, IAudioFilterHost
{
    private const double AudioBufferHighWatermarkSeconds = 4.0;
    private const double AudioBufferLowWatermarkSeconds = 2.0;
    // The quick normalization pass may attenuate but not boost; see RunNormalizationAnalysis.
    private const double MaxQuickPassGain = 1.0;
    private static readonly ConcurrentDictionary<string, double> NormalizationGainCache = new(StringComparer.OrdinalIgnoreCase);

    private readonly IOutputLatencyProvider _outputLatencyProvider;
    private readonly int _bassDeviceIndex;
    private readonly Lock _sync = new();
    private readonly SharedDemuxer _demuxer;
    private readonly bool _ownsDemuxer;
    private AVCodecContext* _codecContext;
    private AVStream* _audioStream;
    private SwrContext* _resampler;
    private AVFrame* _frame;
    private byte* _audioBuffer;
    private int _audioBufferSize;
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
    private double _lastPushedAudioEndSeconds;
    private long _positionSnapshotBits;
    private long _positionSnapshotTimestamp;
    private long _positionSnapshotDurationBits;
    private int _positionSnapshotAvailable;
    private int _positionSnapshotPlaying;
    private int _positionSnapshotTempoPercent;
    private bool _audioPushThrottled;
    private bool _disposed;

    // Effects chain on PlayHandle, in the same order as AudioEngine: equalizer, normalization
    // gain, fade envelope, then the channel volume attribute (user volume).
    private readonly BassEqualizerFilter _equalizer = new();
    private readonly AudioNormalizer _normalizer = new();
    private readonly BassFadeGainStage _fade = new("VideoAudioEngine");

    // Normalization runs on a background FfmpegAudioReader. _gainGate serializes gain FX writes
    // between it and the Normalize setter; the analysis never takes _sync, so CloseCore can join
    // it while holding _sync.
    private readonly Lock _gainGate = new();
    private CancellationTokenSource? _normalizationCts;
    private Task? _normalizationTask;
    private int _normalizationGeneration;
    private volatile bool _normalizationReady = true;
    private readonly Lock _gainEventGate = new();
    private Task _gainEvents = Task.CompletedTask;

    // Gapless loop bookkeeping (see AudioLoopBoundaryTracker). _pendingLoopBoundaries and
    // _completedLoopSeconds are only ever touched from within _sync (GetPositionCore and
    // WrapDecoderForLoop are both always called under lock).
    private readonly Queue<double> _pendingLoopBoundaries = new();
    private double _completedLoopSeconds;

    /// <summary>
    /// When true, the decoder seeks back to 0 and keeps pushing into the same BASS channel
    /// at end-of-stream instead of stopping - the channel never stops, so there is no audio
    /// gap at the loop boundary. Set by the owning pipeline before/while playing looped media.
    /// </summary>
    public bool LoopPlayback { get; set; }

    /// <summary>
    /// Total number of loop wraps whose audio has finished playing this session. Written
    /// under _sync on the decode thread (via GetPositionCore); read without a lock from the
    /// owning pipeline's presentation thread, so both sides use Volatile to publish/observe
    /// the value safely across threads.
    /// </summary>
    public int CompletedLoopCount
    {
        get => Volatile.Read(ref field);
        private set => Volatile.Write(ref field, value);
    }

    public AudioEngineState State { get; private set; } = AudioEngineState.Stopped;
    public int PlayHandle { get; private set; }
    public int SourceHandle { get; private set; }
    public bool Normalize
    {
        get
        {
            lock (_sync)
                return _normalize;
        }
        set
        {
            lock (_sync)
            {
                Volatile.Write(ref _normalize, value);
                lock (_gainGate)
                    _normalizer.SetNormalizeEnabled(value);
            }
        }
    }

    public double NormalizationGain => _normalizer.NormalizationGain;
    public bool IsNormalizationReady => _normalizationReady;
    public int CurrentTempo { get; private set; }
    public double Duration { get; private set; }
    public bool IsOpen
    {
        get
        {
            lock (_sync)
                return IsOpenCore;
        }
    }

    public double Position
    {
        get
        {
            lock (_sync)
            {
                var rawPosition = GetPositionCore();
                var corrected = ApplyOutputLatency(rawPosition);
                PublishPositionSnapshot(corrected);
                return corrected;
            }
        }
        set => Seek(value);
    }

    internal bool TryGetPositionSnapshot(out double position)
    {
        if (Volatile.Read(ref _positionSnapshotAvailable) == 0)
        {
            position = 0;
            return false;
        }

        position = BitConverter.Int64BitsToDouble(Interlocked.Read(ref _positionSnapshotBits));
        if (Volatile.Read(ref _positionSnapshotPlaying) != 0)
        {
            var timestamp = Interlocked.Read(ref _positionSnapshotTimestamp);
            var elapsedSeconds = (Stopwatch.GetTimestamp() - timestamp) / (double)Stopwatch.Frequency;
            if (double.IsFinite(elapsedSeconds) && elapsedSeconds > 0)
            {
                var tempoPercent = Volatile.Read(ref _positionSnapshotTempoPercent);
                var rate = Math.Max(0.01, 1.0 + (tempoPercent / 100.0));
                position += elapsedSeconds * rate;
            }
        }

        var duration = BitConverter.Int64BitsToDouble(Interlocked.Read(ref _positionSnapshotDurationBits));
        position = duration > 0 ? Math.Clamp(position, 0, duration) : Math.Max(0, position);
        return true;
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

    internal int AudioStreamIndex { get; private set; } = -1;

    public VideoAudioEngine() : this(new BassOutputLatencyProvider())
    {
    }

    public VideoAudioEngine(IOutputLatencyProvider outputLatencyProvider)
        : this(outputLatencyProvider, AudioOutputDeviceInfo.PrimaryDeviceIndex)
    {
    }

    internal VideoAudioEngine(IOutputLatencyProvider outputLatencyProvider, int bassDeviceIndex)
        : this(outputLatencyProvider, bassDeviceIndex, null)
    {
    }

    internal VideoAudioEngine(IOutputLatencyProvider outputLatencyProvider, int bassDeviceIndex, SharedDemuxer? sharedDemuxer)
    {
        _outputLatencyProvider = outputLatencyProvider ?? throw new ArgumentNullException(nameof(outputLatencyProvider));
        _bassDeviceIndex = bassDeviceIndex;
        _demuxer = sharedDemuxer ?? new SharedDemuxer();
        _ownsDemuxer = sharedDemuxer == null;
    }

    private double ApplyOutputLatency(double rawPositionSeconds)
    {
        if (rawPositionSeconds <= 0 || !double.IsFinite(rawPositionSeconds))
            return rawPositionSeconds;

        // BASS reports latency for the current device, so position sampling selects this engine's device first.
        _ = BassAudioOutputDevices.TrySetCurrentDevice(_bassDeviceIndex, "VideoAudioEngine", raiseDiagnostic: false);

        var latency = _outputLatencyProvider.GetOutputLatencySeconds();
        if (latency <= 0 || !double.IsFinite(latency))
            return rawPositionSeconds;

        var corrected = rawPositionSeconds - latency;
        return corrected < 0 ? 0 : corrected;
    }

    private bool IsOpenCore => _demuxer.IsOpen && AudioStreamIndex >= 0 && PlayHandle != 0;

    // The raw BASS channel position, never reset across a loop wrap (only Seek/Stop touch
    // _positionBaseSeconds or the channel's own position counter). This is the timeline that
    // loop boundaries are recorded against - see AudioLoopBoundaryTracker.
    private double GetContinuousPositionCore()
    {
        if (PlayHandle == 0)
            return _positionBaseSeconds;

        var bytes = Bass.ChannelGetPosition(PlayHandle);
        var seconds = Bass.ChannelBytes2Seconds(PlayHandle, bytes);
        return _positionBaseSeconds + (double.IsFinite(seconds) ? Math.Max(0, seconds) : 0);
    }

    private double GetPositionCore()
    {
        var continuous = GetContinuousPositionCore();
        AdvanceCompletedLoops(continuous);
        return Math.Max(0, continuous - _completedLoopSeconds);
    }

    private void AdvanceCompletedLoops(double continuousPositionSeconds)
    {
        var crossed = AudioLoopBoundaryTracker.Advance(continuousPositionSeconds, _pendingLoopBoundaries, ref _completedLoopSeconds);
        if (crossed > 0)
        {
            CompletedLoopCount += crossed;

            // A fade that ran to the end of a lap must not leave the next lap muted.
            _fade.EndSession(releaseHandle: false);
        }
    }

    private void PublishPositionSnapshot(double position)
    {
        var cleanPosition = double.IsFinite(position) && position > 0 ? position : 0;
        if (Duration > 0)
            cleanPosition = Math.Min(cleanPosition, Duration);

        _ = Interlocked.Exchange(ref _positionSnapshotTimestamp, Stopwatch.GetTimestamp());
        _ = Interlocked.Exchange(ref _positionSnapshotDurationBits, BitConverter.DoubleToInt64Bits(Duration));
        _ = Interlocked.Exchange(ref _positionSnapshotBits, BitConverter.DoubleToInt64Bits(cleanPosition));
        Volatile.Write(ref _positionSnapshotTempoPercent, CurrentTempo);
        Volatile.Write(ref _positionSnapshotPlaying, State == AudioEngineState.Playing ? 1 : 0);
        Volatile.Write(ref _positionSnapshotAvailable, IsOpenCore ? 1 : 0);
    }

    private void ClearPositionSnapshot()
    {
        _ = Interlocked.Exchange(ref _positionSnapshotTimestamp, Stopwatch.GetTimestamp());
        _ = Interlocked.Exchange(ref _positionSnapshotDurationBits, 0);
        _ = Interlocked.Exchange(ref _positionSnapshotBits, 0);
        Volatile.Write(ref _positionSnapshotTempoPercent, 0);
        Volatile.Write(ref _positionSnapshotPlaying, 0);
        Volatile.Write(ref _positionSnapshotAvailable, 0);
    }

    public bool Open(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        return OpenFile(filePath, CreatePathNormalizationCacheKey(filePath));
    }

    private bool OpenFile(string filePath, string? normalizationCacheKey)
    {
        lock (_sync)
        {
            if (_disposed)
                return false;

            CloseCore();

            if (!_demuxer.IsOpen && !_demuxer.Open(filePath))
                return false;

            AudioStreamIndex = _demuxer.FindBestStream(AVMediaType.AVMEDIA_TYPE_AUDIO);
            if (AudioStreamIndex < 0)
            {
                CloseCore();
                return false;
            }

            _demuxer.RegisterAudioStream(AudioStreamIndex);

            _audioStream = _demuxer.GetStream(AudioStreamIndex);
            _streamStartSeconds = GetStreamStartSeconds(_audioStream);
            if (!InitializeDecoder() || !CreateBassStreams())
            {
                CloseCore();
                return false;
            }

            var formatDurationTicks = _demuxer.FormatDurationTicks;
            Duration = formatDurationTicks > 0 ? formatDurationTicks / (double)ffmpeg.AV_TIME_BASE : 0;
            State = AudioEngineState.Stopped;
            PublishPositionSnapshot(_positionBaseSeconds);
            StartNormalizationAnalysis(filePath, normalizationCacheKey);
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
        return OpenBytes(ms.ToArray(), $"archive|{archiveEntry.Key}|{archiveEntry.Size?.ToString() ?? "unknown"}");
    }

    private bool OpenBytes(byte[] data, string normalizationCacheKey)
    {
        var tempPath = TempMediaFile.Create(data);
        var success = OpenFile(tempPath, normalizationCacheKey);
        if (success)
            lock (_sync)
                _tempPath = tempPath;
        else
            TempMediaFile.TryDelete(tempPath);

        return success;
    }

    public bool DecodeAndPushAudio()
    {
        lock (_sync)
            return DecodeAndPushAudioCore();
    }

    private bool DecodeAndPushAudioCore()
    {
        if (!IsOpenCore || !_demuxer.IsOpen)
            return false;

        if (IsAudioBufferThrottledCore())
        {
            // The push is throttled (2-4s of audio already buffered), but the position must
            // still be sampled: GetPositionCore advances the gapless-loop bookkeeping
            // (AdvanceCompletedLoops / CompletedLoopCount) as the played position crosses a
            // recorded lap boundary. Near a loop wrap the buffer stays full for seconds at a
            // time - skipping the sample here would delay CompletedLoopCount (and the video
            // side's wrap commit) by up to the full buffer drain, leaving the video frozen
            // on the old lap's tail while the audio is already playing the next lap, then
            // forcing a visible fast-forward catch-up when the late commit finally lands.
            PublishPositionSnapshot(GetPositionCore());
            return false;
        }

        AVPacket* packet = ffmpeg.av_packet_alloc();
        if (packet == null)
            return false;

        try
        {
            if (_demuxer.ReadAudioPacket(packet) >= 0)
            {
                var processed = ProcessAudioPacket(packet);
                if (processed)
                    PublishPositionSnapshot(GetPositionCore());
                return processed;
            }

            // End of stream. Gapless loop: seek the FFmpeg decoder back to 0 and keep pushing
            // PCM into the same BASS push stream instead of stopping it - the channel never
            // stops, so there is no audio gap at the loop boundary. Mirrors
            // BackgroundVideoLoop's decoder-only pre-seek pattern.
            return LoopPlayback && Duration > 0 && WrapDecoderForLoop();
        }
        finally
        {
            ffmpeg.av_packet_unref(packet);
            ffmpeg.av_packet_free(&packet);
        }
    }

    // Records the continuous-timeline mark at which this lap's pushed audio ends, then seeks
    // the FFmpeg decoder back to 0. The BASS channel and its position counter are untouched,
    // so playback continues without interruption; PushAudioFrame's existing monotonic guard
    // (see below) keeps _lastPushedAudioEndSeconds increasing across the wrap automatically.
    private bool WrapDecoderForLoop()
    {
        _pendingLoopBoundaries.Enqueue(_lastPushedAudioEndSeconds);

        if (SeekDecoder(0))
            return true;

        // Wrap failed - undo the pending mark so AdvanceCompletedLoops never sees a
        // boundary that will never actually be reached.
        _ = _pendingLoopBoundaries.Count > 0 ? _pendingLoopBoundaries.Dequeue() : 0;
        return false;
    }

    public bool Play()
    {
        lock (_sync)
            return PlayCore();
    }

    private bool PlayCore()
    {
        if (PlayHandle == 0)
            return false;

        var success = Bass.ChannelPlay(PlayHandle);
        if (success)
        {
            SetState(AudioEngineState.Playing);
            PublishPositionSnapshot(GetPositionCore());
        }

        return success;
    }

    public bool Pause()
    {
        lock (_sync)
            return PauseCore();
    }

    private bool PauseCore()
    {
        if (PlayHandle == 0)
            return false;

        var success = Bass.ChannelPause(PlayHandle);
        if (success)
        {
            SetState(AudioEngineState.Paused);
            PublishPositionSnapshot(GetPositionCore());
        }

        return success;
    }

    public bool Stop()
    {
        lock (_sync)
            return StopCore();
    }

    private bool StopCore()
    {
        _positionBaseSeconds = 0;
        if (PlayHandle != 0)
        {
            _ = Bass.ChannelStop(PlayHandle);
            _ = Bass.ChannelSetPosition(PlayHandle, 0);
        }

        if (SourceHandle != 0)
            _ = Bass.ChannelSetPosition(SourceHandle, 0);

        ResetAudioBufferTracking();
        _ = SeekDecoder(0);
        _fade.EndSession(releaseHandle: false);
        SetState(AudioEngineState.Stopped);
        PublishPositionSnapshot(0);
        PositionChanged?.Invoke(this, 0);
        return true;
    }

    public bool Seek(double seconds) => Seek(seconds, resumePlayback: true);

    public bool Seek(double seconds, bool resumePlayback)
    {
        lock (_sync)
            return SeekCore(seconds, resumePlayback);
    }

    private bool SeekCore(double seconds, bool resumePlayback)
    {
        if (!IsOpenCore)
            return false;

        var target = Duration > 0 ? Math.Clamp(seconds, 0, Duration) : Math.Max(0, seconds);
        var wasPlaying = State == AudioEngineState.Playing;
        if (PlayHandle != 0)
        {
            _ = Bass.ChannelPause(PlayHandle);
            _ = Bass.ChannelSetPosition(PlayHandle, 0);
        }

        if (SourceHandle != 0)
            _ = Bass.ChannelSetPosition(SourceHandle, 0);

        if (!SeekDecoder(target))
            return false;

        _positionBaseSeconds = target;
        ResetAudioBufferTracking();
        if (resumePlayback && wasPlaying && PlayHandle != 0)
        {
            _ = Bass.ChannelPlay(PlayHandle);
            SetState(AudioEngineState.Playing);
        }

        var correctedTarget = ApplyOutputLatency(target);
        PublishPositionSnapshot(correctedTarget);

        PositionChanged?.Invoke(this, correctedTarget);
        return true;
    }

    public bool PrebufferAudio(int minimumFrames = 8, int maxDecodeAttempts = 128)
    {
        lock (_sync)
        {
            if (!IsOpenCore || minimumFrames <= 0 || maxDecodeAttempts <= 0)
                return false;

            var startingFrameCount = _pushedAudioFrameCount;
            var attempts = 0;
            while (_pushedAudioFrameCount - startingFrameCount < minimumFrames && attempts++ < maxDecodeAttempts)
                if (!DecodeAndPushAudioCore())
                    break;

            return _pushedAudioFrameCount > startingFrameCount;
        }
    }

    public void Close()
    {
        lock (_sync)
            CloseCore();
    }

    private void CloseCore()
    {
        if (_disposed)
            return;

        // Join the analysis first: it reads the file (possibly a temp file deleted below) and
        // writes the gain FX on PlayHandle.
        CancelNormalizationAnalysis();
        _fade.EndSession(releaseHandle: true);
        _equalizer.Forget();
        lock (_gainGate)
            _normalizer.ResetGain(clearFxHandle: true);

        if (PlayHandle != 0)
        {
            _ = Bass.ChannelStop(PlayHandle);
            _ = Bass.StreamFree(PlayHandle);
            PlayHandle = 0;
        }

        if (SourceHandle != 0)
        {
            _ = Bass.StreamFree(SourceHandle);
            SourceHandle = 0;
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

        if (_ownsDemuxer)
            _demuxer.Close();

        _audioStream = null;
        AudioStreamIndex = -1;
        _streamStartSeconds = 0;
        _discardAudioBeforeSeconds = null;
        _positionBaseSeconds = 0;
        _pushedAudioFrameCount = 0;
        _lastPushedAudioEndSeconds = 0;
        _audioPushThrottled = false;
        _pendingLoopBoundaries.Clear();
        _completedLoopSeconds = 0;
        CompletedLoopCount = 0;
        LoopPlayback = false;
        Duration = 0;
        ClearPositionSnapshot();
        CleanupTempPath();
        SetState(AudioEngineState.Stopped);
        Closed?.Invoke(this, EventArgs.Empty);
    }

    public void SetVolume(double volume01)
    {
        lock (_sync)
        {
            _volume = Math.Clamp(volume01, 0.0, 1.0);
            _ = ApplyVolume();
        }
    }

    public void SetPitch(int semitones)
    {
        lock (_sync)
        {
            _pitch = Math.Clamp(semitones, -60, 60);
            if (PlayHandle != 0)
                _ = Bass.ChannelSetAttribute(PlayHandle, ChannelAttribute.Pitch, _pitch);
        }
    }

    public void SetTempo(int percent)
    {
        lock (_sync)
        {
            CurrentTempo = Math.Clamp(percent, -95, 5000);
            if (PlayHandle != 0)
                _ = Bass.ChannelSetAttribute(PlayHandle, ChannelAttribute.Tempo, CurrentTempo);
            if (IsOpenCore)
                PublishPositionSnapshot(GetPositionCore());
        }
    }

    // Analysis starts automatically on open; these report the gain it has reached so far.
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

    AudioEqualizerSettings IAudioFilterHost.Equalizer => _equalizer.Settings;

    bool IAudioFilterHost.SetEqualizer(AudioEqualizerSettings settings) => _equalizer.Configure(settings);

    int IAudioFadeGainTarget.BeginFadeSession() => _fade.BeginFadeSession();

    bool IAudioFadeGainTarget.SetFadeGain(int session, double gain, TimeSpan ramp) => _fade.SetFadeGain(session, gain, ramp);

    /// <summary>
    /// Restores full fade gain when playback reaches its natural end, so a fade-out that ran to
    /// the end does not leave a replay muted (the plain-audio engine does the same).
    /// </summary>
    internal void EndFadeSessionAtEnd() => _fade.EndSession(releaseHandle: false);

    public void Dispose()
    {
        Close();
        if (_ownsDemuxer)
            _demuxer.Dispose();
        lock (_sync)
            _disposed = true;
        GC.SuppressFinalize(this);
    }

    private bool InitializeDecoder()
    {
        AVCodec* codec = ffmpeg.avcodec_find_decoder(_audioStream->codecpar->codec_id);
        if (codec == null)
            return false;

        _codecContext = ffmpeg.avcodec_alloc_context3(codec);
        if (_codecContext == null || ffmpeg.avcodec_parameters_to_context(_codecContext, _audioStream->codecpar) < 0)
            return false;

        if (ffmpeg.avcodec_open2(_codecContext, codec, null) < 0)
            return false;

        _frame = ffmpeg.av_frame_alloc();
        if (_frame == null)
            return false;

        _sampleRate = _codecContext->sample_rate;
        _sourceChannels = Math.Max(1, _codecContext->ch_layout.nb_channels);
        _channels = _sourceChannels > 2 ? 2 : _sourceChannels;

        AVChannelLayout inputLayout = _codecContext->ch_layout;
        if (inputLayout.nb_channels <= 0 || ffmpeg.av_channel_layout_check(&inputLayout) == 0)
            ffmpeg.av_channel_layout_default(&inputLayout, _sourceChannels);

        var outputLayout = new AVChannelLayout();
        ffmpeg.av_channel_layout_default(&outputLayout, _channels);

        SwrContext* resampler = null;
        if (ffmpeg.swr_alloc_set_opts2(&resampler, &outputLayout, AVSampleFormat.AV_SAMPLE_FMT_FLT, _sampleRate, &inputLayout, _codecContext->sample_fmt, _sampleRate, 0, null) < 0)
            return false;

        _resampler = resampler;
        if (_resampler == null || ffmpeg.swr_init(_resampler) < 0)
            return false;

        _audioBufferSize = _sampleRate * _channels * sizeof(float);
        _audioBuffer = (byte*)ffmpeg.av_malloc((ulong)_audioBufferSize);
        return _audioBuffer != null;
    }

    private bool CreateBassStreams()
    {
        if (!BassAudioOutputDevices.TrySetCurrentDevice(_bassDeviceIndex, "VideoAudioEngine"))
            return false;

        BassFlags flags = BassFlags.Decode | BassFlags.Float;
        if (_channels == 1)
            flags |= BassFlags.Mono;

        SourceHandle = Bass.CreateStream(_sampleRate, _channels, flags, StreamProcedureType.Push);
        if (SourceHandle == 0)
        {
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Error, "VideoAudioEngine", $"Failed to create BASS push stream: {Bass.LastError}");
            return false;
        }

        PlayHandle = BassFx.TempoCreate(SourceHandle, BassFlags.Default);
        if (PlayHandle == 0)
        {
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Error, "VideoAudioEngine", $"Failed to create tempo stream: {Bass.LastError}");
            return false;
        }

        lock (_gainGate)
        {
            _normalizer.ResetGain(clearFxHandle: true);
            _ = _normalizer.SetupGainFx(PlayHandle);
            _normalizer.SetNormalizeEnabled(_normalize);
        }

        _fade.Attach(PlayHandle);
        _ = _equalizer.Apply(PlayHandle);
        _ = ApplyVolume();
        _ = Bass.ChannelSetAttribute(PlayHandle, ChannelAttribute.Pitch, _pitch);
        _ = Bass.ChannelSetAttribute(PlayHandle, ChannelAttribute.Tempo, CurrentTempo);
        return true;
    }

    private bool ProcessAudioPacket(AVPacket* packet)
    {
        var processed = false;
        if (ffmpeg.avcodec_send_packet(_codecContext, packet) < 0)
            return false;

        while (ffmpeg.avcodec_receive_frame(_codecContext, _frame) == 0)
        {
            PushAudioFrame();
            processed = true;
        }

        return processed;
    }

    private bool IsAudioBufferThrottledCore()
    {
        var bufferedSeconds = GetBufferedAudioSecondsCore();
        if (!double.IsFinite(bufferedSeconds) || bufferedSeconds <= 0)
        {
            _audioPushThrottled = false;
            return false;
        }

        if (_audioPushThrottled)
        {
            if (bufferedSeconds > AudioBufferLowWatermarkSeconds)
                return true;

            _audioPushThrottled = false;
            return false;
        }

        if (bufferedSeconds >= AudioBufferHighWatermarkSeconds)
        {
            _audioPushThrottled = true;
            return true;
        }

        return false;
    }

    // _lastPushedAudioEndSeconds is continuous (see PushAudioFrame's monotonic guard, which
    // keeps it increasing across a loop wrap), so it must be compared against the continuous
    // position, not the wrapped/reported one.
    private double GetBufferedAudioSecondsCore() =>
        _lastPushedAudioEndSeconds <= 0
            ? 0
            : Math.Max(0, _lastPushedAudioEndSeconds - GetContinuousPositionCore());

    private void ResetAudioBufferTracking()
    {
        _lastPushedAudioEndSeconds = _positionBaseSeconds;
        _audioPushThrottled = false;

        // A manual seek/stop breaks the natural continuous-playthrough assumption that
        // pending loop boundaries were recorded against - discard them.
        _pendingLoopBoundaries.Clear();
        _completedLoopSeconds = 0;
    }

    private void PushAudioFrame()
    {
        var framePosition = GetFramePositionSeconds(_frame);
        if (_discardAudioBeforeSeconds.HasValue)
        {
            if (framePosition < _discardAudioBeforeSeconds.Value)
                return;

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
            return;

        var byteCount = samples * _channels * sizeof(float);
        var data = ArrayPool<byte>.Shared.Rent(byteCount);
        try
        {
            Marshal.Copy((IntPtr)output, data, 0, byteCount);
            var written = Bass.StreamPutData(SourceHandle, data, byteCount);
            if (written > 0)
            {
                _pushedAudioFrameCount++;
                if (_sampleRate > 0)
                {
                    var frameDuration = samples / (double)_sampleRate;
                    var frameEndPosition = framePosition + frameDuration;
                    if (!double.IsFinite(frameEndPosition) || frameEndPosition < _lastPushedAudioEndSeconds)
                        frameEndPosition = _lastPushedAudioEndSeconds + frameDuration;

                    _lastPushedAudioEndSeconds = frameEndPosition;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(data);
        }
    }

    private bool SeekDecoder(double seconds)
    {
        if (!_demuxer.IsOpen || _audioStream == null)
            return false;

        // Shared-demuxer mode remains available for internal callers that coordinate the
        // container seek externally. Main-pipeline audio owns its cursor and uses the seek below.
        if (!_ownsDemuxer)
        {
            _demuxer.ClearAudioEof();
            if (_codecContext != null)
                ffmpeg.avcodec_flush_buffers(_codecContext);
            _discardAudioBeforeSeconds = seconds;
            return true;
        }

        var timestamp = (long)((seconds + _streamStartSeconds) / ffmpeg.av_q2d(_audioStream->time_base));
        if (_demuxer.Seek(AudioStreamIndex, timestamp, ffmpeg.AVSEEK_FLAG_BACKWARD) < 0)
            return false;

        if (_codecContext != null)
            ffmpeg.avcodec_flush_buffers(_codecContext);

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

    private static double GetStreamStartSeconds(AVStream* stream) =>
        stream == null || stream->start_time == ffmpeg.AV_NOPTS_VALUE
            ? 0
            : Math.Max(0, stream->start_time * ffmpeg.av_q2d(stream->time_base));

    private bool ApplyVolume()
    {
        if (PlayHandle == 0)
            return false;

        // Normalization is its own FX stage (it can amplify); the attribute carries user volume only.
        return Bass.ChannelSetAttribute(PlayHandle, ChannelAttribute.Volume, _volume);
    }

    private void StartNormalizationAnalysis(string filePath, string? cacheKey)
    {
        CancelNormalizationAnalysis();
        var generation = Volatile.Read(ref _normalizationGeneration);
        if (cacheKey is not null && NormalizationGainCache.TryGetValue(cacheKey, out var cachedGain))
        {
            ApplyNormalizationGain(cachedGain, cacheKey, generation, "Using cached normalization gain");
            return;
        }

        _normalizationReady = false;
        var cts = new CancellationTokenSource();
        _normalizationCts = cts;
        _normalizationTask = Task.Run(() => RunNormalizationAnalysis(filePath, cacheKey, generation, cts.Token));
    }

    private void RunNormalizationAnalysis(string filePath, string? cacheKey, int generation, CancellationToken cancellationToken)
    {
        try
        {
            using FfmpegAudioReader? reader = FfmpegAudioReader.Open(filePath);
            if (reader is null)
                return;

            // Film and TV soundtracks often open quietly, so the quick pass (first seconds) can ask
            // for the full 8x boost the deep pass then withdraws. Only let it attenuate; boosting
            // waits for the deep pass.
            var quickPeak = FfmpegAudioPeakScanner.ScanQuick(reader, cancellationToken);
            var quickGain = AudioNormalizer.CalculateGainFromPeak(quickPeak);
            ApplyNormalizationGain(
                Math.Min(quickGain, MaxQuickPassGain),
                cacheKey: null,
                generation,
                quickGain > MaxQuickPassGain ? "Quick normalization complete (boost waits for the full scan)" : "Quick normalization complete");

            var deepPeak = Math.Max(quickPeak, FfmpegAudioPeakScanner.ScanDeep(reader, cancellationToken));
            ApplyNormalizationPeak(deepPeak, cacheKey, generation, "Deep normalization complete");
        }
        catch (OperationCanceledException)
        {
            // Superseded by a new open or a close.
        }
        catch (Exception ex)
        {
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Warning, "VideoAudioEngine", $"Normalization analysis failed: {ex.Message}", innerException: ex);
        }
        finally
        {
            if (Volatile.Read(ref _normalizationGeneration) == generation)
                _normalizationReady = true;
        }
    }

    private void ApplyNormalizationPeak(double peak, string? cacheKey, int generation, string message) =>
        ApplyNormalizationGain(AudioNormalizer.CalculateGainFromPeak(peak), cacheKey, generation, message);

    private void ApplyNormalizationGain(double gain, string? cacheKey, int generation, string message)
    {
        lock (_gainGate)
        {
            if (Volatile.Read(ref _normalizationGeneration) != generation)
                return;

            _normalizer.SetNormalizationGain(gain);
            _normalizer.SetNormalizeEnabled(Volatile.Read(ref _normalize));
            _normalizationReady = true;
        }

        if (cacheKey is not null)
            NormalizationGainCache[cacheKey] = gain;

        DiagnosticRouter.Raise(MediaDiagnosticLevel.Info, "VideoAudioEngine", $"{message}, gain={gain:F3}");
        QueueNormalizationGainChanged(gain, generation);
    }

    /// <summary>
    /// Raises <see cref="NormalizationGainChanged"/> in order on the thread pool. Host handlers
    /// must never run on the analysis thread, which <see cref="CloseCore"/> joins under _sync.
    /// </summary>
    private void QueueNormalizationGainChanged(double gain, int generation)
    {
        lock (_gainEventGate)
        {
            _gainEvents = _gainEvents.ContinueWith(
                _ =>
                {
                    if (Volatile.Read(ref _normalizationGeneration) == generation)
                        NormalizationGainChanged?.Invoke(this, gain);
                },
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default);
        }
    }

    private void CancelNormalizationAnalysis()
    {
        _ = Interlocked.Increment(ref _normalizationGeneration);
        CancellationTokenSource? cts = _normalizationCts;
        Task? task = _normalizationTask;
        _normalizationCts = null;
        _normalizationTask = null;
        if (cts is null)
        {
            _normalizationReady = true;
            return;
        }

        // The analysis handles its own exceptions; cancellation is checked once per decoded frame.
        cts.Cancel();
        task?.Wait();
        cts.Dispose();
        _normalizationReady = true;
    }

    private static string? CreatePathNormalizationCacheKey(string filePath)
    {
        try
        {
            var info = new FileInfo(filePath);
            return info.Exists ? $"video|{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}" : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private void SetState(AudioEngineState state)
    {
        if (State == state)
            return;

        State = state;
        StateChanged?.Invoke(this, state);
    }

    private void CleanupTempPath()
    {
        if (string.IsNullOrWhiteSpace(_tempPath))
            return;

        TempMediaFile.TryDelete(_tempPath);
        _tempPath = null;
    }
}
