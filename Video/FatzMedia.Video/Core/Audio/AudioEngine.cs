using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using FatzMedia.Video.Core.Abstractions;
using FatzMedia.Video.Core.Audio.Fade;
using FatzMedia.Video.Core.Audio.Filters;
using FatzMedia.Video.Core.Network;
using FatzMedia.Video.Core.Threading;
using ManagedBass;
using MBass = ManagedBass.Bass;

namespace FatzMedia.Video.Core.Audio;

/// <summary>
/// High-performance BASS-based audio engine with tempo/pitch control and normalization.
/// Optimized for real-time audio playback in karaoke and DJ applications.
/// Features adaptive sample rate initialization for optimal audio quality.
/// </summary>
public sealed class AudioEngine : IAudioEngine, IAudioOutputDeviceProvider, IAudioLoopControl, IAudioAnalysisOpenControl, IAudioTrimWindowProvider, IMediaFileAnalyzer, IAudioFadeGainTarget, INetworkAudioSource, IAudioFilterHost, IAudioVolumeSlide, IBassOutputClient, IMidiEventReaderSource
{
    readonly BassEqualizerFilter _equalizer = new();

    const double CachedDurationToleranceSeconds = 0.25;

    #region Private Fields

    // Composition: Delegate specialized responsibilities to focused components
    readonly AudioNormalizer _normalizer;
    // The player this engine was built for, for diagnostics raised on BASS's own threads.
    readonly object? _diagnosticOwner;
    readonly MediaAnalysisCache _analysisCache;
    readonly AudioStreamManager _streamManager;
    readonly IOutputLatencyProvider _outputLatencyProvider;
    readonly SilenceTrimOptions _silenceTrim;

    // Adaptive sample rate management
    bool _bassInitialized;
    int _currentDeviceSampleRate = 44100; // Default fallback
    int _requestedDevice = AudioOutputDeviceInfo.PrimaryDeviceIndex;
    int _currentDevice = AudioOutputDeviceInfo.PrimaryDeviceIndex;
    CancellationTokenSource? _normalizationAnalysisCts;
    Task? _normalizationAnalysisTask;
    readonly Lock _normalizationAnalysisLock = new();
    int _normalizationAnalysisGeneration;
    volatile bool _normalizationReady = true;
    readonly SyncProcedure _playbackEndedSyncProcedure;
    int _playbackEndSyncHandle;
    int _playbackEndSyncChannel;
    bool _loopPlayback;
    MediaAnalysisResult? _activeTrimWindow;
    readonly CancellationTokenSource _standaloneAnalysisLifetime = new();
    int _standaloneAnalysisCount;
    readonly BassFadeGainStage _fade = new("AudioEngine");
    readonly NetworkRetryPolicy _networkRetry;
    readonly Lock _networkGate = new();
    readonly SyncProcedure _networkMetadataSyncProcedure;
    Timer? _networkMonitor;
    BufferingStateTracker? _bufferingTracker;
    MediaSourceCapabilities? _networkCapabilities;
    int _networkGeneration;
    static NormalizationGainCache NormalizationGainCache => NormalizationGainCache.Shared;

    volatile bool _disposed;

    // Serializes Play/Pause/Stop with the output watchdog's resume, so a channel the host just
    // paused or stopped is never resumed (ENG-032).
    readonly Lock _transportGate = new();

    #endregion

    #region Constructors

    /// <summary>
    /// Creates a new AudioEngine with default components.
    /// </summary>
    public AudioEngine() : this(new AudioNormalizer(), new AudioStreamManager(), new BassOutputLatencyProvider())
    {
    }

    /// <summary>
    /// Creates a new AudioEngine with custom components (for testing/advanced scenarios).
    /// </summary>
    /// <param name="normalizer">Custom normalizer implementation</param>
    /// <param name="streamManager">Custom stream manager implementation</param>
    internal AudioEngine(AudioNormalizer normalizer, AudioStreamManager streamManager)
        : this(normalizer, streamManager, new BassOutputLatencyProvider())
    {
    }

    /// <summary>
    /// Creates a new AudioEngine with custom components and an output-latency provider.
    /// </summary>
    /// <param name="normalizer">Custom normalizer implementation</param>
    /// <param name="streamManager">Custom stream manager implementation</param>
    /// <param name="outputLatencyProvider">Provider for BASS-reported output latency</param>
    /// <param name="silenceTrim">Which leading and trailing silence to skip</param>
    /// <param name="analysisCache">Cache of analysis results per file; the shared cache when null</param>
    /// <param name="networkRetry">Retry policy for network sources; the default policy when null</param>
    internal AudioEngine(
        AudioNormalizer normalizer,
        AudioStreamManager streamManager,
        IOutputLatencyProvider outputLatencyProvider,
        SilenceTrimOptions silenceTrim = default,
        MediaAnalysisCache? analysisCache = null,
        NetworkRetryPolicy? networkRetry = null)
    {
        _networkRetry = networkRetry ?? NetworkRetryPolicy.Default;
        _networkMetadataSyncProcedure = OnNetworkMetadataSync;
        _normalizer = normalizer ?? throw new ArgumentNullException(nameof(normalizer));
        _analysisCache = analysisCache ?? MediaAnalysisCache.Shared;
        _streamManager = streamManager ?? throw new ArgumentNullException(nameof(streamManager));
        _outputLatencyProvider = outputLatencyProvider ?? throw new ArgumentNullException(nameof(outputLatencyProvider));
        _silenceTrim = silenceTrim;
        _playbackEndedSyncProcedure = OnPlaybackEndedSync;
        _diagnosticOwner = DiagnosticRouter.Owner;

        // Wire up normalizer events to forward to our event
        _normalizer.NormalizationGainChanged += (_, gain) =>
        {
            Debug.WriteLine($"[AudioEngine] Forwarding normalization gain change: {gain:F3}");
            NormalizationGainChanged?.Invoke(this, gain);
        };
    }

    double ApplyOutputLatency(double rawPositionSeconds)
    {
        if (rawPositionSeconds <= 0 || !double.IsFinite(rawPositionSeconds))
            return rawPositionSeconds;

        // BASS reports latency for the current device, so position sampling selects this engine's device first.
        _ = BassAudioOutputDevices.TrySetCurrentDevice(_currentDevice, "AudioEngine", raiseDiagnostic: false);

        var latency = _outputLatencyProvider.GetOutputLatencySeconds();
        if (latency <= 0 || !double.IsFinite(latency))
            return rawPositionSeconds;

        var corrected = rawPositionSeconds - latency;
        return corrected < 0 ? 0 : corrected;
    }

