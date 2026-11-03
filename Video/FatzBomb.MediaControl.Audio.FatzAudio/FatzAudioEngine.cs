using FatzBomb.MediaControl.Core.Abstractions;
using FatzBomb.MediaControl.Core.Audio;
using FatzBomb.MediaControl.Core.Audio.Fade;
using FatzBomb.MediaControl.Core.Configuration;
using FatzBomb.MediaControl.Core.Threading;
using Fa = global::FatzAudio;

namespace FatzBomb.MediaControl.Audio.FatzAudio;

/// <summary>
/// Plays audio files and archive entries through FatzAudio (037A slice 2), behind the same
/// contract and internal seams as the BASS <see cref="AudioEngine"/>. One FatzAudio engine, output
/// device and mix bus per player.
/// </summary>
/// <remarks>
/// <para>Gain stages: the stream's volume is the fade stage (<see cref="IAudioFadeGainTarget"/>,
/// FatzAudio volume ramps); the player's mix bus carries the user volume times the normalization
/// gain. The equalizer is FatzAudio's built-in one, before both.</para>
/// <para>Normalization keeps MediaControl's peak rule (quick then deep pass, the same gains and
/// gain cache as BASS), measured on a FatzAudio decode-only stream; FatzAudio's own loudness
/// normalization is off.</para>
/// <para>Silence trim uses FatzAudio's silence detection at open, so it applies from the first
/// play; the trimmed window becomes the stream's CUE_IN/CUE_OUT, so play, stop, end and loop follow
/// it natively.</para>
/// <para>Events arrive on FatzAudio's event thread; handlers re-enter the player's diagnostic owner.</para>
/// </remarks>
internal sealed class FatzAudioEngine : IAudioEngine, IAudioLoopControl, IAudioAnalysisOpenControl, IAudioTrimWindowProvider,
    IAudioFadeGainTarget, IAudioFilterHost, IAudioVolumeSlide, IMidiEventReaderSource, IAudioDeviceChangeSource
{
    private const string Category = "FatzAudioEngine";

    private static readonly Fa.StreamOptions PlaybackOptions = new() { NoCdg = true, NoNormalize = true };
    private static readonly Fa.StreamOptions DecodeOptions = new() { DecodeOnly = true, NoCdg = true, NoNormalize = true };

    private readonly Fa.AudioEngine _engine;
    private readonly Fa.OutputDevice _output;
    private readonly Fa.MixBus _bus;
    private readonly Fa.SoundFont? _soundFont;
    private readonly SilenceTrimOptions _silenceTrim;
    private readonly AudioNormalizer _normalizer = new();
    private readonly object? _diagnosticOwner;

    // Guards the open stream and everything bound to it; never held while raising events.
    private readonly Lock _gate = new();
    private Fa.AudioStream? _stream;
    private OpenedSource? _source;
    private ConvertedAudio? _converted;
    private int _sampleRate;
    private double _duration;
    private MediaAnalysisResult? _activeTrimWindow;
    private AudioEqualizerSettings _equalizer = AudioEqualizerSettings.Disabled;
    private bool _loopPlayback;
    private double _userVolume = 1;
    private int _fadeSession;
    private bool _disposed;

    private readonly Lock _stateGate = new();
    private AudioEngineState _state = AudioEngineState.Stopped;

    private readonly Lock _analysisGate = new();
    private CancellationTokenSource? _analysisCts;
    private Task? _analysisTask;
    private int _analysisGeneration;
    private volatile bool _normalizationReady = true;

    /// <summary>Creates the engine and opens the default output device.</summary>
    /// <param name="configuration">The player's configuration: soundfont, silence trim, device.</param>
    /// <param name="nullOutput">Plays to FatzAudio's null device (tests, machines without sound).</param>
    internal FatzAudioEngine(MediaPlayerConfiguration configuration, bool nullOutput = false)
    {
        _silenceTrim = configuration.SilenceTrim;
        _diagnosticOwner = DiagnosticRouter.Owner;
        _engine = Fa.AudioEngine.Create(new Fa.AudioEngineOptions { NullOutput = nullOutput });
        try
        {
            // Normalization is ours (peak rule, applied on the bus), not FatzAudio's loudness.
            _engine.SetNormalization(Fa.NormalizeMode.Off);
            if (!string.IsNullOrWhiteSpace(configuration.MidiSoundfontPath))
            {
                _soundFont = Fa.SoundFont.FromFile(configuration.MidiSoundfontPath);
                _engine.DefaultSoundFont = _soundFont;
            }

            if (configuration.AudioOutputDeviceIndex >= 0)
            {
                DiagnosticRouter.Raise(
                    MediaDiagnosticLevel.Warning,
                    Category,
                    $"Audio output device {configuration.AudioOutputDeviceIndex} is a BASS device index; the FatzAudio engine plays on the system default device and follows it.");
            }

            // The default device: FatzAudio follows default-device changes and reopens on the
            // default when the device goes away.
            _output = _engine.OpenOutput();
            _bus = _output.CreateBus();
        }
        catch
        {
            _soundFont?.Dispose();
            _engine.Dispose();
            throw;
        }

        _normalizer.NormalizationGainChanged += (_, gain) =>
        {
            ApplyOutputVolume(TimeSpan.Zero);
            NormalizationGainChanged?.Invoke(this, gain);
        };

        DiagnosticRouter.Raise(
            MediaDiagnosticLevel.Info,
            Category,
            $"FatzAudio {Fa.AudioEngine.NativeVersion} ready on the {(nullOutput ? "null" : "default")} output device; MIDI soundfont: {(_soundFont is null ? "system bank" : configuration.MidiSoundfontPath)}");
    }

    public event EventHandler<AudioEngineState>? StateChanged;
    public event EventHandler? Opened;
    public event EventHandler? Closed;
    public event EventHandler<double>? PositionChanged;
    public event EventHandler? PlaybackEnded;
    public event EventHandler<double>? NormalizationGainChanged;
    public event EventHandler<AudioDeviceChangeInfo>? DeviceChanged;

    public AudioEngineState State
    {
        get
        {
            lock (_stateGate)
                return _state;
        }
    }

    /// <summary>BASS-specific: always 0.</summary>
    public int PlayHandle => 0;

    /// <summary>BASS-specific: always 0.</summary>
    public int SourceHandle => 0;

    public bool Normalize
    {
        get;
        set
        {
            if (field == value)
                return;

            field = value;
            ApplyOutputVolume(TimeSpan.Zero);
        }
    } = true;

    public double NormalizationGain => _normalizer.NormalizationGain;

    public bool IsNormalizationReady => _normalizationReady;

    public int CurrentTempo { get; private set; }

    public double Duration
    {
        get
        {
            lock (_gate)
                return _duration;
        }
    }

    /// <summary>The source position heard now: FatzAudio's presentation clock (device latency included).</summary>
    public double Position
    {
        get
        {
            lock (_gate)
                return ReadClockLocked();
        }
        set
        {
            double before;
            double after;
            lock (_gate)
            {
                if (_stream is not { } stream)
                    return;

                before = ReadClockLocked();
                var target = Math.Max(0, double.IsFinite(value) ? value : 0);
                if (_duration > 0)
                    target = Math.Min(target, _duration);
                try
                {
                    stream.Position = TimeSpan.FromSeconds(target);
                }
                catch (Exception ex)
                {
                    DiagnosticRouter.Raise(MediaDiagnosticLevel.Warning, Category, $"Seek to {target:F3}s failed: {ex.Message}", innerException: ex);
                    return;
                }

                after = ReadClockLocked();
            }

            if (Math.Abs(after - before) > 0.001)
                PositionChanged?.Invoke(this, after);
        }
    }

    bool IAudioLoopControl.LoopPlayback
    {
        get
        {
            lock (_gate)
                return _loopPlayback;
        }
        set
        {
            lock (_gate)
            {
                _loopPlayback = value;
                TryLocked(stream => stream.Loop = value, "Loop");
            }
        }
    }

    MediaAnalysisResult? IAudioTrimWindowProvider.ActiveTrimWindow
    {
        get
        {
            lock (_gate)
                return _activeTrimWindow;
        }
    }

    AudioEqualizerSettings IAudioFilterHost.Equalizer
    {
        get
        {
            lock (_gate)
                return _equalizer;
        }
    }

    /// <summary>The open stream, for tests.</summary>
    internal Fa.AudioStream? CurrentStream
    {
        get
        {
            lock (_gate)
                return _stream;
        }
    }

    /// <summary>The bus volume (user volume times the normalization gain), for tests.</summary>
    internal float OutputVolume => _bus.Volume;

    /// <summary>The engine is ready from construction (the factory opens the device).</summary>
    public bool Init(int device = -1, int sampleRate = 44100) => !_disposed;

    public bool Open(string filePath) => Open(filePath, analyzeSilence: true);

    bool IAudioAnalysisOpenControl.Open(string filePath, bool analyzeSilence) => Open(filePath, analyzeSilence);

    private bool Open(string filePath, bool analyzeSilence)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        return OpenCore(
            OpenedSource.ForFile(filePath),
            filePath,
            AudioEngine.CreatePathNormalizationCacheKey(filePath),
            "file",
            analyzeSilence && _silenceTrim.Enabled && !AudioStreamManager.IsTrackerFormat(filePath));
    }

    public bool Open(IArchiveEntryInfo archiveEntry)
    {
        ArgumentNullException.ThrowIfNull(archiveEntry);
        if (_disposed)
            return false;

        byte[] data;
        int length;
        try
        {
            (data, length) = AudioStreamManager.ReadArchiveEntry(archiveEntry);
        }
        catch (Exception ex)
        {
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Error, Category, $"Open failed for archive entry '{archiveEntry.Key}': {ex.Message}", sourcePath: archiveEntry.Key, innerException: ex);
            return false;
        }

        // Archive entries are never trimmed, as on BASS.
        return OpenCore(
            OpenedSource.ForMemory(data.AsMemory(0, length), archiveEntry.Key),
            archiveEntry.Key,
            AudioEngine.CreateArchiveNormalizationCacheKey(archiveEntry),
            "archive",
            detectSilence: false);
    }

    private bool OpenCore(OpenedSource source, string label, string normalizationKey, string sourceLabel, bool detectSilence)
    {
        if (_disposed)
            return false;

        CancelNormalizationAnalysis(join: false);
        CloseStream();

        Fa.AudioStream? stream = null;
        ConvertedAudio? converted = null;
        try
        {
            try
            {
                stream = source.Open(_engine, PlaybackOptions);
            }
            catch (Fa.FatzAudioException ex) when (ex.ErrorCode == Fa.ErrorCode.UnsupportedFormat
                && source.Path is { } path
                && FfmpegAudioFallback.CanRetryWithFfmpeg(path))
            {
                // A format FatzAudio does not decode (WMA off Windows, DTS, ...): FFmpeg converts it
                // to a float WAV, as for BASS.
                converted = FfmpegAudioFallback.Take(path, MediaOpenCancellation.Current)
                    ?? throw new InvalidOperationException($"FFmpeg could not convert '{path}'.", ex);
                source = converted.IsInMemory
                    ? OpenedSource.ForMemory(converted.InMemoryData, path)
                    : OpenedSource.ForFile(converted.TempPath!);
                stream = source.Open(_engine, PlaybackOptions);
                DiagnosticRouter.Raise(MediaDiagnosticLevel.Info, Category, $"FatzAudio cannot decode '{Path.GetFileName(path)}'; playing an FFmpeg conversion.", sourcePath: path);
            }

            Fa.StreamInfo info = stream.Info;
            var duration = info.Duration?.TotalSeconds ?? 0;
            MediaAnalysisResult? trimWindow = detectSilence ? DetectTrimWindow(stream, info, duration, label) : null;

            stream.Ended += OnStreamEnded;
            stream.Looped += OnStreamLooped;
            stream.DeviceChanged += OnStreamDeviceChanged;
            lock (_gate)
            {
                // From here CloseStream owns the stream and the conversion.
                Fa.AudioStream opened = stream;
                _stream = stream;
                _source = source;
                _converted = converted;
                stream = null;
                converted = null;
                _sampleRate = info.Format.SampleRate;
                _duration = double.IsFinite(duration) ? duration : 0;
                _activeTrimWindow = trimWindow;
                if (trimWindow is not null)
                {
                    if (trimWindow.PlayableStart > 0)
                        opened.Markers.CueIn = TimeSpan.FromSeconds(trimWindow.PlayableStart);
                    if (trimWindow.PlayableEnd < duration)
                        opened.Markers.CueOut = TimeSpan.FromSeconds(trimWindow.PlayableEnd);
                    opened.Position = TimeSpan.FromSeconds(trimWindow.PlayableStart);
                }

                opened.Loop = _loopPlayback;
                ApplyEqualizerLocked();
                CurrentTempo = 0;
            }

            _normalizer.ResetGain(clearFxHandle: true);
            ApplyOutputVolume(TimeSpan.Zero);
            DiagnosticRouter.Raise(
                MediaDiagnosticLevel.Info,
                Category,
                $"Audio opened: {Path.GetFileName(label)} ({info.Codec}, {info.Format.SampleRate}Hz, {info.Format.Channels}ch, {_duration:F3}s)",
                sourcePath: label);
            StartNormalizationAnalysis(normalizationKey, sourceLabel);
            Opened?.Invoke(this, EventArgs.Empty);
            SetState(AudioEngineState.Stopped);
            return true;
        }
        catch (Exception ex)
        {
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Error, Category, $"Open failed for '{label}': {ex.Message}", sourcePath: label, innerException: ex);
            stream?.Dispose();
            converted?.Dispose();
            CloseStream();
            return false;
        }
    }

    /// <summary>
    /// Finds the leading and trailing silence with FatzAudio (it decodes only the edges, so it is
    /// ready before the first play) and applies our padding and edge switches.
    /// </summary>
    private MediaAnalysisResult? DetectTrimWindow(Fa.AudioStream stream, Fa.StreamInfo info, double duration, string label)
    {
        if (!double.IsFinite(duration) || duration <= 0)
            return null;

        try
        {
            Fa.SilenceInfo silence = stream.DetectSilence(FatzAudioMappings.ToSilenceOptions(_silenceTrim));
            var sampleRate = silence.SampleRate > 0 ? silence.SampleRate : info.Format.SampleRate;
            IReadOnlyList<SilenceRegion> edges = FatzAudioMappings.ToEdgeRegions(silence.FirstAudible, silence.LastAudible, silence.Length, sampleRate, duration);
            MediaAnalysisResult result = SilenceTrimCalculator.Calculate(duration, edges, 1.0, _silenceTrim);
            DiagnosticRouter.Raise(
                MediaDiagnosticLevel.Info,
                Category,
                $"Silence detection complete: first audible {silence.FirstAudibleTime?.TotalSeconds.ToString("F3") ?? "none"}s, last audible {silence.LastAudibleTime?.TotalSeconds.ToString("F3") ?? "none"}s, playable {result.PlayableStart:F3}-{result.PlayableEnd:F3}s ({silence.Mode} {silence.ThresholdDb:F0} dBFS{(silence.FromCache ? ", cached" : string.Empty)})",
                sourcePath: label);
            return MediaAnalysisResultValidator.IsValid(result) ? result : null;
        }
        catch (Exception ex)
        {
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Warning, Category, $"Silence detection failed, playing untrimmed: {ex.Message}", sourcePath: label, innerException: ex);
            return null;
        }
    }

    public bool Play()
    {
        lock (_gate)
        {
            if (_stream is not { } stream || _activeTrimWindow is { EffectiveDuration: <= 0 })
                return false;

            try
            {
                // The stream joins the player's bus at its first play; later plays resume it.
                if (stream.Device is null)
                    stream.PlayOn(_bus);
                else
                    stream.Play();
            }
            catch (Exception ex)
            {
                DiagnosticRouter.Raise(MediaDiagnosticLevel.Error, Category, $"Play failed: {ex.Message}", innerException: ex);
                return false;
            }
        }

        SetState(AudioEngineState.Playing);
        return true;
    }

    public bool Pause()
    {
        lock (_gate)
        {
            if (!TryLocked(static stream => stream.Pause(), "Pause"))
                return false;
        }

        SetState(AudioEngineState.Paused);
        return true;
    }

    public bool Stop()
    {
        lock (_gate)
        {
            // Rewinds to CUE_IN (the trimmed start) or the start.
            if (!TryLocked(static stream => stream.Stop(), "Stop"))
                return false;

            EndFadeSessionLocked(restoreGain: true);
        }

        SetState(AudioEngineState.Stopped);
        return true;
    }

    public void Close()
    {
        if (_disposed)
            return;

        CancelNormalizationAnalysis(join: false);
        CloseStream();
        _normalizer.ResetGain(clearFxHandle: true);
        Closed?.Invoke(this, EventArgs.Empty);
        SetState(AudioEngineState.Stopped);
    }

    public void SetVolume(double volume01)
    {
        _userVolume = Math.Clamp(double.IsFinite(volume01) ? volume01 : 0, 0, 1);
        ApplyOutputVolume(TimeSpan.Zero);
    }

    void IAudioVolumeSlide.SlideVolume(double volume01, TimeSpan duration)
    {
        _userVolume = Math.Clamp(double.IsFinite(volume01) ? volume01 : 0, 0, 1);
        ApplyOutputVolume(duration);
    }

    public void SetPitch(int semitones)
    {
        lock (_gate)
            _ = TryLocked(stream => stream.Pitch = Math.Clamp(semitones, -60, 60), "Pitch");
    }

    public void SetTempo(int percent)
    {
        lock (_gate)
        {
            if (_stream is null)
                return;

            percent = Math.Clamp(percent, -95, 5000);
            CurrentTempo = percent;
            _ = TryLocked(stream => stream.Tempo = FatzAudioMappings.ToTempo(percent), "Tempo");
        }
    }

    bool IAudioFilterHost.SetEqualizer(AudioEqualizerSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (_gate)
        {
            _equalizer = settings;
            return _stream is null || ApplyEqualizerLocked();
        }
    }

    int IAudioFadeGainTarget.BeginFadeSession()
    {
        lock (_gate)
        {
            if (_stream is null)
                return 0;

            _fadeSession = NextSession(_fadeSession);
            return _fadeSession;
        }
    }

    bool IAudioFadeGainTarget.SetFadeGain(int session, double gain, TimeSpan ramp)
    {
        lock (_gate)
        {
            if (session == 0 || session != _fadeSession || _stream is null)
                return false;

            var target = (float)(double.IsFinite(gain) ? Math.Clamp(gain, 0, 1) : 0);
            return ramp > TimeSpan.Zero
                ? TryLocked(stream => stream.Ramp(Fa.AudioParameter.Volume, target, ramp), "Fade")
                : TryLocked(stream => stream.Volume = target, "Fade");
        }
    }

    IMidiEventReader? IMidiEventReaderSource.GetMidiEventReader()
    {
        lock (_gate)
        {
            if (_stream is not { } stream)
                return null;

            try
            {
                return FatzAudioMidiEventReader.TryCreate(stream, _duration);
            }
            catch (Exception ex)
            {
                DiagnosticRouter.Raise(MediaDiagnosticLevel.Warning, Category, $"MIDI events could not be read: {ex.Message}", innerException: ex);
                return null;
            }
        }
    }

    public async Task<double> AnalyzePeakQuickAsync(CancellationToken cancellationToken) =>
        await AnalyzeCurrentAsync(level => _normalizer.AnalyzePeakQuickAsync(level, cancellationToken: cancellationToken)).ConfigureAwait(false);

    public async Task<double> AnalyzePeakAsync(CancellationToken cancellationToken) =>
        await AnalyzeCurrentAsync(level => _normalizer.AnalyzePeakAsync(level, cancellationToken: cancellationToken)).ConfigureAwait(false);

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        CancelNormalizationAnalysis(join: true);
        CloseStream();
        SetState(AudioEngineState.Stopped);
        _bus.Dispose();
        _output.Dispose();
        _soundFont?.Dispose();
        _engine.Dispose();
    }

    private async Task<double> AnalyzeCurrentAsync(Func<IAudioLevelSource, Task<double>> analyze)
    {
        OpenedSource? source;
        lock (_gate)
            source = _source;
        if (source is null)
            return 0;

        using FatzAudioLevelSource level = new(source.Open(_engine, DecodeOptions));
        return await analyze(level).ConfigureAwait(false);
    }

    private void StartNormalizationAnalysis(string cacheKey, string sourceLabel)
    {
        if (NormalizationGainCache.Shared.TryGetValue(cacheKey, out var cachedGain))
        {
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Info, Category, $"Using cached normalization gain {cachedGain:F3} ({sourceLabel})");
            _normalizationReady = true;
            _normalizer.SetNormalizationGain(cachedGain, forceNotify: true);
            return;
        }

        OpenedSource? source;
        lock (_gate)
            source = _source;
        if (source is null)
        {
            _normalizationReady = true;
            return;
        }

        _normalizationReady = false;
        var generation = Interlocked.Increment(ref _analysisGeneration);
        var cts = new CancellationTokenSource();
        lock (_analysisGate)
        {
            _analysisCts = cts;
            _analysisTask = Task.Run(() => RunNormalizationAnalysisAsync(source, cacheKey, sourceLabel, generation, cts.Token));
        }
    }

    /// <summary>The BASS engine's two passes and gain policy, on a FatzAudio decode-only stream.</summary>
    private async Task RunNormalizationAnalysisAsync(OpenedSource source, string cacheKey, string sourceLabel, int generation, CancellationToken cancellationToken)
    {
        using DiagnosticRouter.OwnerScope owner = DiagnosticRouter.EnterOwner(_diagnosticOwner);
        try
        {
            using FatzAudioLevelSource level = new(source.Open(_engine, DecodeOptions));
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Info, Category, $"Quick normalization analysis starting ({sourceLabel})");
            var quickPeak = await _normalizer.AnalyzePeakQuickAsync(level, updateGain: false, cancellationToken: cancellationToken).ConfigureAwait(false);
            ApplyGainIfCurrent(
                NormalizationGainPolicy.QuickGain(quickPeak),
                cacheKey: null,
                NormalizationGainPolicy.QuickPassHeldBackBoost(quickPeak) ? $"Quick normalization complete (boost waits for the full scan) ({sourceLabel})" : $"Quick normalization complete ({sourceLabel})",
                generation,
                cancellationToken);

            DiagnosticRouter.Raise(MediaDiagnosticLevel.Info, Category, $"Deep normalization analysis starting ({sourceLabel})");
            var deepPeak = await _normalizer.AnalyzePeakAsync(level, updateGain: false, cancellationToken: cancellationToken).ConfigureAwait(false);
            ApplyGainIfCurrent(
                AudioNormalizer.CalculateGainFromPeak(NormalizationGainPolicy.FinalPeak(quickPeak, deepPeak)),
                cacheKey,
                $"Deep normalization complete ({sourceLabel})",
                generation,
                cancellationToken);
        }
        catch (Exception ex) when (ex is OperationCanceledException || cancellationToken.IsCancellationRequested)
        {
            // Cancelled by an open, close or dispose; its source may already be gone.
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Info, Category, $"Normalization analysis cancelled ({sourceLabel})");
        }
        catch (Exception ex)
        {
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Error, Category, $"Normalization analysis failed ({sourceLabel}): {ex.Message}", innerException: ex);
        }
        finally
        {
            if (IsCurrentAnalysis(generation, CancellationToken.None))
                _normalizationReady = true;
        }
    }

    private void ApplyGainIfCurrent(double gain, string? cacheKey, string message, int generation, CancellationToken cancellationToken)
    {
        if (!IsCurrentAnalysis(generation, cancellationToken))
            return;

        _normalizer.SetNormalizationGain(gain, forceNotify: true);
        _normalizationReady = true;
        if (cacheKey is not null)
            NormalizationGainCache.Shared.Set(cacheKey, gain);
        DiagnosticRouter.Raise(MediaDiagnosticLevel.Info, Category, $"{message}, gain={gain:F3}");
    }

    private bool IsCurrentAnalysis(int generation, CancellationToken cancellationToken) =>
        !_disposed && !cancellationToken.IsCancellationRequested && Volatile.Read(ref _analysisGeneration) == generation;

    /// <summary>
    /// Cancels the running analysis. It reads its own decode stream, so an open or close need not
    /// wait for it; disposal joins it before the native engine goes.
    /// </summary>
    private void CancelNormalizationAnalysis(bool join)
    {
        _ = Interlocked.Increment(ref _analysisGeneration);
        CancellationTokenSource? cts;
        Task? task;
        lock (_analysisGate)
        {
            cts = _analysisCts;
            task = _analysisTask;
            _analysisCts = null;
            _analysisTask = null;
        }

        cts?.Cancel();
        if (join && task is not null)
        {
            try
            {
                _ = task.Wait(TimeSpan.FromSeconds(2));
            }
            catch (AggregateException)
            {
                // Reported by the analysis itself.
            }
        }

        if (join || task is null || task.IsCompleted)
            cts?.Dispose();
        _normalizationReady = true;
    }

    /// <summary>User volume times the normalization gain (when on), on the player's bus.</summary>
    private void ApplyOutputVolume(TimeSpan ramp)
    {
        if (_disposed)
            return;

        var target = (float)(_userVolume * (Normalize ? _normalizer.NormalizationGain : 1.0));
        try
        {
            if (ramp > TimeSpan.Zero)
                _bus.Ramp(Fa.AudioParameter.Volume, target, ramp);
            else
                _bus.Volume = target;
        }
        catch (Exception ex) when (ex is Fa.FatzAudioException or ObjectDisposedException)
        {
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Warning, Category, $"Volume could not be set: {ex.Message}", innerException: ex);
        }
    }

    private bool ApplyEqualizerLocked() =>
        TryLocked(stream => stream.SetEqualizer(FatzAudioMappings.ToEqualizerBands(_equalizer)), "Equalizer");

    private void CloseStream()
    {
        Fa.AudioStream? stream;
        ConvertedAudio? converted;
        lock (_gate)
        {
            stream = _stream;
            converted = _converted;
            _stream = null;
            _source = null;
            _converted = null;
            _duration = 0;
            _sampleRate = 0;
            _activeTrimWindow = null;
            _fadeSession = NextSession(_fadeSession);
        }

        if (stream is not null)
        {
            stream.Ended -= OnStreamEnded;
            stream.Looped -= OnStreamLooped;
            stream.DeviceChanged -= OnStreamDeviceChanged;
            stream.Dispose();
        }

        converted?.Dispose();
    }

    private void EndFadeSessionLocked(bool restoreGain)
    {
        _fadeSession = NextSession(_fadeSession);
        if (restoreGain)
            _ = TryLocked(static stream => stream.Volume = 1f, "Fade reset");
    }

    private double ReadClockLocked()
    {
        if (_stream is not { } stream)
            return 0;

        try
        {
            return stream.Clock.TotalSeconds;
        }
        catch (ObjectDisposedException)
        {
            return 0;
        }
    }

    private bool TryLocked(Action<Fa.AudioStream> action, string operation)
    {
        if (_stream is not { } stream)
            return false;

        try
        {
            action(stream);
            return true;
        }
        catch (Exception ex) when (ex is Fa.FatzAudioException or ObjectDisposedException or InvalidOperationException or ArgumentException)
        {
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Warning, Category, $"{operation} failed: {ex.Message}", innerException: ex);
            return false;
        }
    }

    private void SetState(AudioEngineState state)
    {
        lock (_stateGate)
        {
            if (_state == state)
                return;

            _state = state;
        }

        StateChanged?.Invoke(this, state);
    }

    private bool IsCurrent(object? sender)
    {
        lock (_gate)
            return sender is not null && ReferenceEquals(sender, _stream);
    }

    // FatzAudio's event thread: re-enter the player's diagnostics, and never let a subscriber's
    // exception reach the native side.
    private void OnStreamEnded(object? sender, EventArgs e) => OnStreamEvent(sender, "End", () =>
    {
        double end;
        lock (_gate)
        {
            // A fade that ran to the end must not leave a replay muted.
            EndFadeSessionLocked(restoreGain: true);
            end = _activeTrimWindow?.PlayableEnd ?? _duration;
        }

        SetState(AudioEngineState.Stopped);
        PositionChanged?.Invoke(this, end);
        PlaybackEnded?.Invoke(this, EventArgs.Empty);
    });

    // One event per wrap, raised as the BASS engine does: PlaybackEnded while still Playing.
    private void OnStreamLooped(object? sender, Fa.StreamEventArgs e) => OnStreamEvent(sender, "Loop", () =>
    {
        double start;
        lock (_gate)
        {
            EndFadeSessionLocked(restoreGain: true);
            start = _sampleRate > 0 ? e.Value / (double)_sampleRate : _activeTrimWindow?.PlayableStart ?? 0;
        }

        SetState(AudioEngineState.Playing);
        PositionChanged?.Invoke(this, start);
        PlaybackEnded?.Invoke(this, EventArgs.Empty);
    });

    private void OnStreamDeviceChanged(object? sender, Fa.StreamEventArgs e) => OnStreamEvent(sender, "Device change", () =>
    {
        string? deviceName = null;
        try
        {
            deviceName = _engine.OutputDevices.FirstOrDefault(static device => device.IsDefault)?.Name;
        }
        catch (Exception ex) when (ex is Fa.FatzAudioException or ObjectDisposedException)
        {
            // The name is only for the report.
        }

        DiagnosticRouter.Raise(
            MediaDiagnosticLevel.Info,
            Category,
            e.Value == 0
                ? $"FatzAudio moved the output to the new default device {deviceName ?? "(unknown)"}."
                : $"The output device went away; FatzAudio reopened the output on the default device {deviceName ?? "(unknown)"}.");
        DeviceChanged?.Invoke(this, new AudioDeviceChangeInfo(FatzAudioMappings.ToDeviceChangeKind(e.Value), null, deviceName, null, 0));
    });

    private void OnStreamEvent(object? sender, string what, Action handle)
    {
        using DiagnosticRouter.OwnerScope owner = DiagnosticRouter.EnterOwner(_diagnosticOwner);
        try
        {
            if (!_disposed && IsCurrent(sender))
                handle();
        }
        catch (Exception ex)
        {
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Error, Category, $"{what} handling failed: {ex.Message}", innerException: ex);
        }
    }

    private static int NextSession(int session) => session == int.MaxValue ? 1 : session + 1;

    /// <summary>Where the open media comes from, so a second (decode-only) stream can be opened for analysis.</summary>
    private sealed class OpenedSource
    {
        private readonly ReadOnlyMemory<byte> _data;

        private OpenedSource(string? path, ReadOnlyMemory<byte> data, string label)
        {
            Path = path;
            _data = data;
            Label = label;
        }

        /// <summary>The file on disk, or null for memory.</summary>
        public string? Path { get; }

        public string Label { get; }

        public static OpenedSource ForFile(string path) => new(path, ReadOnlyMemory<byte>.Empty, path);

        public static OpenedSource ForMemory(ReadOnlyMemory<byte> data, string label) => new(null, data, label);

        public Fa.AudioStream Open(Fa.AudioEngine engine, Fa.StreamOptions options) =>
            Path is not null ? engine.OpenFile(Path, options) : engine.OpenMemory(_data, options);
    }
}
