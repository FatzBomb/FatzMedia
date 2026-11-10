using System.Diagnostics;
using System.Globalization;
using FatzMedia.Video.Core.Abstractions;
using FatzMedia.Video.Core.Audio.Fade;
using FatzMedia.Video.Core.Files;
using FatzMedia.Video.Core.Media;
using FatzMedia.Video.Core.Network;
using FFmpeg.AutoGen;

namespace FatzMedia.Video.Core.Audio;

/// <summary>
/// Plays the audio of a video file, a soundtrack or network audio: FFmpeg demuxes and decodes it, and an
/// <see cref="IPushAudioOutput"/> plays the PCM (BASS by default, FatzAudio with the FatzAudio engine;
/// 037A slice 5). Seeks, gapless loops, the push throttle and the position bookkeeping live here, the
/// same for every output.
/// </summary>
sealed unsafe class VideoAudioEngine : IAudioEngine, IAudioLoopControl, IAudioFadeGainTarget, IAudioFilterHost, IAudioVolumeSlide, IBassOutputClient, IMidiEventReaderSource
{
    const double AudioBufferHighWatermarkSeconds = 4.0;
    const double AudioBufferLowWatermarkSeconds = 2.0;

    readonly IPushAudioOutput _output;
    // Lock order: _ioGate, then _sync. _ioGate owns the demuxer cursor (open, read, seek, close);
    // the packet reads and container seeks (network I/O for a network source: a segment download
    // can take seconds) run under it but never under _sync. _sync guards the decoder, the output
    // and the position bookkeeping, and is only ever held for CPU work, so Position,
    // Normalize and the volume/pitch/tempo setters a host calls from its UI thread never wait for
    // the network (ENG-035: the Avalonia player froze when a network seek held _sync).
    readonly Lock _ioGate = new();
    readonly Lock _sync = new();
    readonly SharedDemuxer _demuxer;
    readonly bool _ownsDemuxer;
    AVCodecContext* _codecContext;
    AVStream* _audioStream;
    SwrContext* _resampler;
    AVFrame* _frame;
    byte* _audioBuffer;
    int _audioBufferSize;
    int _sampleRate;
    int _sourceChannels;
    int _channels;
    double _streamStartSeconds;
    double? _discardAudioBeforeSeconds;
    double _positionBaseSeconds;
    /// <summary>Whether the output decodes the compressed packets itself (FatzAudio); FFmpeg then only demuxes.</summary>
    internal bool IsPacketMode { get; private set; }
    string? _tempPath;
    long _pushedAudioFrameCount;
    double _lastPushedAudioEndSeconds;
    long _positionSnapshotBits;
    long _positionSnapshotTimestamp;
    long _positionSnapshotDurationBits;
    int _positionSnapshotAvailable;
    // IsOpenCore as of the last open or close, published for lock-free IsOpen reads (Stage 022A
    // slice 2: the video presentation loop asked IsOpen every frame and contended with the
    // decode thread on _sync).
    volatile bool _openPublished;
    int _positionSnapshotPlaying;
    int _positionSnapshotTempoPercent;
    bool _audioPushThrottled;
    bool _disposed;

    // Gapless loop bookkeeping (see AudioLoopBoundaryTracker). _pendingLoopBoundaries and
    // _completedLoopSeconds are only ever touched from within _sync (GetPositionCore and
    // WrapDecoderForLoop are both always called under lock).
    readonly Queue<double> _pendingLoopBoundaries = new();
    double _completedLoopSeconds;

    /// <summary>
    /// When true, the decoder seeks back to 0 and keeps pushing into the same output stream
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
    public int PlayHandle => _output.PlayHandle;
    public int SourceHandle => _output.SourceHandle;

    /// <summary>The output's MIDI source (the BASS push stream, as before 037A slice 1); none for FatzAudio.</summary>
    IMidiEventReader? IMidiEventReaderSource.GetMidiEventReader() => _output.GetMidiEventReader();

    public bool Normalize
    {
        get
        {
            lock (_sync)
                return _output.Normalize;
        }
        set
        {
            lock (_sync)
                _output.Normalize = value;
        }
    }

