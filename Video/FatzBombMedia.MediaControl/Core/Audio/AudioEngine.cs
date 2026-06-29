using FatzBombMedia.MediaControl.Core.Abstractions;
using ManagedBass;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using MBass = ManagedBass.Bass;

namespace FatzBombMedia.MediaControl.Core.Audio;

/// <summary>
/// High-performance BASS-based audio engine with tempo/pitch control and normalization.
/// Optimized for real-time audio playback in karaoke and DJ applications.
/// Features adaptive sample rate initialization for optimal audio quality.
/// </summary>
public sealed class AudioEngine : IAudioEngine
{
    #region Private Fields

    // Composition: Delegate specialized responsibilities to focused components
    private readonly AudioNormalizer _normalizer;
    private readonly AudioStreamManager _streamManager;

    // Adaptive sample rate management
    private bool _bassInitialized = false;
    private int _currentDeviceSampleRate = 44100; // Default fallback
    private int _currentDevice = -1;
    private CancellationTokenSource? _normalizationAnalysisCts;
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
    public AudioEngine() : this(new AudioNormalizer(), new AudioStreamManager())
    {
    }

    /// <summary>
    /// Creates a new AudioEngine with custom components (for testing/advanced scenarios).
    /// </summary>
    /// <param name="normalizer">Custom normalizer implementation</param>
    /// <param name="streamManager">Custom stream manager implementation</param>
    internal AudioEngine(AudioNormalizer normalizer, AudioStreamManager streamManager)
    {
        _normalizer = normalizer ?? throw new ArgumentNullException(nameof(normalizer));
        _streamManager = streamManager ?? throw new ArgumentNullException(nameof(streamManager));
        _playbackEndedSyncProcedure = OnPlaybackEndedSync;

        // Wire up normalizer events to forward to our event
        _normalizer.NormalizationGainChanged += (_, gain) =>
        {
            Debug.WriteLine($"[AudioEngine] Forwarding normalization gain change: {gain:F3}");
            NormalizationGainChanged?.Invoke(this, gain);
        };
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

    // Event declarations for state and lifecycle
    public event EventHandler<AudioEngineState>? StateChanged;
    public event EventHandler? Opened;
    public event EventHandler? Closed;
    public event EventHandler<double>? PositionChanged;

    // Event for orchestration
    public event EventHandler? SeekCompleted;
    public event EventHandler? PlaybackStarted;
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
        get => _streamManager.GetCurrentPosition();
        set
        {
            var old = _streamManager.GetCurrentPosition();
            _streamManager.SetCurrentPosition(value);
            var updated = _streamManager.GetCurrentPosition();
            if (Math.Abs(updated - old) > 0.001)
            {
                PositionChanged?.Invoke(this, updated);
                SeekCompleted?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <inheritdoc/>
    public int PlayHandle => _streamManager.PlayHandle;

    /// <inheritdoc/>
    public double Duration => _streamManager.DurationSeconds;

    #endregion

    #region Public Methods

    /// <inheritdoc/>
    public bool Init(int device = -1, int sampleRate = 44100)
    {
        try
        {
            // Check if already initialized
            if (_bassInitialized)
            {
                Debug.WriteLine($"[AudioEngine] BASS already initialized on this engine");
                return true;
            }

            // Check if BASS is initialized globally by another engine
            if (MBass.GetInfo(out BassInfo existingInfo) && existingInfo.SampleRate > 0)
            {
                Debug.WriteLine($"[AudioEngine] ✅ Using existing global BASS instance: {existingInfo.SampleRate}Hz");
                _bassInitialized = true;
                _currentDevice = device;
                _currentDeviceSampleRate = existingInfo.SampleRate;
                return true;
            }

            // Initialize BASS for the first time
            var success = MBass.Init(device, sampleRate);
            if (success)
            {
                _bassInitialized = true;
                _currentDevice = device;
                _currentDeviceSampleRate = sampleRate;

                Debug.WriteLine($"[AudioEngine] ✅ Initialized BASS globally: device={device}, sampleRate={sampleRate} Hz");
            }
            else
            {
                Debug.WriteLine($"[AudioEngine] ❌ Failed to initialize BASS: {MBass.LastError}");
            }
            return success;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AudioEngine] Init failed: {ex.Message}");
            return false;
        }
    }

    /// <inheritdoc/>
    public bool Open(string filePath)
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
                if (!Init(_currentDevice, sessionOptimalRate))
                {
                    Debug.WriteLine("[AudioEngine] Failed to initialize BASS with session-optimal sample rate");
                    return false;
                }
                _bassInitialized = true;
                _currentDeviceSampleRate = sessionOptimalRate;
            }

            var success = _streamManager.OpenFromPath(filePath);
            if (success)
            {
                _normalizer.ResetGain(clearFxHandle: true);
                CurrentTempo = 0; // Reset tempo when opening new file

                var playHandle = _streamManager.PlayHandle;
                if (playHandle != 0)
                {
                    _ = _normalizer.SetupGainFx(playHandle);
                    Debug.WriteLine($"[AudioEngine] Gain FX setup on handle {playHandle}");
                }

                LogAudioInfo(filePath);

                StartNormalizationAnalysis(CreatePathNormalizationCacheKey(filePath), "file");
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
            Debug.WriteLine($"[AudioEngine] Open failed for '{filePath}': {ex.Message}");
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
                if (!Init(_currentDevice))
                {
                    Debug.WriteLine("[AudioEngine] Failed to initialize BASS for archive entry");
                    return false;
                }
                _bassInitialized = true;
                _currentDeviceSampleRate = 44100;
            }

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
                    Debug.WriteLine($"[AudioEngine] Gain FX setup on handle {playHandle} (archive)");
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
            Debug.WriteLine($"[AudioEngine] Open failed for archive entry '{archiveEntry.Key}': {ex.Message}");
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

        var result = MBass.ChannelPlay(playHandle);
        if (result)
        {
            State = AudioEngineState.Playing;
            PlaybackStarted?.Invoke(this, EventArgs.Empty);
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
    public bool SetVolume(double volume01)
    {
        var playHandle = _streamManager.PlayHandle;
        if (playHandle == 0) return false;

        // Just set volume directly - gain is handled separately by FX
        // No need for CalculateEffectiveVolume anymore
        return MBass.ChannelSetAttribute(playHandle, ChannelAttribute.Volume, volume01);
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool SetPitch(int semitones)
    {
        var playHandle = _streamManager.PlayHandle;
        if (playHandle == 0) return false;

        semitones = Math.Clamp(semitones, -60, 60);
        return MBass.ChannelSetAttribute(playHandle, ChannelAttribute.Pitch, semitones);
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool SetTempo(int percent)
    {
        var playHandle = _streamManager.PlayHandle;
        if (playHandle == 0) return false;

        percent = Math.Clamp(percent, -95, 5000);
        CurrentTempo = percent; // Track the current tempo setting
        return MBass.ChannelSetAttribute(playHandle, ChannelAttribute.Tempo, percent);
    }

    /// <inheritdoc/>
    public async Task<double> AnalyzePeakQuickAsync(CancellationToken cancellationToken = default)
    {
        var handle = _streamManager.GetAnalysisHandle();
        return await _normalizer.AnalyzePeakQuickAsync(handle, cancellationToken: cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<double> AnalyzePeakAsync(CancellationToken cancellationToken = default)
    {
        var handle = _streamManager.GetAnalysisHandle();
        return await _normalizer.AnalyzePeakAsync(handle, cancellationToken: cancellationToken);
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
            Debug.WriteLine("[AudioEngine] AudioEngine disposed (global BASS instance preserved)");
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
            Debug.WriteLine($"[AudioEngine] Failed to attach playback end sync: {MBass.LastError}");
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
        State = AudioEngineState.Stopped;
        PositionChanged?.Invoke(this, Duration);
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
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AudioEngine] Error logging audio info: {ex.Message}");
        }
    }

    private void StartNormalizationAnalysis(string cacheKey, string sourceLabel)
    {
        CancelNormalizationAnalysis();

        if (NormalizationGainCache.TryGetValue(cacheKey, out var cachedGain))
        {
            Debug.WriteLine($"[AudioEngine] Using cached normalization gain {cachedGain:F3} ({sourceLabel})");
            _normalizationReady = true;
            _normalizer.SetNormalizationGain(cachedGain, forceNotify: true);
            if (Normalize)
            {
                _normalizer.SetNormalizeEnabled(true);
            }
            return;
        }

        var analysisHandle = _streamManager.GetAnalysisHandle();
        if (analysisHandle == 0)
        {
            _normalizationReady = true;
            return;
        }

        _normalizationReady = false;
        var generation = Interlocked.Increment(ref _normalizationAnalysisGeneration);
        var cts = new CancellationTokenSource();
        CancellationTokenSource? previous = Interlocked.Exchange(ref _normalizationAnalysisCts, cts);
        previous?.Cancel();
        previous?.Dispose();

        _ = RunNormalizationAnalysisAsync(analysisHandle, cacheKey, sourceLabel, generation, cts.Token);
    }

    private async Task RunNormalizationAnalysisAsync(int analysisHandle, string cacheKey, string sourceLabel, int generation, CancellationToken cancellationToken)
    {
        try
        {
            Debug.WriteLine($"[AudioEngine] Quick normalization analysis starting with handle {analysisHandle} ({sourceLabel})");
            var quickPeak = await _normalizer.AnalyzePeakQuickAsync(analysisHandle, updateGain: false, cancellationToken: cancellationToken).ConfigureAwait(false);
            ApplyNormalizationPeakIfCurrent(quickPeak, cacheKey, sourceLabel, generation, isFinal: false, cancellationToken: cancellationToken);

            Debug.WriteLine($"[AudioEngine] Deep normalization analysis starting ({sourceLabel})");
            var deepPeak = await _normalizer.AnalyzePeakAsync(analysisHandle, updateGain: false, cancellationToken: cancellationToken).ConfigureAwait(false);
            ApplyNormalizationPeakIfCurrent(deepPeak, cacheKey, sourceLabel, generation, isFinal: true, cancellationToken: cancellationToken);
        }
        catch (OperationCanceledException)
        {
            Debug.WriteLine($"[AudioEngine] Normalization analysis cancelled ({sourceLabel})");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AudioEngine] Normalization analysis failed ({sourceLabel}): {ex.Message}");
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

        Debug.WriteLine($"[AudioEngine] {(isFinal ? "Deep" : "Quick")} normalization complete, gain={gain:F3} ({sourceLabel})");
    }

    private bool IsCurrentNormalizationAnalysis(int generation, CancellationToken cancellationToken) => !_disposed
            && !cancellationToken.IsCancellationRequested
            && Volatile.Read(ref _normalizationAnalysisGeneration) == generation;

    private void CancelNormalizationAnalysis()
    {
        _ = Interlocked.Increment(ref _normalizationAnalysisGeneration);
        CancellationTokenSource? previous = Interlocked.Exchange(ref _normalizationAnalysisCts, null);
        previous?.Cancel();
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

    #endregion
}