    #endregion

    #region Public Properties

    /// <inheritdoc/>
    public double NormalizationGain => _normalizer.NormalizationGain;

    /// <inheritdoc/>
    public bool IsNormalizationReady => _normalizationReady;

    /// <inheritdoc/>
    public event EventHandler<double>? NormalizationGainChanged;

    /// <inheritdoc/>
    public bool Normalize
    {
        get;
        set
        {
            if (field == value) return;
            field = value;

            // Apply or remove normalization immediately based on the new setting
            _normalizer.SetNormalizeEnabled(value);
            Debug.WriteLine($"[AudioEngine] Normalization {(value ? "enabled" : "disabled")}");
        }
    } = true;

    /// <inheritdoc/>
    public int CurrentTempo { get; private set; }

    /// <remarks>
    /// Network sources never loop: a live stream cannot restart, and the native loop flag is not
    /// applied to URL streams, so a requested loop reads as off and the end is a real stop.
    /// </remarks>
    bool IAudioLoopControl.LoopPlayback
    {
        get => _loopPlayback && !_streamManager.IsNetworkSource;
        set
        {
            _loopPlayback = value;
            _streamManager.SetLoopPlayback(value && Volatile.Read(ref _activeTrimWindow) is null);
        }
    }

    MediaAnalysisResult? IAudioTrimWindowProvider.ActiveTrimWindow => Volatile.Read(ref _activeTrimWindow);

    // Event declarations for state and lifecycle
    public event EventHandler<AudioEngineState>? StateChanged;
    public event EventHandler? Opened;
    public event EventHandler? Closed;
    public event EventHandler<double>? PositionChanged;

    public event EventHandler? PlaybackEnded;

    public AudioEngineState State
    {
        get; private set
        {
            if (field != value)
            {
                field = value;

                // The output watchdog looks after the channel only while it should be playing.
                if (value == AudioEngineState.Playing)
                    BassOutputWatchdog.Register(this);
                else
                    BassOutputWatchdog.Unregister(this);

                StateChanged?.Invoke(this, field);
            }
        }
    } = AudioEngineState.Stopped;

    public double Position
    {
        get => ApplyOutputLatency(_streamManager.GetCurrentPosition());
        set
        {
            var old = _streamManager.GetCurrentPosition();
            _streamManager.SetCurrentPosition(value);
            var updated = _streamManager.GetCurrentPosition();
            var corrected = ApplyOutputLatency(updated);
            if (Math.Abs(corrected - ApplyOutputLatency(old)) > 0.001)
            {
                PositionChanged?.Invoke(this, corrected);
            }
        }
    }

    /// <inheritdoc/>
    public int PlayHandle => _streamManager.PlayHandle;

    public int SourceHandle => _streamManager.DecodeHandle;

    /// <summary>MIDI lyrics and metadata read the source channel through BASS, as before 037A slice 1.</summary>
    IMidiEventReader? IMidiEventReaderSource.GetMidiEventReader() => BassMidiEventReader.ForHandle(SourceHandle);

    /// <inheritdoc/>
    public double Duration => _streamManager.DurationSeconds;

    public int BassDeviceIndex => _bassInitialized ? _currentDevice : _requestedDevice;

    #endregion

    #region Public Methods

