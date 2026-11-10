using FatzMedia.Video.Core.Abstractions;
using FatzMedia.Video.Core.Audio.Fade;
using FatzMedia.Video.Core.Audio.Filters;
using ManagedBass;
using ManagedBass.Fx;

namespace FatzMedia.Video.Core.Audio;

/// <summary>
/// <see cref="VideoAudioEngine"/>'s BASS output, moved out of the engine unchanged (037A slice 5): a
/// BASS push stream behind a BASS_FX tempo stream, with the equalizer, our peak normalization (a
/// background FFmpeg scan, quick then deep), the fade stage and the channel volume, in that order.
/// </summary>
sealed class BassPushAudioOutput(IOutputLatencyProvider outputLatencyProvider, int bassDeviceIndex) : IPushAudioOutput
{
    // The quick normalization pass may attenuate but not boost; see RunNormalizationAnalysis.
    static NormalizationGainCache NormalizationGainCache => NormalizationGainCache.Shared;

    readonly IOutputLatencyProvider _outputLatencyProvider = outputLatencyProvider ?? throw new ArgumentNullException(nameof(outputLatencyProvider));

    // Effects chain on PlayHandle, in the same order as AudioEngine: equalizer, normalization
    // gain, fade envelope, then the channel volume attribute (user volume).
    readonly BassEqualizerFilter _equalizer = new();
    readonly AudioNormalizer _normalizer = new();
    readonly BassFadeGainStage _fade = new("VideoAudioEngine");

    // Normalization runs on a background FfmpegAudioReader. _gainGate serializes gain FX writes
    // between it and the Normalize setter; the analysis never takes the engine's lock, so Release
    // can join it while the engine holds that lock.
    readonly Lock _gainGate = new();
    CancellationTokenSource? _normalizationCts;
    Task? _normalizationTask;
    int _normalizationGeneration;
    volatile bool _normalizationReady = true;
    readonly Lock _gainEventGate = new();
    Task _gainEvents = Task.CompletedTask;

    double _volume = 1.0;
    int _pitch;
    int _tempo;
    bool _normalize = true;

    public int BassDeviceIndex { get; } = bassDeviceIndex;
    public int PlayHandle { get; private set; }
    public int SourceHandle { get; private set; }
    public bool IsCreated => PlayHandle != 0;

    public event EventHandler<double>? NormalizationGainChanged;

    public bool Normalize
    {
        get => _normalize;
        set
        {
            Volatile.Write(ref _normalize, value);
            lock (_gainGate)
                _normalizer.SetNormalizeEnabled(value);
        }
    }

    public double NormalizationGain => _normalizer.NormalizationGain;
    public bool IsNormalizationReady => _normalizationReady;
    public AudioEqualizerSettings Equalizer => _equalizer.Settings;

    /// <summary>The push stream as the MIDI source, exactly as the lyric path read it before 037A slice 1.</summary>
    public IMidiEventReader? GetMidiEventReader() => BassMidiEventReader.ForHandle(SourceHandle);

    public bool Create(int sampleRate, int channels, string? sourcePath)
    {
        if (!BassAudioOutputDevices.TrySetCurrentDevice(BassDeviceIndex, "VideoAudioEngine"))
            return false;

        var flags = BassFlags.Decode | BassFlags.Float;
        if (channels == 1)
            flags |= BassFlags.Mono;

        SourceHandle = Bass.CreateStream(sampleRate, channels, flags, StreamProcedureType.Push);
        if (SourceHandle == 0)
        {
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Error, "VideoAudioEngine", $"Failed to create BASS push stream: {Bass.LastError}");
            return false;
        }