    public double NormalizationGain => _output.NormalizationGain;
    public bool IsNormalizationReady => _output.IsNormalizationReady;
    public int CurrentTempo { get; private set; }
    public double Duration { get; private set; }
    public bool IsOpen => _openPublished && _demuxer.IsOpen;

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
            var elapsedSeconds = Stopwatch.GetElapsedTime(timestamp).TotalSeconds;
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
        : this(new BassPushAudioOutput(outputLatencyProvider, bassDeviceIndex), null)
    {
    }

    /// <param name="output">Where the audio plays; the engine owns and disposes it.</param>
    /// <param name="sharedDemuxer">The picture's cursor of a network video, or null for a cursor of its own.</param>
    internal VideoAudioEngine(IPushAudioOutput output, SharedDemuxer? sharedDemuxer)
    {
        _output = output ?? throw new ArgumentNullException(nameof(output));
        _output.NormalizationGainChanged += OnOutputNormalizationGainChanged;
        _demuxer = sharedDemuxer ?? new SharedDemuxer();
        _ownsDemuxer = sharedDemuxer == null;
    }

    double ApplyOutputLatency(double rawPositionSeconds) => _output.ToHeardPosition(rawPositionSeconds);

    bool IsOpenCore => _demuxer.IsOpen && AudioStreamIndex >= 0 && _output.IsCreated;

    // The output's position on the continuous timeline, never reset across a loop wrap (only
    // Seek/Stop restart it at _positionBaseSeconds). This is the timeline that loop boundaries are
    // recorded against - see AudioLoopBoundaryTracker.
    double GetContinuousPositionCore() =>
        _output.IsCreated ? _output.GetContinuousPosition(_positionBaseSeconds) : _positionBaseSeconds;

    double GetPositionCore()
    {
        var continuous = GetContinuousPositionCore();
        AdvanceCompletedLoops(continuous);
        return Math.Max(0, continuous - _completedLoopSeconds);
    }

    void AdvanceCompletedLoops(double continuousPositionSeconds)
    {
        var crossed = AudioLoopBoundaryTracker.Advance(continuousPositionSeconds, _pendingLoopBoundaries, ref _completedLoopSeconds);
        if (crossed > 0)
        {
            CompletedLoopCount += crossed;

            // A fade that ran to the end of a lap must not leave the next lap muted.
            _output.EndFadeSession();
        }
    }

    void PublishPositionSnapshot(double position)
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

    void ClearPositionSnapshot()
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

    /// <summary>
    /// Opens the audio of an http/https source (Stage 039A): one attempt (retries belong to the
    /// caller), no normalization pre-analysis (unity gain, as for 014A network audio). With a
    /// shared cursor that is already open (network video, slice 2) the audio takes its packets
    /// from the picture's read-ahead and opens no connection of its own.
    /// </summary>
    internal bool OpenNetwork(string url, FfmpegNetworkOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        ArgumentNullException.ThrowIfNull(options);
        return OpenCore(url, () => _demuxer.IsOpen || _demuxer.Open(url, options), normalizationCacheKey: null, analyzeNormalization: false, temporaryCopy: false);
    }

    /// <summary>Aborts blocking network I/O at once, without the engine lock (Stage 039A).</summary>
    internal void AbortNetwork()
    {
        if (_ownsDemuxer)
            _demuxer.Abort();
    }

    /// <summary>
    /// Gives up a network read or seek of this engine's cursor that started before
    /// <paramref name="timestamp"/>, without a lock (ENG-035: a newer seek supersedes it).
    /// </summary>
    internal void SupersedeNetworkOperationsStartedBefore(long timestamp)
    {
        if (_ownsDemuxer)
            _demuxer.SupersedeOperationsStartedBefore(timestamp);
    }

    /// <summary>How long this engine's network read has been blocked. Lock-free.</summary>
    internal TimeSpan NetworkReadStall => _ownsDemuxer ? _demuxer.NetworkReadStall : TimeSpan.Zero;

    /// <summary>Whether this engine's network reader reached the end of the stream. Lock-free.</summary>
    internal bool NetworkEndOfStream => _ownsDemuxer && _demuxer.NetworkEndOfStream;

    /// <summary>
    /// Progress of this engine's network read-ahead towards ending a buffering spell, <c>0..1</c>
    /// (slice 2); 1 with a shared cursor, whose owner reports it. Never waits for network I/O.
    /// </summary>
    internal double NetworkReadAheadFill => _ownsDemuxer ? _demuxer.ReadAheadResumeFill : 1;

