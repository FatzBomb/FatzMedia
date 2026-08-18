using FatzBomb.MediaControl.Core.Abstractions;
using ManagedBass;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using MBass = ManagedBass.Bass;

namespace FatzBomb.MediaControl.Core.Audio;

/// <summary>
/// High-performance BASS-based audio engine with tempo/pitch control and normalization.
/// Optimized for real-time audio playback in karaoke and DJ applications.
/// Features adaptive sample rate initialization for optimal audio quality.
/// </summary>
public sealed class AudioEngine : IAudioEngine, IAudioOutputDeviceProvider, IAudioLoopControl, IAudioAnalysisOpenControl
{
    #region Private Fields

    // Composition: Delegate specialized responsibilities to focused components
    private readonly AudioNormalizer _normalizer;
    private readonly AudioSilenceAnalyzer _silenceAnalyzer;
    private readonly MediaAnalysisCache _analysisCache;
    private readonly AudioStreamManager _streamManager;
    private readonly IOutputLatencyProvider _outputLatencyProvider;
    private readonly SilenceTrimOptions _silenceTrim;

    // Adaptive sample rate management
    private bool _bassInitialized = false;
    private int _currentDeviceSampleRate = 44100; // Default fallback
    private int _requestedDevice = AudioOutputDeviceInfo.PrimaryDeviceIndex;
    private int _currentDevice = AudioOutputDeviceInfo.PrimaryDeviceIndex;
    private CancellationTokenSource? _normalizationAnalysisCts;
    private Task? _normalizationAnalysisTask;
    private readonly Lock _normalizationAnalysisLock = new();
    private int _normalizationAnalysisGeneration;
    private volatile bool _normalizationReady = true;
    private readonly SyncProcedure _playbackEndedSyncProcedure;
    private int _playbackEndSyncHandle;
    private int _playbackEndSyncChannel;
    private static readonly ConcurrentDictionary<string, double> NormalizationGainCache = new(StringComparer.OrdinalIgnoreCase);

    private volatile bool _disposed;

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
    internal AudioEngine(
        AudioNormalizer normalizer,
        AudioStreamManager streamManager,
        IOutputLatencyProvider outputLatencyProvider,
        SilenceTrimOptions silenceTrim = default,
        MediaAnalysisCache? analysisCache = null)
    {
        _normalizer = normalizer ?? throw new ArgumentNullException(nameof(normalizer));
        _silenceAnalyzer = new AudioSilenceAnalyzer();
        _analysisCache = analysisCache ?? MediaAnalysisCache.Shared;
        _streamManager = streamManager ?? throw new ArgumentNullException(nameof(streamManager));
        _outputLatencyProvider = outputLatencyProvider ?? throw new ArgumentNullException(nameof(outputLatencyProvider));
        _silenceTrim = silenceTrim;
        _playbackEndedSyncProcedure = OnPlaybackEndedSync;

        // Wire up normalizer events to forward to our event
        _normalizer.NormalizationGainChanged += (_, gain) =>
        {
            Debug.WriteLine($"[AudioEngine] Forwarding normalization gain change: {gain:F3}");
            NormalizationGainChanged?.Invoke(this, gain);
        };
    }

    private double ApplyOutputLatency(double rawPositionSeconds)
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
    public int CurrentTempo { get; private set; } = 0;

    bool IAudioLoopControl.LoopPlayback
    {
        get;
        set
        {
            field = value;
            _streamManager.SetLoopPlayback(value);
        }
    }

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
                _currentDeviceSampleRate = MBass.GetInfo(out BassInfo info) && info.SampleRate > 0
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