    /// <inheritdoc/>
    public bool Init(int device = -1, int sampleRate = 44100)
    {
        _requestedDevice = device;

        try
        {
            // Check if already initialized
            if (_bassInitialized)
            {
                var selected = BassAudioOutputDevices.TrySetCurrentDevice(_currentDevice, "AudioEngine");
                DiagnosticRouter.Raise(MediaDiagnosticLevel.Info, "AudioEngine", selected
                    ? $"BASS already initialized on this engine: device={_currentDevice}"
                    : $"BASS already initialized but device {_currentDevice} could not be selected");
                return selected;
            }

            // Initialize BASS for the first time
            var success = MBass.Init(device, sampleRate);
            if (!success && MBass.LastError == Errors.Already)
                success = device < 0 || BassAudioOutputDevices.TrySetCurrentDevice(device, "AudioEngine");

            if (success)
            {
                _bassInitialized = true;
                _currentDevice = BassAudioOutputDevices.ResolveInitializedDeviceIndex(device);
                _ = BassAudioOutputDevices.TrySetCurrentDevice(_currentDevice, "AudioEngine");
                _currentDeviceSampleRate = MBass.GetInfo(out var info) && info.SampleRate > 0
                    ? info.SampleRate
                    : sampleRate;

                DiagnosticRouter.Raise(MediaDiagnosticLevel.Info, "AudioEngine", $"Initialized BASS globally: requestedDevice={device}, activeDevice={_currentDevice}, sampleRate={_currentDeviceSampleRate} Hz");
                EnsureDevNonStop();
            }
            else
            {
                DiagnosticRouter.Raise(MediaDiagnosticLevel.Error, "AudioEngine", $"Failed to initialize BASS: {MBass.LastError}");
            }
            return success;
        }
        catch (Exception ex)
        {
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Error, "AudioEngine", $"Init failed: {ex.Message}", innerException: ex);
            return false;
        }
    }

    /// <inheritdoc/>
    public bool Open(string filePath) => Open(filePath, analyzeSilence: true);

    bool IAudioAnalysisOpenControl.Open(string filePath, bool analyzeSilence) =>
        Open(filePath, analyzeSilence);

    bool Open(string filePath, bool analyzeSilence)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        // A disposed engine would create BASS streams nothing frees.
        if (_disposed)
            return false;

        try
        {
            CancelNormalizationAnalysis();
            RemovePlaybackEndSync();
            ResetActiveTrimWindow();

            // Auto-initialize BASS with optimal sample rate if not already done
            if (!_bassInitialized)
            {
                // Use session-optimized rate based on first file
                var sessionOptimalRate = GetSessionOptimalRate(filePath);
                if (!Init(_requestedDevice, sessionOptimalRate))
                {
                    DiagnosticRouter.Raise(MediaDiagnosticLevel.Error, "AudioEngine", "Failed to initialize BASS with session-optimal sample rate");
                    return false;
                }
                _bassInitialized = true;
                _currentDeviceSampleRate = sessionOptimalRate;
            }

            if (!BassAudioOutputDevices.TrySetCurrentDevice(_currentDevice, "AudioEngine"))
                return false;

            _fade.EndSession(releaseHandle: true);
            StopNetworkMonitoring();
            _equalizer.Forget();
            var success = _streamManager.OpenFromPath(filePath);
            if (success)
            {
                _normalizer.ResetGain(clearFxHandle: true);
                CurrentTempo = 0; // Reset tempo when opening new file

                var playHandle = _streamManager.PlayHandle;
                if (playHandle != 0)
                {
                    _ = _normalizer.SetupGainFx(playHandle);
                    _fade.Attach(playHandle);
                    _ = _equalizer.Apply(playHandle);
                    DiagnosticRouter.Raise(MediaDiagnosticLevel.Info, "AudioEngine", $"Gain FX setup on handle {playHandle}");
                }

                LogAudioInfo(filePath);

                StartNormalizationAnalysis(
                    CreatePathNormalizationCacheKey(filePath),
                    "file",
                    CreateMediaAnalysisCacheKey(filePath, analyzeSilence));
            }
            if (success)
            {
                AttachPlaybackEndSync();
                Opened?.Invoke(this, EventArgs.Empty);
                State = AudioEngineState.Stopped;
            }
            return success;
        }
        catch (Exception ex)
        {
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Error, "AudioEngine", $"Open failed for '{filePath}': {ex.Message}", sourcePath: filePath, innerException: ex);
            return false;
        }
    }

    /// <inheritdoc/>
    public bool Open(IArchiveEntryInfo archiveEntry)
    {
        ArgumentNullException.ThrowIfNull(archiveEntry);

        if (_disposed)
            return false;

        try
        {
            CancelNormalizationAnalysis();
            RemovePlaybackEndSync();
            ResetActiveTrimWindow();

            // Auto-initialize BASS if not already done (use default for archive entries)
            if (!_bassInitialized)
            {
                if (!Init(_requestedDevice))
                {
                    DiagnosticRouter.Raise(MediaDiagnosticLevel.Error, "AudioEngine", "Failed to initialize BASS for archive entry");
                    return false;
                }
                _bassInitialized = true;
                _currentDeviceSampleRate = 44100;
            }

            if (!BassAudioOutputDevices.TrySetCurrentDevice(_currentDevice, "AudioEngine"))
                return false;

            _fade.EndSession(releaseHandle: true);
            StopNetworkMonitoring();
            _equalizer.Forget();
            var success = _streamManager.OpenFromArchive(archiveEntry);
            if (success)
            {
                _normalizer.ResetGain(clearFxHandle: true);
                CurrentTempo = 0; // Reset tempo when opening new file

                // Set up gain FX on the play handle
                var playHandle = _streamManager.PlayHandle;
                if (playHandle != 0)
                {
                    _ = _normalizer.SetupGainFx(playHandle);
                    _fade.Attach(playHandle);
                    _ = _equalizer.Apply(playHandle);
                    DiagnosticRouter.Raise(MediaDiagnosticLevel.Info, "AudioEngine", $"Gain FX setup on handle {playHandle} (archive)");
                }

                LogAudioInfo($"Archive: {archiveEntry.Key}");

                StartNormalizationAnalysis(CreateArchiveNormalizationCacheKey(archiveEntry), "archive");
            }
            if (success)
            {
                AttachPlaybackEndSync();
                Opened?.Invoke(this, EventArgs.Empty);
                State = AudioEngineState.Stopped;
            }
            return success;
        }
        catch (Exception ex)
        {
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Error, "AudioEngine", $"Open failed for archive entry '{archiveEntry.Key}': {ex.Message}", sourcePath: archiveEntry.Key, innerException: ex);
            return false;
        }
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Play()
    {
        lock (_transportGate)
            return PlayCore();
    }

    bool PlayCore()
    {
        var playHandle = _streamManager.PlayHandle;
        if (playHandle == 0) return false;

        var trimWindow = Volatile.Read(ref _activeTrimWindow);
        if (trimWindow is not null)
        {
            if (trimWindow.EffectiveDuration <= 0)
            {
                return false;
            }

            var sourcePosition = _streamManager.GetCurrentPosition();
            if (sourcePosition < trimWindow.PlayableStart
                || sourcePosition >= trimWindow.PlayableEnd)
            {
                _streamManager.SetCurrentPosition(trimWindow.PlayableStart);
            }
        }

        if (_playbackEndSyncHandle == 0)
        {
            AttachPlaybackEndSync();
        }

        if (trimWindow is not null && _playbackEndSyncHandle == 0)
        {
            return false;
        }

        // Ensure the BASS device context is current before playing.
        _ = BassAudioOutputDevices.TrySetCurrentDevice(_currentDevice, "AudioEngine", raiseDiagnostic: false);

        var result = MBass.ChannelPlay(playHandle);
        if (!result)
        {
            // The output may be paused because the device became unavailable (another application
            // holding it exclusively, or unplugged): restart it once and retry (ENG-032).
            var error = MBass.LastError;
            if (BassOutputWatchdog.IsOutputUnavailableError(error)
                && BassOutputWatchdog.TryRestoreOutputForPlay(playHandle, _currentDevice, _currentDeviceSampleRate))
            {
                result = MBass.ChannelPlay(playHandle);
                if (!result)
                    error = MBass.LastError;
            }

            if (!result)
                DiagnosticRouter.Raise(MediaDiagnosticLevel.Error, "AudioEngine", BassOutputWatchdog.DescribePlayFailure(error));
        }

        if (result)
        {
            State = AudioEngineState.Playing;
        }
        return result;
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Pause()
    {
        lock (_transportGate)
        {
            var playHandle = _streamManager.PlayHandle;
            if (playHandle == 0) return false;

            var success = MBass.ChannelPause(playHandle);
            if (success)
            {
                State = AudioEngineState.Paused;
            }

            return success;
        }
    }

    /// <inheritdoc/>
    public bool Stop()
    {
        lock (_transportGate)
        {
            var playHandle = _streamManager.PlayHandle;
            if (playHandle == 0) return false;

            var success = MBass.ChannelStop(playHandle);
            _fade.EndSession(releaseHandle: false);

            // Reset positions for all streams
            _streamManager.ResetPosition();

            State = AudioEngineState.Stopped;
            return success;
        }
    }

    int IBassOutputClient.PlayingHandle => !_disposed && State == AudioEngineState.Playing ? _streamManager.PlayHandle : 0;

    bool IBassOutputClient.ResumeChannelPausedByDevice()
    {
        lock (_transportGate)
        {
            var playHandle = _streamManager.PlayHandle;
            if (_disposed || State != AudioEngineState.Playing || playHandle == 0
                || (int)MBass.ChannelIsActive(playHandle) != BassOutputWatchdog.ActivePausedDevice)
            {
                return true;
            }

            // Not restarted: the channel carries on from its position.
            return MBass.ChannelPlay(playHandle);
        }
    }

    /// <inheritdoc/>
    public void Close()
    {
        if (_disposed) return;

        CancelNormalizationAnalysis();
        RemovePlaybackEndSync();
        _fade.EndSession(releaseHandle: true);
        StopNetworkMonitoring();
        _equalizer.Forget();

        _streamManager.Close();
        Volatile.Write(ref _activeTrimWindow, null);
        _normalizer.ResetGain(clearFxHandle: true);
        Closed?.Invoke(this, EventArgs.Empty);
        State = AudioEngineState.Stopped;
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetVolume(double volume01)
    {
        var playHandle = _streamManager.PlayHandle;
        if (playHandle == 0) return;

        // Just set volume directly - gain is handled separately by FX
        // No need for CalculateEffectiveVolume anymore
        _ = BassChannelVolume.Set(playHandle, volume01);
    }

    void IAudioVolumeSlide.SlideVolume(double volume01, TimeSpan duration)
    {
        var playHandle = _streamManager.PlayHandle;
        if (playHandle == 0) return;

        _ = BassChannelVolume.Slide(playHandle, volume01, duration);
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetPitch(int semitones)
    {
        var playHandle = _streamManager.PlayHandle;
        if (playHandle == 0) return;

        semitones = Math.Clamp(semitones, -60, 60);
        _ = MBass.ChannelSetAttribute(playHandle, ChannelAttribute.Pitch, semitones);
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetTempo(int percent)
    {
        var playHandle = _streamManager.PlayHandle;
        if (playHandle == 0) return;

        percent = Math.Clamp(percent, -95, 5000);
        CurrentTempo = percent; // Track the current tempo setting
        _ = MBass.ChannelSetAttribute(playHandle, ChannelAttribute.Tempo, percent);
        // BASS_FX position syncs remain tied to the same rendered audio position after
        // a tempo change; only their wall-clock arrival changes, so no re-arm is needed.
    }

    /// <summary>
    /// Analyzes a local plain-audio file on its own decode stream without touching the current
    /// media, then warms the shared media-analysis and normalization-gain caches so a later open of
    /// the same file applies the result without rescanning.
    /// </summary>
    async Task<MediaAnalysisResult?> IMediaFileAnalyzer.AnalyzeAsync(string filePath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        if (_disposed || AudioStreamManager.IsTrackerFormat(filePath) || !File.Exists(filePath))
            return null;

        var mediaKey = CreateMediaAnalysisCacheKey(filePath, analyzeSilence: true);
        if (mediaKey is { } cachedKey
            && _analysisCache.TryGet(cachedKey, out var cached)
            && cached is not null)
        {
            return cached;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _standaloneAnalysisLifetime.Token);
        var token = linked.Token;
        _ = Interlocked.Increment(ref _standaloneAnalysisCount);
        var handle = 0;
        try
        {
            // An FFmpeg-fallback file is converted now and kept, so its transition starts at once.
            handle = FfmpegAudioFallback.RequiresConversion(filePath)
                ? FfmpegAudioFallback.Prepare(filePath, token)?.CreateStream(AudioStreamManager.DecodeFlags) ?? 0
                : MBass.CreateStream(filePath, Flags: AudioStreamManager.DecodeFlags);
            if (handle == 0)
            {
                DiagnosticRouter.Raise(MediaDiagnosticLevel.Warning, "AudioEngine", $"Standalone analysis could not open '{filePath}': {MBass.LastError}", sourcePath: filePath);
                return null;
            }

            var lengthBytes = MBass.ChannelGetLength(handle);
            var sourceDuration = lengthBytes > 0 ? MBass.ChannelBytes2Seconds(handle, lengthBytes) : 0;
            if (!double.IsFinite(sourceDuration) || sourceDuration <= 0)
                return null;

            var gainKey = CreatePathNormalizationCacheKey(filePath);
            if (!NormalizationGainCache.TryGetValue(gainKey, out var normalizationGain))
            {
                // The same final gain as playback's analysis (NormalizationGainPolicy), as it shares the key.
                var quickPeak = await _normalizer.AnalyzePeakQuickAsync(BassChannelLevelSource.ForHandle(handle), updateGain: false, cancellationToken: token).ConfigureAwait(false);
                var peak = await _normalizer.AnalyzePeakAsync(BassChannelLevelSource.ForHandle(handle), updateGain: false, cancellationToken: token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                normalizationGain = AudioNormalizer.CalculateGainFromPeak(NormalizationGainPolicy.FinalPeak(quickPeak, peak));
                NormalizationGainCache.Set(gainKey, normalizationGain);
            }

            var scan = await AudioSilenceAnalyzer.ScanAsync(BassChannelLevelSource.ForHandle(handle), sourceDuration, _silenceTrim.ThresholdDecibels, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            var result = SilenceTrimCalculator.Calculate(scan.SourceDuration, scan.SilenceRegions, normalizationGain, _silenceTrim);
            if (mediaKey is { } storeKey)
                _ = _analysisCache.TryStore(storeKey, result);

            DiagnosticRouter.Raise(
                MediaDiagnosticLevel.Info,
                "AudioEngine",
                $"Standalone analysis complete: {result.SilenceRegions.Count} region(s), playable={result.PlayableDuration:F3}s, gain={normalizationGain:F3}",
                sourcePath: filePath);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || _standaloneAnalysisLifetime.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (Exception ex)
        {
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Warning, "AudioEngine", $"Standalone analysis failed for '{filePath}': {ex.Message}", sourcePath: filePath, innerException: ex);
            return null;
        }
        finally
        {
            if (handle != 0)
                _ = MBass.StreamFree(handle);
            _ = Interlocked.Decrement(ref _standaloneAnalysisCount);
        }
    }

    public event EventHandler<MediaBufferingChangedEventArgs>? BufferingChanged;

    public event EventHandler<MediaStreamMetadataChangedEventArgs>? StreamMetadataChanged;

    MediaSourceCapabilities? INetworkAudioSource.NetworkCapabilities
    {
        get
        {
            lock (_networkGate)
            {
                return _networkCapabilities;
            }
        }
    }

    bool INetworkAudioSource.OpenUrl(string url)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);

        if (_disposed)
            return false;

        try
        {
            CancelNormalizationAnalysis();
            RemovePlaybackEndSync();
            ResetActiveTrimWindow();
            _fade.EndSession(releaseHandle: true);
            StopNetworkMonitoring();
            _equalizer.Forget();

            if (!_bassInitialized)
            {
                if (!Init(_requestedDevice))
                {
                    DiagnosticRouter.Raise(MediaDiagnosticLevel.Error, "AudioEngine", "Failed to initialize BASS for network stream", sourcePath: url);
                    return false;
                }
                _bassInitialized = true;
            }

            if (!BassAudioOutputDevices.TrySetCurrentDevice(_currentDevice, "AudioEngine"))
                return false;

            // BASS network timeouts are process-wide settings; they are applied per open so the
            // most recent player's policy governs its own connect.
            MBass.NetTimeOut = (int)_networkRetry.ConnectTimeout.TotalMilliseconds;
            MBass.NetReadTimeOut = (int)_networkRetry.ConnectTimeout.TotalMilliseconds;

            // ENG-024: the load's cancellation (the host's token, the player's Reset or Dispose)
            // ends the retry delays and skips further attempts; a connect in progress finishes.
            var cancellation = MediaOpenCancellation.Current;
            var success = false;
            for (var attempt = 1; attempt <= _networkRetry.MaxAttempts; attempt++)
            {
                if (attempt > 1)
                {
                    var delay = _networkRetry.GetDelayBeforeRetry(attempt - 1);
                    DiagnosticRouter.Raise(MediaDiagnosticLevel.Warning, "AudioEngine", $"Network retry {attempt - 1}/{_networkRetry.MaxRetries} in {delay.TotalSeconds:F1}s", sourcePath: url);
                    _ = cancellation.WaitHandle.WaitOne(delay);
                    if (_disposed)
                        return false;
                }

                if (cancellation.IsCancellationRequested)
                {
                    DiagnosticRouter.Raise(MediaDiagnosticLevel.Info, "AudioEngine", "Network open cancelled", sourcePath: url);
                    return false;
                }

                success = _streamManager.OpenFromUrl(url);
                if (success)
                    break;
            }

            if (!success)
            {
                DiagnosticRouter.Raise(MediaDiagnosticLevel.Error, "AudioEngine", $"Network stream unavailable after {_networkRetry.MaxAttempts} attempt(s): {MBass.LastError}", sourcePath: url);
                return false;
            }

            _normalizer.ResetGain(clearFxHandle: true);
            CurrentTempo = 0;
            var playHandle = _streamManager.PlayHandle;
            if (playHandle != 0)
            {
                _ = _normalizer.SetupGainFx(playHandle);
                _fade.Attach(playHandle);
                _ = _equalizer.Apply(playHandle);
            }

            // No pre-analysis for network sources: normalization stays at unity gain.
            _normalizationReady = true;
            var probe = _streamManager.ProbeNetworkSource();
            var capabilities = MediaSourceCapabilityMatrix.ForNetwork(probe);
            StartNetworkMonitoring(capabilities);
            DiagnosticRouter.Raise(
                MediaDiagnosticLevel.Info,
                "AudioEngine",
                $"Network stream connected: {capabilities.SourceClass}, length={probe.ContentLength?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}, ranges={probe.SupportsRangeRequests}, icy={probe.HasIcyMetadata}",
                sourcePath: url);

            AttachPlaybackEndSync();
            Opened?.Invoke(this, EventArgs.Empty);
            State = AudioEngineState.Stopped;
            PublishNetworkMetadata();
            return true;
        }
        catch (Exception ex)
        {
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Error, "AudioEngine", $"Network open failed: {ex.Message}", sourcePath: url, innerException: ex);
            return false;
        }
    }

    void StartNetworkMonitoring(MediaSourceCapabilities capabilities)
    {
        var syncHandle = _streamManager.NetworkSyncHandle;
        lock (_networkGate)
        {
            var generation = ++_networkGeneration;
            _networkCapabilities = capabilities;
            _bufferingTracker = new BufferingStateTracker(0.05, Math.Clamp(MBass.NetPreBuffer / 100.0, 0.05, 1));
            _networkMonitor = new Timer(_ => SampleNetworkBuffer(generation), null, TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(250));
        }

        if (syncHandle != 0)
        {
            _ = MBass.ChannelSetSync(syncHandle, SyncFlags.MetadataReceived, 0, _networkMetadataSyncProcedure);
            _ = MBass.ChannelSetSync(syncHandle, SyncFlags.OggChange, 0, _networkMetadataSyncProcedure);
        }
    }

    void StopNetworkMonitoring()
    {
        Timer? monitor;
        lock (_networkGate)
        {
            _networkGeneration++;
            monitor = _networkMonitor;
            _networkMonitor = null;
            _bufferingTracker = null;
            _networkCapabilities = null;
        }

        // Syncs are freed with the stream; the timer is disposed here and stale callbacks are
        // ignored by generation.
        monitor?.Dispose();
    }

    void SampleNetworkBuffer(int generation)
    {
        try
        {
            BufferingStateTracker? tracker;
            lock (_networkGate)
            {
                if (generation != _networkGeneration || _bufferingTracker is null)
                    return;

                tracker = _bufferingTracker;
            }

            var (bufferedBytes, targetBytes, downloadComplete) = _streamManager.SampleNetworkBuffer();
            bool changed;
            double fill;
            bool isBuffering;
            lock (_networkGate)
            {
                if (generation != _networkGeneration)
                    return;

                changed = tracker.Update(bufferedBytes, targetBytes, downloadComplete, out fill);
                isBuffering = tracker.IsBuffering;
            }

            if (changed)
                BufferingChanged?.Invoke(this, new MediaBufferingChangedEventArgs(isBuffering, fill));
        }
        catch (Exception ex)
        {
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Warning, "AudioEngine", $"Network buffer sampling failed: {ex.Message}", innerException: ex);
        }
    }

    void OnNetworkMetadataSync(int handle, int channel, int data, IntPtr user)
    {
        // A BASS callback thread: re-enter the owning player (ENG-025), as the end sync does.
        using var owner = DiagnosticRouter.EnterOwner(_diagnosticOwner);
        PublishNetworkMetadata();
    }

    void PublishNetworkMetadata()
    {
        try
        {
            var metadata = IcyMetadataParser.ParseIcy(_streamManager.ReadIcyMetadata());
            if (metadata == default)
                metadata = IcyMetadataParser.ParseOggComments(_streamManager.ReadOggComments());
            if (metadata == default)
                return;

            StreamMetadataChanged?.Invoke(this, new MediaStreamMetadataChangedEventArgs(metadata.Title, metadata.Artist, metadata.RawTitle));
        }
        catch (Exception ex)
        {
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Warning, "AudioEngine", $"Stream metadata read failed: {ex.Message}", innerException: ex);
        }
    }

    AudioEqualizerSettings IAudioFilterHost.Equalizer => _equalizer.Settings;

    bool IAudioFilterHost.SetEqualizer(AudioEqualizerSettings settings) => _equalizer.Configure(settings);

    int IAudioFadeGainTarget.BeginFadeSession() => _fade.BeginFadeSession();

    bool IAudioFadeGainTarget.SetFadeGain(int session, double gain, TimeSpan ramp) => _fade.SetFadeGain(session, gain, ramp);

    void StopStandaloneAnalyses()
    {
        _standaloneAnalysisLifetime.Cancel();

        // Standalone analyses own their decode handles. Cancellation is cooperative (one level
        // window per check), so give them a short bounded join before engine teardown proceeds.
        var deadline = Environment.TickCount64 + 2000;
        while (Volatile.Read(ref _standaloneAnalysisCount) > 0 && Environment.TickCount64 < deadline)
            Thread.Sleep(10);
    }

    /// <inheritdoc/>
    public async Task<double> AnalyzePeakQuickAsync(CancellationToken cancellationToken = default)
    {
        var handle = _streamManager.GetAnalysisHandle();
        return await _normalizer.AnalyzePeakQuickAsync(BassChannelLevelSource.ForHandle(handle), cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<double> AnalyzePeakAsync(CancellationToken cancellationToken = default)
    {
        var handle = _streamManager.GetAnalysisHandle();
        return await _normalizer.AnalyzePeakAsync(BassChannelLevelSource.ForHandle(handle), cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;

        BassOutputWatchdog.Unregister(this);
        CancelNormalizationAnalysis();
        StopStandaloneAnalyses();
        StopNetworkMonitoring();

        _streamManager?.Dispose();

        // DON'T shut down BASS globally - other engines (like UnifiedEngine) might be using it
        // The global BASS instance should persist for the application lifetime
        if (_bassInitialized)
        {
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Info, "AudioEngine", "Disposed (global BASS instance preserved)");
            _bassInitialized = false;
        }

        _disposed = true;
        GC.SuppressFinalize(this);
    }

    #endregion

    #region Private Methods - Core

    void AttachPlaybackEndSync()
    {
        RemovePlaybackEndSync();

        var playHandle = _streamManager.PlayHandle;
        if (playHandle == 0)
        {
            return;
        }

        var syncFlags = SyncFlags.End | SyncFlags.Onetime;
        var syncPosition = 0L;
        var trimWindow = Volatile.Read(ref _activeTrimWindow);
        if (trimWindow is not null
            && trimWindow.PlayableEnd > 0
            && trimWindow.PlayableEnd < _streamManager.DurationSeconds)
        {
            // Official BASS custom-loop guidance requires a mixtime position sync so
            // the boundary is handled in the rendering update that reaches it.
            syncFlags = SyncFlags.Position | SyncFlags.Mixtime | SyncFlags.Onetime;
            syncPosition = MBass.ChannelSeconds2Bytes(playHandle, trimWindow.PlayableEnd);
            if (syncPosition < 0)
            {
                DiagnosticRouter.Raise(
                    MediaDiagnosticLevel.Error,
                    "AudioEngine",
                    $"Failed to convert trim end {trimWindow.PlayableEnd:F3}s to a channel position: {MBass.LastError}");
                return;
            }
        }

        _playbackEndSyncHandle = MBass.ChannelSetSync(
            playHandle,
            syncFlags,
            syncPosition,
            _playbackEndedSyncProcedure,
            IntPtr.Zero);
        _playbackEndSyncChannel = _playbackEndSyncHandle != 0 ? playHandle : 0;

        if (_playbackEndSyncHandle == 0)
        {
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Error, "AudioEngine", $"Failed to attach playback end sync: {MBass.LastError}");
        }
    }

    void RemovePlaybackEndSync()
    {
        var syncHandle = _playbackEndSyncHandle;
        var channel = _playbackEndSyncChannel;
        _playbackEndSyncHandle = 0;
        _playbackEndSyncChannel = 0;

        if (syncHandle != 0 && channel != 0)
        {
            _ = MBass.ChannelRemoveSync(channel, syncHandle);
        }
    }

    // BASS calls sync procedures on its own threads, where an exception would end the process:
    // a failing event subscriber is reported instead.
    void OnPlaybackEndedSync(int handle, int channel, int data, IntPtr user)
    {
        // A native BASS thread carries no execution context: attribute to the engine's player.
        using var diagnosticOwner = DiagnosticRouter.EnterOwner(_diagnosticOwner);
        try
        {
            HandlePlaybackEnded(channel);
        }
        catch (Exception ex)
        {
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Error, "AudioEngine", $"Playback end handling failed: {ex.Message}", innerException: ex);
        }
    }

    void HandlePlaybackEnded(int channel)
    {
        if (channel != _playbackEndSyncChannel)
        {
            return;
        }

        _playbackEndSyncHandle = 0;
        _playbackEndSyncChannel = 0;

        // A fade that ran to the end (typically a fade-out) must not leave the next pass, a
        // replay or a loop wrap, muted. Natural end closes the fade session like Stop does.
        _fade.EndSession(releaseHandle: false);

        var trimWindow = Volatile.Read(ref _activeTrimWindow);
        if (((IAudioLoopControl)this).LoopPlayback)
        {
            if (trimWindow is not null)
                _streamManager.SetCurrentPosition(trimWindow.PlayableStart, flush: true);

            // The sync is one-shot: re-arm it for the next wrap, which the native loop flag
            // (untrimmed) or the seek above (trimmed) starts, so every wrap is reported.
            AttachPlaybackEndSync();

            State = AudioEngineState.Playing;
            PositionChanged?.Invoke(this, trimWindow?.PlayableStart ?? Position);
        }
        else
        {
            if (trimWindow is not null)
            {
                _ = MBass.ChannelStop(channel);
            }

            State = AudioEngineState.Stopped;
            PositionChanged?.Invoke(this, trimWindow?.PlayableEnd ?? Duration);
        }

        PlaybackEnded?.Invoke(this, EventArgs.Empty);
    }

    #endregion

    #region Private Methods - Adaptive Sample Rate

    void LogAudioInfo(string filePath)
    {
        try
        {
            var playHandle = _streamManager.PlayHandle;
            if (playHandle == 0) return;

            var channelInfo = MBass.ChannelGetInfo(playHandle);
            Debug.WriteLine($"[AudioEngine] Audio opened: {Path.GetFileName(filePath)}");
            Debug.WriteLine($"  File rate: {channelInfo.Frequency} Hz, Channels: {channelInfo.Channels}");
            Debug.WriteLine($"  Device rate: {_currentDeviceSampleRate} Hz");

            if (channelInfo.Frequency != _currentDeviceSampleRate)
            {
                Debug.WriteLine($"  ? BASS will resample from {channelInfo.Frequency} Hz to {_currentDeviceSampleRate} Hz");
            }

            DiagnosticRouter.Raise(MediaDiagnosticLevel.Info, "AudioEngine",
                $"Audio opened: {Path.GetFileName(filePath)} ({channelInfo.Frequency}Hz, {channelInfo.Channels}ch, device {_currentDeviceSampleRate}Hz)");
        }
        catch (Exception ex)
        {
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Warning, "AudioEngine", $"Error logging audio info: {ex.Message}", innerException: ex);
        }
    }

    void StartNormalizationAnalysis(
        string cacheKey,
        string sourceLabel,
        MediaAnalysisCacheKey? analysisCacheKey = null)
    {
        CancelNormalizationAnalysis();

        if (analysisCacheKey is { } mediaKey
            && _analysisCache.TryGet(mediaKey, out var cachedAnalysis)
            && cachedAnalysis is not null
            && TryActivateTrimWindow(cachedAnalysis))
        {
            ApplyCachedNormalizationGain(cacheKey, cachedAnalysis.NormalizationGain, sourceLabel);
            DiagnosticRouter.Raise(
                MediaDiagnosticLevel.Info,
                "AudioEngine",
                $"Using cached media analysis with {cachedAnalysis.SilenceRegions.Count} silence region(s) ({sourceLabel})");
            return;
        }

        double? cachedGain = null;
        if (NormalizationGainCache.TryGetValue(cacheKey, out var normalizationGain))
        {
            cachedGain = normalizationGain;
            ApplyCachedNormalizationGain(cacheKey, normalizationGain, sourceLabel);
            if (analysisCacheKey is null)
            {
                return;
            }
        }

        var analysisHandle = _streamManager.GetAnalysisHandle();
        if (analysisHandle == 0)
        {
            _normalizationReady = true;
            return;
        }

        _normalizationReady = cachedGain.HasValue;
        var sourceDuration = _streamManager.DurationSeconds;
        var generation = Interlocked.Increment(ref _normalizationAnalysisGeneration);
        var cts = new CancellationTokenSource();
        lock (_normalizationAnalysisLock)
        {
            _normalizationAnalysisCts = cts;
            _normalizationAnalysisTask = RunNormalizationAnalysisAsync(
                analysisHandle,
                cacheKey,
                sourceLabel,
                cachedGain,
                analysisCacheKey,
                sourceDuration,
                generation,
                cts.Token);
        }
    }

    async Task RunNormalizationAnalysisAsync(
        int analysisHandle,
        string cacheKey,
        string sourceLabel,
        double? cachedGain,
        MediaAnalysisCacheKey? analysisCacheKey,
        double sourceDuration,
        int generation,
        CancellationToken cancellationToken)
    {
        var originalPosition = analysisCacheKey is not null
            ? MBass.ChannelGetPosition(analysisHandle)
            : -1;

        try
        {
            var normalizationGain = cachedGain;
            if (normalizationGain is null)
            {
                DiagnosticRouter.Raise(MediaDiagnosticLevel.Info, "AudioEngine", $"Quick normalization analysis starting ({sourceLabel})");
                var quickPeak = await _normalizer.AnalyzePeakQuickAsync(BassChannelLevelSource.ForHandle(analysisHandle), updateGain: false, cancellationToken: cancellationToken).ConfigureAwait(false);
                // Not cached: the quick gain never boosts, so it is not the source's gain.
                ApplyNormalizationGainIfCurrent(
                    NormalizationGainPolicy.QuickGain(quickPeak),
                    cacheKey: null,
                    NormalizationGainPolicy.QuickPassHeldBackBoost(quickPeak) ? $"Quick normalization complete (boost waits for the full scan) ({sourceLabel})" : $"Quick normalization complete ({sourceLabel})",
                    generation,
                    cancellationToken);

                DiagnosticRouter.Raise(MediaDiagnosticLevel.Info, "AudioEngine", $"Deep normalization analysis starting ({sourceLabel})");
                var deepPeak = await _normalizer.AnalyzePeakAsync(BassChannelLevelSource.ForHandle(analysisHandle), updateGain: false, cancellationToken: cancellationToken).ConfigureAwait(false);
                ApplyNormalizationGainIfCurrent(
                    AudioNormalizer.CalculateGainFromPeak(NormalizationGainPolicy.FinalPeak(quickPeak, deepPeak)),
                    cacheKey,
                    $"Deep normalization complete ({sourceLabel})",
                    generation,
                    cancellationToken);
                normalizationGain = _normalizer.NormalizationGain;
            }

            if (analysisCacheKey is { } mediaKey)
            {
                cancellationToken.ThrowIfCancellationRequested();
                DiagnosticRouter.Raise(MediaDiagnosticLevel.Info, "AudioEngine", $"Silence analysis starting ({sourceLabel})");
                var scan = await AudioSilenceAnalyzer.ScanAsync(
                    BassChannelLevelSource.ForHandle(analysisHandle),
                    sourceDuration,
                    _silenceTrim.ThresholdDecibels,
                    cancellationToken).ConfigureAwait(false);

                if (!IsCurrentNormalizationAnalysis(generation, cancellationToken))
                {
                    return;
                }

                var result = SilenceTrimCalculator.Calculate(
                    scan.SourceDuration,
                    scan.SilenceRegions,
                    normalizationGain.Value,
                    _silenceTrim);
                if (_analysisCache.TryStore(mediaKey, result))
                {
                    DiagnosticRouter.Raise(
                        MediaDiagnosticLevel.Info,
                        "AudioEngine",
                        $"Silence analysis complete: {result.SilenceRegions.Count} region(s), playable={result.PlayableDuration:F3}s ({sourceLabel})");
                }
            }
        }
        catch (OperationCanceledException)
        {
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Info, "AudioEngine", $"Normalization analysis cancelled ({sourceLabel})");
        }
        catch (Exception ex)
        {
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Error, "AudioEngine", $"Normalization analysis failed ({sourceLabel}): {ex.Message}", innerException: ex);
        }
        finally
        {
            if (originalPosition >= 0 && !MBass.ChannelSetPosition(analysisHandle, originalPosition))
            {
                DiagnosticRouter.Raise(
                    MediaDiagnosticLevel.Warning,
                    "AudioEngine",
                    $"Unable to restore analysis stream position ({sourceLabel}): {MBass.LastError}");
            }
        }
    }

    void ApplyCachedNormalizationGain(string cacheKey, double gain, string sourceLabel)
    {
        DiagnosticRouter.Raise(MediaDiagnosticLevel.Info, "AudioEngine", $"Using cached normalization gain {gain:F3} ({sourceLabel})");
        NormalizationGainCache.Set(cacheKey, gain);
        _normalizationReady = true;
        _normalizer.SetNormalizationGain(gain, forceNotify: true);
        if (Normalize)
        {
            _normalizer.SetNormalizeEnabled(true);
        }
    }

    bool TryActivateTrimWindow(MediaAnalysisResult result)
    {
        if (!MediaAnalysisResultValidator.IsValid(result)
            || Math.Abs(result.SourceDuration - _streamManager.DurationSeconds) > CachedDurationToleranceSeconds)
        {
            return false;
        }

        Volatile.Write(ref _activeTrimWindow, result);
        _streamManager.SetLoopPlayback(false);
        return true;
    }

    void ResetActiveTrimWindow()
    {
        Volatile.Write(ref _activeTrimWindow, null);
        _streamManager.SetLoopPlayback(_loopPlayback);
    }

    void ApplyNormalizationGainIfCurrent(double gain, string? cacheKey, string message, int generation, CancellationToken cancellationToken)
    {
        if (!IsCurrentNormalizationAnalysis(generation, cancellationToken))
            return;

        _normalizer.SetNormalizationGain(gain, forceNotify: true);
        _normalizationReady = true;
        if (cacheKey is not null)
            NormalizationGainCache.Set(cacheKey, gain);

        if (Normalize)
            _normalizer.SetNormalizeEnabled(true);

        DiagnosticRouter.Raise(MediaDiagnosticLevel.Info, "AudioEngine", $"{message}, gain={gain:F3}");
    }

    bool IsCurrentNormalizationAnalysis(int generation, CancellationToken cancellationToken) => !_disposed
            && !cancellationToken.IsCancellationRequested
            && Volatile.Read(ref _normalizationAnalysisGeneration) == generation;

    void CancelNormalizationAnalysis()
    {
        _ = Interlocked.Increment(ref _normalizationAnalysisGeneration);
        CancellationTokenSource? previous;
        Task? analysisTask;
        lock (_normalizationAnalysisLock)
        {
            previous = _normalizationAnalysisCts;
            analysisTask = _normalizationAnalysisTask;
            _normalizationAnalysisCts = null;
            _normalizationAnalysisTask = null;
        }

        previous?.Cancel();

        // Synchronously join the previous analysis before the caller frees the decode handle it
        // reads from (Open reuses the handle slot, Close/Dispose free it). Cancellation is
        // cooperative - ChannelGetLevel blocks per analysis window - so joining here guarantees the
        // handle is never touched after the stream is closed. GetAwaiter().GetResult() unwraps the
        // AggregateException while preserving the original exception for cancellation/fault handling.
        try
        {
            analysisTask?.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            // Expected during re-open or teardown.
        }
        catch (Exception ex)
        {
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Warning, "AudioEngine", $"Normalization analysis did not stop cleanly: {ex.Message}", innerException: ex);
        }

        previous?.Dispose();
        _normalizationReady = true;
    }

    internal static string CreatePathNormalizationCacheKey(string filePath)
    {
        try
        {
            var info = new FileInfo(filePath);
            return $"file|{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
        }
        catch
        {
            return $"file|{Path.GetFullPath(filePath)}";
        }
    }

    internal static string CreateArchiveNormalizationCacheKey(IArchiveEntryInfo archiveEntry) => $"archive|{archiveEntry.Key}|{archiveEntry.Size?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}";

    MediaAnalysisCacheKey? CreateMediaAnalysisCacheKey(string filePath, bool analyzeSilence) =>
        analyzeSilence
        && _silenceTrim.Enabled
        && !AudioStreamManager.IsTrackerFormat(filePath)
        && MediaAnalysisCacheKey.TryCreateForFile(
            filePath,
            MediaAnalysisVersions.Current,
            _silenceTrim,
            out var key)
            ? key
            : null;

    /// <summary>
    /// Gets the session-optimal sample rate based on a file's properties.
    /// </summary>
    static int GetSessionOptimalRate(string filePath)
    {
        try
        {
            var extension = Path.GetExtension(filePath).ToLowerInvariant();

            return extension switch
            {
                ".flac" => 48000,      // FLAC often uses 48kHz or higher
                ".wav" => 48000,       // Professional audio often 48kHz
                ".wma" => 44100,       // Windows Media typically 44.1kHz
                ".aac" => 44100,       // AAC typically 44.1kHz
                ".m4a" => 44100,       // iTunes typically 44.1kHz
                ".mp3" => 44100,       // MP3 typically 44.1kHz
                _ => 44100             // Safe default
            };
        }
        catch
        {
            return 44100; // Safe fallback
        }
    }

    static void EnsureDevNonStop()
    {
        try
        {
            var configured = MBass.Configure(ManagedBass.Configuration.DevNonStop, true);
            if (!configured)
                DiagnosticRouter.Raise(MediaDiagnosticLevel.Warning, "AudioEngine", $"BASS Configure(DevNonStop) failed: {MBass.LastError}");
        }
        catch (Exception ex)
        {
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Warning, "AudioEngine", $"BASS Configure(DevNonStop) threw: {ex.Message}", innerException: ex);
        }
    }

    #endregion
}