    /// <summary>The FFmpeg error of the last failed network open, or null.</summary>
    internal string? LastNetworkError => _demuxer.LastNetworkError;

    /// <summary>What FFmpeg reported for the open network source.</summary>
    internal NetworkVideoProbe GetNetworkProbe() => _demuxer.GetNetworkProbe();

    /// <summary>
    /// Takes the in-stream title (ICY, Ogg comments) that playback has reached on this engine's own
    /// network cursor (ENG-034). Takes <c>_sync</c> for the position only; never waits for network I/O.
    /// </summary>
    internal bool TryTakeStreamMetadata(out StreamMetadata metadata)
    {
        double playedUpTo;
        lock (_sync)
        {
            if (!_ownsDemuxer || !IsOpenCore)
            {
                metadata = default;
                return false;
            }

            // Packet times are on the container timeline; the position starts at the stream's start.
            playedUpTo = ApplyOutputLatency(GetPositionCore()) + _streamStartSeconds;
        }

        return _demuxer.TryTakeStreamMetadata(playedUpTo, out metadata);
    }

    bool OpenFile(string filePath, string? normalizationCacheKey, bool temporaryCopy = false) =>
        OpenCore(filePath, () => _demuxer.IsOpen || _demuxer.Open(filePath), normalizationCacheKey, analyzeNormalization: true, temporaryCopy);

    bool OpenCore(string filePath, Func<bool> openDemuxer, string? normalizationCacheKey, bool analyzeNormalization, bool temporaryCopy)
    {
        lock (_ioGate)
        {
            lock (_sync)
            {
                if (_disposed)
                    return false;

                CloseCore();
            }

            // A network connect and probe can take seconds: outside _sync.
            if (!openDemuxer())
                return false;

            lock (_sync)
                return CompleteOpenCore(filePath, normalizationCacheKey, analyzeNormalization, temporaryCopy);
        }
    }

    // Under _ioGate and _sync, with the demuxer open.
    bool CompleteOpenCore(string filePath, string? normalizationCacheKey, bool analyzeNormalization, bool temporaryCopy)
    {
        // A network source takes the audio of the variant the picture plays (039A).
        AudioStreamIndex = _demuxer.SelectAudioStream();
        if (AudioStreamIndex < 0)
        {
            CloseCore();
            return false;
        }

        _demuxer.RegisterAudioStream(AudioStreamIndex);

        // With its own demuxer (the video pipeline gives audio a separate cursor), every
        // other stream is read and dropped: discard them so the file is not demuxed twice
        // (and, for HLS/DASH, so the other variants are not downloaded).
        if (_ownsDemuxer)
        {
            _demuxer.DiscardAllStreamsExcept([AudioStreamIndex]);
        }
        else
        {
            _demuxer.DiscardUnregisteredNetworkStreams();
        }

        _audioStream = _demuxer.GetStream(AudioStreamIndex);
        _streamStartSeconds = GetStreamStartSeconds(_audioStream);
        // A local file (analyzed) names its source so the output can recognize the song. An output that decodes
        // the codec itself takes the packets; otherwise FFmpeg decodes.
        var sourcePath = analyzeNormalization ? filePath : null;
        IsPacketMode = TryCreatePacketOutput(sourcePath);
        if (!IsPacketMode && (!InitializeDecoder() || !_output.Create(_sampleRate, _channels, sourcePath)))
        {
            CloseCore();
            return false;
        }

        var formatDurationTicks = _demuxer.FormatDurationTicks;
        Duration = formatDurationTicks > 0 ? formatDurationTicks / (double)ffmpeg.AV_TIME_BASE : 0;
        State = AudioEngineState.Stopped;
        PublishPositionSnapshot(_positionBaseSeconds);
        if (analyzeNormalization)
            _output.StartNormalization(filePath, normalizationCacheKey, Duration, temporaryCopy);
        _openPublished = IsOpenCore;
        Opened?.Invoke(this, EventArgs.Empty);
        Debug.WriteLine($"[VideoAudioEngine] Opened audio stream: {_sampleRate}Hz, {_sourceChannels}ch -> {_channels}ch, duration {Duration:F2}s");
        return true;
    }