    private bool Open(string filePath, bool analyzeSilence)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        try
        {
            CancelNormalizationAnalysis();
            RemovePlaybackEndSync();

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

            var success = _streamManager.OpenFromPath(filePath);
            if (success)
            {
                _normalizer.ResetGain(clearFxHandle: true);
                CurrentTempo = 0; // Reset tempo when opening new file

                var playHandle = _streamManager.PlayHandle;
                if (playHandle != 0)
                {
                    _ = _normalizer.SetupGainFx(playHandle);
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

        try
        {
            CancelNormalizationAnalysis();
            RemovePlaybackEndSync();

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
        var playHandle = _streamManager.PlayHandle;
        if (playHandle == 0) return false;

        if (_playbackEndSyncHandle == 0)
        {
            AttachPlaybackEndSync();
        }

        // Ensure the BASS device context is current before playing.
        // After extended idle time (e.g. media ended, user away for hours),
        // Windows may release the audio device, causing ChannelPlay to fail.
        _ = BassAudioOutputDevices.TrySetCurrentDevice(_currentDevice, "AudioEngine", raiseDiagnostic: false);

        var result = MBass.ChannelPlay(playHandle);
        if (!result && _bassInitialized)
        {
            // Device may have been lost during idle — attempt reinitialization
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Warning, "AudioEngine",
                $"ChannelPlay failed ({MBass.LastError}), attempting device recovery...");
            _bassInitialized = false;
            if (Init(_requestedDevice, _currentDeviceSampleRate))
            {
                _ = BassAudioOutputDevices.TrySetCurrentDevice(_currentDevice, "AudioEngine");
                result = MBass.ChannelPlay(playHandle);
            }
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
        var playHandle = _streamManager.PlayHandle;
        if (playHandle == 0) return false;

        var success = MBass.ChannelPause(playHandle);
        if (success)
        {
            State = AudioEngineState.Paused;
        }

        return success;
    }

    /// <inheritdoc/>
    public bool Stop()
    {
        var playHandle = _streamManager.PlayHandle;
        if (playHandle == 0) return false;

        var success = MBass.ChannelStop(playHandle);

        // Reset positions for all streams
        _streamManager.ResetPosition();

        State = AudioEngineState.Stopped;
        return success;
    }

    /// <inheritdoc/>
    public void Close()
    {
        if (_disposed) return;

        CancelNormalizationAnalysis();
        RemovePlaybackEndSync();

        _streamManager.Close();
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
        _ = MBass.ChannelSetAttribute(playHandle, ChannelAttribute.Volume, volume01);
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
    }

    /// <inheritdoc/>
    public async Task<double> AnalyzePeakQuickAsync(CancellationToken cancellationToken = default)
    {
        var handle = _streamManager.GetAnalysisHandle();
        return await _normalizer.AnalyzePeakQuickAsync(handle, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<double> AnalyzePeakAsync(CancellationToken cancellationToken = default)
    {
        var handle = _streamManager.GetAnalysisHandle();
        return await _normalizer.AnalyzePeakAsync(handle, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;

        CancelNormalizationAnalysis();

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

    private void AttachPlaybackEndSync()
    {
        RemovePlaybackEndSync();

        var playHandle = _streamManager.PlayHandle;
        if (playHandle == 0)
        {
            return;
        }

        _playbackEndSyncHandle = MBass.ChannelSetSync(
            playHandle,
            SyncFlags.End | SyncFlags.Onetime,
            0,
            _playbackEndedSyncProcedure,
            IntPtr.Zero);
        _playbackEndSyncChannel = _playbackEndSyncHandle != 0 ? playHandle : 0;

        if (_playbackEndSyncHandle == 0)
        {
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Error, "AudioEngine", $"Failed to attach playback end sync: {MBass.LastError}");
        }
    }

    private void RemovePlaybackEndSync()
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

    private void OnPlaybackEndedSync(int handle, int channel, int data, IntPtr user)
    {
        if (channel != _playbackEndSyncChannel)
        {
            return;
        }

        _playbackEndSyncHandle = 0;
        _playbackEndSyncChannel = 0;
        if (((IAudioLoopControl)this).LoopPlayback)
        {
            State = AudioEngineState.Playing;
            PositionChanged?.Invoke(this, Position);
        }
        else
        {
            State = AudioEngineState.Stopped;
            PositionChanged?.Invoke(this, Duration);
        }

        PlaybackEnded?.Invoke(this, EventArgs.Empty);
    }

    #endregion

    #region Private Methods - Adaptive Sample Rate

    private void LogAudioInfo(string filePath)
    {
        try
        {
            var playHandle = _streamManager.PlayHandle;
            if (playHandle == 0) return;

            ChannelInfo channelInfo = MBass.ChannelGetInfo(playHandle);
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

    private void StartNormalizationAnalysis(
        string cacheKey,
        string sourceLabel,
        MediaAnalysisCacheKey? analysisCacheKey = null)
    {
        CancelNormalizationAnalysis();

        if (analysisCacheKey is { } mediaKey
            && _analysisCache.TryGet(mediaKey, out var cachedAnalysis)
            && cachedAnalysis is not null)
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

    private async Task RunNormalizationAnalysisAsync(
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
                var quickPeak = await _normalizer.AnalyzePeakQuickAsync(analysisHandle, updateGain: false, cancellationToken: cancellationToken).ConfigureAwait(false);
                ApplyNormalizationPeakIfCurrent(quickPeak, cacheKey, sourceLabel, generation, isFinal: false, cancellationToken: cancellationToken);

                DiagnosticRouter.Raise(MediaDiagnosticLevel.Info, "AudioEngine", $"Deep normalization analysis starting ({sourceLabel})");
                var deepPeak = await _normalizer.AnalyzePeakAsync(analysisHandle, updateGain: false, cancellationToken: cancellationToken).ConfigureAwait(false);
                ApplyNormalizationPeakIfCurrent(deepPeak, cacheKey, sourceLabel, generation, isFinal: true, cancellationToken: cancellationToken);
                normalizationGain = _normalizer.NormalizationGain;
            }

            if (analysisCacheKey is { } mediaKey)
            {
                cancellationToken.ThrowIfCancellationRequested();
                DiagnosticRouter.Raise(MediaDiagnosticLevel.Info, "AudioEngine", $"Silence analysis starting ({sourceLabel})");
                var scan = await _silenceAnalyzer.ScanAsync(
                    analysisHandle,
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

    private void ApplyCachedNormalizationGain(string cacheKey, double gain, string sourceLabel)
    {
        DiagnosticRouter.Raise(MediaDiagnosticLevel.Info, "AudioEngine", $"Using cached normalization gain {gain:F3} ({sourceLabel})");
        NormalizationGainCache[cacheKey] = gain;
        _normalizationReady = true;
        _normalizer.SetNormalizationGain(gain, forceNotify: true);
        if (Normalize)
        {
            _normalizer.SetNormalizeEnabled(true);
        }
    }

    private void ApplyNormalizationPeakIfCurrent(double peak, string cacheKey, string sourceLabel, int generation, bool isFinal, CancellationToken cancellationToken)
    {
        if (!IsCurrentNormalizationAnalysis(generation, cancellationToken))
            return;

        var gain = _normalizer.SetNormalizationGainFromPeak(peak, forceNotify: true);
        _normalizationReady = true;
        NormalizationGainCache[cacheKey] = gain;

        if (Normalize)
            _normalizer.SetNormalizeEnabled(true);

        DiagnosticRouter.Raise(MediaDiagnosticLevel.Info, "AudioEngine", $"{(isFinal ? "Deep" : "Quick")} normalization complete, gain={gain:F3} ({sourceLabel})");
    }

    private bool IsCurrentNormalizationAnalysis(int generation, CancellationToken cancellationToken) => !_disposed
            && !cancellationToken.IsCancellationRequested
            && Volatile.Read(ref _normalizationAnalysisGeneration) == generation;

    private void CancelNormalizationAnalysis()
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

    private static string CreatePathNormalizationCacheKey(string filePath)
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

    private static string CreateArchiveNormalizationCacheKey(IArchiveEntryInfo archiveEntry) => $"archive|{archiveEntry.Key}|{archiveEntry.Size?.ToString() ?? "unknown"}";

    private MediaAnalysisCacheKey? CreateMediaAnalysisCacheKey(string filePath, bool analyzeSilence) =>
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
    private static int GetSessionOptimalRate(string filePath)
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

    private static void EnsureDevNonStop()
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