        PlayHandle = BassFx.TempoCreate(SourceHandle, BassFlags.Float);
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
        _ = Bass.ChannelSetAttribute(PlayHandle, ChannelAttribute.Tempo, _tempo);
        return true;
    }

    public void Release()
    {
        // Join the analysis first: it reads the file (possibly a temp file deleted next) and writes
        // the gain FX on PlayHandle.
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
    }

    public unsafe bool Push(ReadOnlySpan<byte> interleavedFloat, double continuousStartSeconds)
    {
        // BASS copies the samples into its queue: no managed copy first.
        fixed (byte* data = interleavedFloat)
            return Bass.StreamPutData(SourceHandle, (IntPtr)data, interleavedFloat.Length) > 0;
    }

    public bool Play()
    {
        if (PlayHandle == 0)
            return false;

        var success = Bass.ChannelPlay(PlayHandle);
        if (!success)
        {
            // The output may be paused because the device became unavailable (another application
            // holding it exclusively, or unplugged): restart it once and retry (ENG-032).
            var error = Bass.LastError;
            if (BassOutputWatchdog.IsOutputUnavailableError(error)
                && BassOutputWatchdog.TryRestoreOutputForPlay(PlayHandle, BassDeviceIndex, 0))
            {
                success = Bass.ChannelPlay(PlayHandle);
                if (!success)
                    error = Bass.LastError;
            }

            if (!success)
                DiagnosticRouter.Raise(MediaDiagnosticLevel.Error, "VideoAudioEngine", BassOutputWatchdog.DescribePlayFailure(error));
        }

        return success;
    }

    public bool Resume() => PlayHandle != 0 && Bass.ChannelPlay(PlayHandle);

    public bool Pause() => PlayHandle != 0 && Bass.ChannelPause(PlayHandle);

    public void Restart(double positionBaseSeconds, bool stop)
    {
        if (PlayHandle != 0)
        {
            _ = stop ? Bass.ChannelStop(PlayHandle) : Bass.ChannelPause(PlayHandle);
            _ = Bass.ChannelSetPosition(PlayHandle, 0);
        }

        if (SourceHandle != 0)
            _ = Bass.ChannelSetPosition(SourceHandle, 0);
    }

    // The raw BASS channel position, never reset across a loop wrap (only a restart resets the
    // channel's own position counter).
    public double GetContinuousPosition(double positionBaseSeconds)
    {
        if (PlayHandle == 0)
            return positionBaseSeconds;

        var bytes = Bass.ChannelGetPosition(PlayHandle);
        var seconds = Bass.ChannelBytes2Seconds(PlayHandle, bytes);
        return positionBaseSeconds + (double.IsFinite(seconds) ? Math.Max(0, seconds) : 0);
    }

    public double ToHeardPosition(double seconds)
    {
        if (seconds <= 0 || !double.IsFinite(seconds))
            return seconds;

        // BASS reports latency for the current device, so position sampling selects this engine's device first.
        _ = BassAudioOutputDevices.TrySetCurrentDevice(BassDeviceIndex, "VideoAudioEngine", raiseDiagnostic: false);

        var latency = _outputLatencyProvider.GetOutputLatencySeconds();
        if (latency <= 0 || !double.IsFinite(latency))
            return seconds;

        var corrected = seconds - latency;
        return corrected < 0 ? 0 : corrected;
    }

    /// <summary>True while BASS has parked the channel because its output device was lost (ENG-032).</summary>
    internal bool IsChannelPausedByDevice()
    {
        var playHandle = PlayHandle;
        return playHandle != 0 && (int)Bass.ChannelIsActive(playHandle) == BassOutputWatchdog.ActivePausedDevice;
    }

    /// <summary>Resumes a channel BASS parked for its device; true when there was nothing to do.</summary>
    internal bool ResumeIfPausedByDevice()
    {
        if (PlayHandle == 0 || (int)Bass.ChannelIsActive(PlayHandle) != BassOutputWatchdog.ActivePausedDevice)
            return true;

        // Not restarted: the channel carries on from its position.
        return Bass.ChannelPlay(PlayHandle);
    }

    public void SetVolume(double volume01)
    {
        _volume = Math.Clamp(volume01, 0.0, 1.0);
        _ = ApplyVolume();
    }

    public void SlideVolume(double volume01, TimeSpan duration)
    {
        _volume = Math.Clamp(volume01, 0.0, 1.0);
        if (PlayHandle != 0)
            _ = BassChannelVolume.Slide(PlayHandle, _volume, duration);
    }

    public void SetPitch(int semitones)
    {
        _pitch = Math.Clamp(semitones, -60, 60);
        if (PlayHandle != 0)
            _ = Bass.ChannelSetAttribute(PlayHandle, ChannelAttribute.Pitch, _pitch);
    }

    public void SetTempo(int percent)
    {
        _tempo = percent;
        if (PlayHandle != 0)
            _ = Bass.ChannelSetAttribute(PlayHandle, ChannelAttribute.Tempo, _tempo);
    }

    public bool SetEqualizer(AudioEqualizerSettings settings) => _equalizer.Configure(settings);

    public int BeginFadeSession() => _fade.BeginFadeSession();

    public bool SetFadeGain(int session, double gain, TimeSpan ramp) => _fade.SetFadeGain(session, gain, ramp);

    public void EndFadeSession() => _fade.EndSession(releaseHandle: false);

    public void Dispose() => Release();

    bool ApplyVolume()
    {
        if (PlayHandle == 0)
            return false;

        // Normalization is its own FX stage (it can amplify); the attribute carries user volume only.
        return BassChannelVolume.Set(PlayHandle, _volume);
    }

    public void StartNormalization(string filePath, string? cacheKey, double durationSeconds, bool temporaryCopy)
    {
        CancelNormalizationAnalysis();
        var generation = Volatile.Read(ref _normalizationGeneration);
        if (cacheKey is not null && NormalizationGainCache.TryGetValue(cacheKey, out var cachedGain))
        {
            ApplyNormalizationGain(cachedGain, cacheKey, generation, "Using cached normalization gain", filePath);
            return;
        }

        _normalizationReady = false;
        var cts = new CancellationTokenSource();
        _normalizationCts = cts;
        _normalizationTask = Task.Run(() => RunNormalizationAnalysis(filePath, cacheKey, generation, cts.Token));
    }

    void RunNormalizationAnalysis(string filePath, string? cacheKey, int generation, CancellationToken cancellationToken)
    {
        try
        {
            using var reader = FfmpegAudioReader.Open(filePath);
            if (reader is null)
                return;

            // Film and TV soundtracks often open quietly: the quick pass only attenuates
            // (NormalizationGainPolicy), boosting waits for the deep pass.
            var quickPeak = FfmpegAudioPeakScanner.ScanQuick(reader, cancellationToken);
            ApplyNormalizationGain(
                NormalizationGainPolicy.QuickGain(quickPeak),
                cacheKey: null,
                generation,
                NormalizationGainPolicy.QuickPassHeldBackBoost(quickPeak) ? "Quick normalization complete (boost waits for the full scan)" : "Quick normalization complete",
                filePath);

            var deepPeak = NormalizationGainPolicy.FinalPeak(quickPeak, FfmpegAudioPeakScanner.ScanDeep(reader, cancellationToken));
            ApplyNormalizationGain(
                AudioNormalizer.CalculateGainFromPeak(deepPeak),
                cacheKey,
                generation,
                // A silent or empty audio track (common in background videos) scans to a peak of 0,
                // which leaves the gain at 1.0: say so rather than look like a measured gain.
                deepPeak > 0 ? "Deep normalization complete" : "Deep normalization complete (no audible audio found; gain left at 1.0)",
                filePath);
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

    void ApplyNormalizationGain(double gain, string? cacheKey, int generation, string message, string sourcePath)
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
            NormalizationGainCache.Set(cacheKey, gain);

        DiagnosticRouter.Raise(MediaDiagnosticLevel.Info, "VideoAudioEngine", $"{message}, gain={gain:F3}", sourcePath: sourcePath);
        QueueNormalizationGainChanged(gain, generation);
    }

    /// <summary>
    /// Raises <see cref="NormalizationGainChanged"/> in order on the thread pool. Host handlers
    /// must never run on the analysis thread, which <see cref="Release"/> joins under the engine's lock.
    /// </summary>
    void QueueNormalizationGainChanged(double gain, int generation)
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

    void CancelNormalizationAnalysis()
    {
        _ = Interlocked.Increment(ref _normalizationGeneration);
        var cts = _normalizationCts;
        var task = _normalizationTask;
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
}