    public bool Open(IArchiveEntryInfo archiveEntry)
    {
        ArgumentNullException.ThrowIfNull(archiveEntry);

        // Streamed straight to the scratch file: no in-memory copies of the whole entry.
        var tempPath = TempMediaFile.CreateFromArchiveEntry(archiveEntry);
        var normalizationCacheKey = $"archive|{archiveEntry.Key}|{archiveEntry.Size?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}";
        var success = OpenFile(tempPath, normalizationCacheKey, temporaryCopy: true);
        if (success)
            lock (_sync)
                _tempPath = tempPath;
        else
            TempMediaFile.TryDelete(tempPath);

        return success;
    }

    public bool DecodeAndPushAudio()
    {
        lock (_ioGate)
            return DecodeAndPushAudioCore();
    }

    // Under _ioGate: the packet read runs outside _sync.
    bool DecodeAndPushAudioCore()
    {
        lock (_sync)
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
        }

        var packet = ffmpeg.av_packet_alloc();
        if (packet == null)
            return false;

        try
        {
            var result = _demuxer.ReadAudioPacket(packet);
            lock (_sync)
            {
                if (result >= 0)
                {
                    var processed = ProcessAudioPacket(packet);
                    if (processed)
                        PublishPositionSnapshot(GetPositionCore());
                    return processed;
                }

                // A newer seek gave up the read (network); the seek repositions the cursor next.
                if (result == ffmpeg.AVERROR_EXIT || !LoopPlayback || Duration <= 0)
                    return false;
            }

            // End of stream. Gapless loop: seek the FFmpeg decoder back to 0 and keep pushing
            // PCM into the same output stream instead of stopping it - the stream never
            // stops, so there is no audio gap at the loop boundary. Mirrors
            // BackgroundVideoLoop's decoder-only pre-seek pattern.
            return WrapDecoderForLoop();
        }
        finally
        {
            ffmpeg.av_packet_unref(packet);
            ffmpeg.av_packet_free(&packet);
        }
    }

    // Seeks the FFmpeg decoder back to 0, then records the continuous-timeline mark at which this
    // lap's pushed audio ends. The output stream and its position are untouched, so
    // playback continues without interruption; PushAudioFrame's existing monotonic guard (see
    // below) keeps _lastPushedAudioEndSeconds increasing across the wrap automatically. Under
    // _ioGate (nothing is pushed meanwhile); the mark is recorded only once the seek succeeded,
    // so AdvanceCompletedLoops never sees a boundary that will never be reached.
    bool WrapDecoderForLoop()
    {
        if (!SeekDecoder(0))
            return false;

        lock (_sync)
            _pendingLoopBoundaries.Enqueue(_lastPushedAudioEndSeconds);
        return true;
    }

    public bool Play()
    {
        lock (_sync)
            return PlayCore();
    }

    bool PlayCore()
    {
        if (!_output.IsCreated)
            return false;

        var success = _output.Play();
        if (success)
        {
            SetState(AudioEngineState.Playing);
            PublishPositionSnapshot(GetPositionCore());
        }

        return success;
    }

    int IBassOutputClient.PlayingHandle => State == AudioEngineState.Playing ? PlayHandle : 0;

    /// <summary>
    /// True while BASS has parked the channel because its output device was lost (ENG-032); always
    /// false for an output that is not BASS (FatzAudio follows the device itself).
    /// </summary>
    internal bool IsChannelPausedByDevice() => _output is BassPushAudioOutput bass && bass.IsChannelPausedByDevice();

    bool IBassOutputClient.ResumeChannelPausedByDevice()
    {
        lock (_sync)
        {
            if (_disposed || State != AudioEngineState.Playing || _output is not BassPushAudioOutput bass
                || !bass.IsChannelPausedByDevice())
            {
                return true;
            }

            // Not restarted: the channel carries on from its position, and the snapshot the video
            // clock reads restarts from the real position instead of the stalled one.
            var resumed = bass.ResumeIfPausedByDevice();
            PublishPositionSnapshot(GetPositionCore());
            return resumed;
        }
    }

    public bool Pause()
    {
        lock (_sync)
            return PauseCore();
    }

    bool PauseCore()
    {
        if (!_output.IsCreated)
            return false;

        var success = _output.Pause();
        if (success)
        {
            SetState(AudioEngineState.Paused);
            PublishPositionSnapshot(GetPositionCore());
        }

        return success;
    }

    public bool Stop()
    {
        lock (_ioGate)
        {
            lock (_sync)
            {
                _positionBaseSeconds = 0;
                _output.Restart(0, stop: true);
                ResetAudioBufferTracking();
            }

            // Network I/O: outside _sync. _ioGate keeps the push loop out meanwhile.
            _ = SeekDecoder(0);
            lock (_sync)
            {
                _output.EndFadeSession();
                SetState(AudioEngineState.Stopped);
                PublishPositionSnapshot(0);
                PositionChanged?.Invoke(this, 0);
            }

            return true;
        }
    }

    public bool Seek(double seconds) => Seek(seconds, resumePlayback: true);

    public bool Seek(double seconds, bool resumePlayback)
    {
        lock (_ioGate)
        {
            double target;
            lock (_sync)
            {
                if (!IsOpenCore)
                    return false;

                target = Duration > 0 ? Math.Clamp(seconds, 0, Duration) : Math.Max(0, seconds);
            }

            // The decoder seeks first: if it fails, playback carries on untouched instead of being
            // left paused with its buffered audio flushed. _ioGate keeps the push loop out
            // meanwhile; the seek (network I/O) runs outside _sync.
            if (!SeekDecoder(target))
                return false;

            lock (_sync)
                return CompleteSeekCore(target, resumePlayback);
        }
    }

    // Under _ioGate and _sync, once the decoder sought to target.
    bool CompleteSeekCore(double target, bool resumePlayback)
    {
        var wasPlaying = State == AudioEngineState.Playing;
        _output.Restart(target, stop: false);
        _positionBaseSeconds = target;
        ResetAudioBufferTracking();
        if (resumePlayback && wasPlaying && _output.IsCreated)
        {
            _ = _output.Resume();
            SetState(AudioEngineState.Playing);
        }

        var correctedTarget = ApplyOutputLatency(target);
        PublishPositionSnapshot(correctedTarget);

        PositionChanged?.Invoke(this, correctedTarget);
        return true;
    }

    public bool PrebufferAudio(int minimumFrames = 8, int maxDecodeAttempts = 128)
    {
        // _pushedAudioFrameCount only changes under _ioGate, so it is read here without _sync.
        lock (_ioGate)
        {
            lock (_sync)
            {
                if (!IsOpenCore || minimumFrames <= 0 || maxDecodeAttempts <= 0)
                    return false;
            }

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
        lock (_ioGate)
        {
            lock (_sync)
                CloseCore();
        }
    }

    void CloseCore()
    {
        if (_disposed)
            return;

        _openPublished = false;

        // First: the output joins its normalization work, which may read the temp file deleted below.
        _output.Release();

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
        else
            _demuxer.UnregisterAudioStream(); // the shared read-ahead stops keeping its packets

        _audioStream = null;
        AudioStreamIndex = -1;
        IsPacketMode = false;
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
            _output.SetVolume(volume01);
    }

    void IAudioVolumeSlide.SlideVolume(double volume01, TimeSpan duration)
    {
        lock (_sync)
            _output.SlideVolume(volume01, duration);
    }

    public void SetPitch(int semitones)
    {
        lock (_sync)
            _output.SetPitch(semitones);
    }

    public void SetTempo(int percent)
    {
        lock (_sync)
        {
            CurrentTempo = Math.Clamp(percent, -95, 5000);
            _output.SetTempo(CurrentTempo);
            if (IsOpenCore)
                PublishPositionSnapshot(GetPositionCore());
        }
    }

    void OnOutputNormalizationGainChanged(object? sender, double gain) => NormalizationGainChanged?.Invoke(this, gain);

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

    AudioEqualizerSettings IAudioFilterHost.Equalizer => _output.Equalizer;

    bool IAudioFilterHost.SetEqualizer(AudioEqualizerSettings settings) => _output.SetEqualizer(settings);

    int IAudioFadeGainTarget.BeginFadeSession() => _output.BeginFadeSession();

    bool IAudioFadeGainTarget.SetFadeGain(int session, double gain, TimeSpan ramp) => _output.SetFadeGain(session, gain, ramp);

    /// <summary>
    /// Restores full fade gain when playback reaches its natural end, so a fade-out that ran to
    /// the end does not leave a replay muted (the plain-audio engine does the same).
    /// </summary>
    internal void EndFadeSessionAtEnd() => _output.EndFadeSession();

    public void Dispose()
    {
        BassOutputWatchdog.Unregister(this);
        Close();
        if (_ownsDemuxer)
            _demuxer.Dispose();
        lock (_sync)
            _disposed = true;

        // Outside _sync: disposing may wait for the output's background work (FatzAudio loudness scans).
        _output.NormalizationGainChanged -= OnOutputNormalizationGainChanged;
        _output.Dispose();

        GC.SuppressFinalize(this);
    }

    bool InitializeDecoder()
    {
        if (!FfmpegAudioDecoderSetup.TryOpen(_audioStream, out _codecContext, out _resampler, out _sampleRate, out _channels))
            return false;

        _sourceChannels = Math.Max(1, _codecContext->ch_layout.nb_channels);

        _frame = ffmpeg.av_frame_alloc();
        if (_frame == null)
            return false;

        _audioBufferSize = _sampleRate * _channels * sizeof(float);
        _audioBuffer = (byte*)ffmpeg.av_malloc((ulong)_audioBufferSize);
        return _audioBuffer != null;
    }

    // Packet mode: the output decodes. Under _sync.
    bool TryCreatePacketOutput(string? sourcePath)
    {
        var parameters = _audioStream->codecpar;
        var channels = parameters->ch_layout.nb_channels;
        if (parameters->sample_rate <= 0 || channels <= 0)
            return false;

        var codec = new PacketCodecInfo(
            ffmpeg.avcodec_get_name(parameters->codec_id),
            parameters->sample_rate,
            channels,
            parameters->extradata_size > 0 ? new ReadOnlySpan<byte>(parameters->extradata, parameters->extradata_size).ToArray() : null,
            parameters->block_align,
            parameters->bits_per_coded_sample,
            parameters->bit_rate);
        if (!_output.CreateForPackets(codec, sourcePath))
            return false;

        _sampleRate = codec.SampleRate;
        _sourceChannels = _channels = codec.Channels;
        return true;
    }

    /// <summary>
    /// Queues a compressed packet for an output that decodes it, on the continuous timeline as decoded frames are.
    /// After a seek, packets that end before the target are dropped; the one that spans it plays from its start
    /// (at most one packet, 20-40 ms, early), and the clock reports where it starts.
    /// </summary>
    bool PushCompressedPacket(AVPacket* packet)
    {
        var timeBase = ffmpeg.av_q2d(_audioStream->time_base);
        var timestamp = packet->pts != ffmpeg.AV_NOPTS_VALUE ? packet->pts : packet->dts;
        var start = timestamp == ffmpeg.AV_NOPTS_VALUE ? _lastPushedAudioEndSeconds : Math.Max(0, (timestamp * timeBase) - _streamStartSeconds);
        var duration = packet->duration > 0 ? packet->duration * timeBase : 0;
        if (_discardAudioBeforeSeconds is { } target)
        {
            if (duration > 0 && start + duration <= target)
                return false;
            _discardAudioBeforeSeconds = null;
        }

        var end = start + duration;
        if (!double.IsFinite(end) || end < _lastPushedAudioEndSeconds)
            end = _lastPushedAudioEndSeconds + duration;

        if (!_output.PushPacket(new ReadOnlySpan<byte>(packet->data, packet->size), end - duration, duration))
            return false;

        _pushedAudioFrameCount++;
        _lastPushedAudioEndSeconds = end;
        return true;
    }

    bool ProcessAudioPacket(AVPacket* packet)
    {
        if (IsPacketMode)
            return PushCompressedPacket(packet);

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

    bool IsAudioBufferThrottledCore()
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
    double GetBufferedAudioSecondsCore() =>
        _lastPushedAudioEndSeconds <= 0
            ? 0
            : Math.Max(0, _lastPushedAudioEndSeconds - GetContinuousPositionCore());

    void ResetAudioBufferTracking()
    {
        _lastPushedAudioEndSeconds = _positionBaseSeconds;
        _audioPushThrottled = false;

        // A manual seek/stop breaks the natural continuous-playthrough assumption that
        // pending loop boundaries were recorded against - discard them.
        _pendingLoopBoundaries.Clear();
        _completedLoopSeconds = 0;
    }

    void PushAudioFrame()
    {
        var framePosition = GetFramePositionSeconds(_frame);
        var discardBefore = _discardAudioBeforeSeconds;
        // After a seek, frames wholly before the target are dropped; the resampler keeps the rate,
        // so its output samples line up with the frame's samples.
        if (discardBefore is { } target
            && AudioSeekTrim.SamplesToSkip(framePosition, target, _sampleRate, _frame->nb_samples) >= _frame->nb_samples)
            return;

        var output = _audioBuffer;
        var samples = ffmpeg.swr_convert(
            _resampler,
            &output,
            _audioBufferSize / (_channels * sizeof(float)),
            _frame->extended_data,
            _frame->nb_samples);

        if (samples <= 0)
            return;

        // The frame that spans the seek target starts at the target, where the clock says playback is.
        var skip = 0;
        if (discardBefore is { } seekTarget)
        {
            _discardAudioBeforeSeconds = null;
            skip = AudioSeekTrim.SamplesToSkip(framePosition, seekTarget, _sampleRate, samples);
            if (skip >= samples)
                return;

            framePosition += skip / (double)_sampleRate;
        }

        samples -= skip;
        var frameDuration = _sampleRate > 0 ? samples / (double)_sampleRate : 0;
        // The continuous timeline grows on across a loop wrap: a frame that starts before the
        // previous one ended (the next lap) continues after it.
        var frameEndPosition = framePosition + frameDuration;
        if (!double.IsFinite(frameEndPosition) || frameEndPosition < _lastPushedAudioEndSeconds)
            frameEndPosition = _lastPushedAudioEndSeconds + frameDuration;

        var pcm = new ReadOnlySpan<byte>(output + (skip * _channels * sizeof(float)), samples * _channels * sizeof(float));
        if (!_output.Push(pcm, frameEndPosition - frameDuration))
            return;

        _pushedAudioFrameCount++;
        if (_sampleRate > 0)
            _lastPushedAudioEndSeconds = frameEndPosition;
    }

    // Under _ioGate and not under _sync: the container seek is network I/O for a network source.
    // The stream fields it reads change only under _ioGate (open, close).
    bool SeekDecoder(double seconds)
    {
        if (!_demuxer.IsOpen || _audioStream == null)
            return false;

        // Shared-demuxer mode remains available for internal callers that coordinate the
        // container seek externally. Main-pipeline audio owns its cursor and uses the seek below.
        if (_ownsDemuxer)
        {
            var timestamp = (long)((seconds + _streamStartSeconds) / ffmpeg.av_q2d(_audioStream->time_base));
            if (_demuxer.Seek(AudioStreamIndex, timestamp, ffmpeg.AVSEEK_FLAG_BACKWARD) < 0)
                return false;
        }
        else
        {
            _demuxer.ClearAudioEof();
        }

        lock (_sync)
        {
            if (_codecContext != null)
                ffmpeg.avcodec_flush_buffers(_codecContext);   // none in packet mode: the output decodes

            _discardAudioBeforeSeconds = seconds;
        }

        return true;
    }

    double GetFramePositionSeconds(AVFrame* frame)
    {
        if (_audioStream == null || frame == null)
            return 0;

        var timeBase = ffmpeg.av_q2d(_audioStream->time_base);
        var timestamp = frame->best_effort_timestamp;
        if (timestamp == ffmpeg.AV_NOPTS_VALUE)
            timestamp = frame->pts;

        return timestamp == ffmpeg.AV_NOPTS_VALUE ? _positionBaseSeconds : Math.Max(0, (timestamp * timeBase) - _streamStartSeconds);
    }

    static double GetStreamStartSeconds(AVStream* stream) =>
        stream == null || stream->start_time == ffmpeg.AV_NOPTS_VALUE
            ? 0
            : Math.Max(0, stream->start_time * ffmpeg.av_q2d(stream->time_base));

    static string? CreatePathNormalizationCacheKey(string filePath)
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

    void SetState(AudioEngineState state)
    {
        if (State == state)
            return;

        State = state;

        // The output watchdog looks after the channel only while it should be playing.
        if (state == AudioEngineState.Playing)
            BassOutputWatchdog.Register(this);
        else
            BassOutputWatchdog.Unregister(this);

        StateChanged?.Invoke(this, state);
    }

    void CleanupTempPath()
    {
        if (string.IsNullOrWhiteSpace(_tempPath))
            return;

        TempMediaFile.TryDelete(_tempPath);
        _tempPath = null;
    }
}
