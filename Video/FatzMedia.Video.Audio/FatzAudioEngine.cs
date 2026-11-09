using FatzMedia.Video.Core.Abstractions;
using FatzMedia.Video.Core.Audio;
using FatzMedia.Video.Core.Audio.Fade;
using FatzMedia.Video.Core.Configuration;
using FatzMedia.Video.Core.Files;
using FatzMedia.Video.Core.Threading;
using Fa = global::FatzMedia.Audio;

namespace FatzMedia.Video.Audio;

/// <summary>
/// Plays audio files and archive entries through FatzAudio (037A slice 2), behind the same
/// contract and internal seams as the BASS <see cref="AudioEngine"/>. One FatzAudio engine, output
/// device and mix bus per player.
/// </summary>
/// <remarks>
/// <para>Gain stages: the stream's volume is the fade stage (<see cref="IAudioFadeGainTarget"/>,
/// FatzAudio volume ramps); the player's mix bus carries the user volume. The equalizer is
/// FatzAudio's built-in one, before both.</para>
/// <para>Normalization is FatzAudio's loudness (LUFS) normalization and true-peak limiter, set from
/// <see cref="MediaPlayerConfiguration.Normalization"/>: FatzAudio measures each song (tags, its
/// scan cache, or a scan on all cores), applies the gain on top of every volume and glides to the
/// measured value; nothing here waits for it.</para>
/// <para>Silence trim uses FatzAudio's silence detection at open, so it applies from the first
/// play; the trimmed window becomes the stream's CUE_IN/CUE_OUT, so play, stop, end and loop follow
/// it natively.</para>
/// <para>Events arrive on FatzAudio's event thread; handlers re-enter the player's diagnostic owner.</para>
/// </remarks>
sealed class FatzAudioEngine : IAudioEngine, IAudioLoopControl, IAudioAnalysisOpenControl, IAudioTrimWindowProvider,
    IAudioFadeGainTarget, IAudioFilterHost, IAudioVolumeSlide, IMidiEventReaderSource, IAudioDeviceChangeSource
{
    const string Category = "FatzAudioEngine";

    static readonly Fa.StreamOptions PlaybackOptions = new() { NoCdg = true };

    readonly Fa.AudioEngine _engine;
    readonly Fa.OutputDevice _output;
    readonly Fa.MixBus _bus;
    readonly Fa.SoundFont? _soundFont;
    readonly SilenceTrimOptions _silenceTrim;
    readonly NormalizationOptions _normalization;
    readonly object? _diagnosticOwner;

    // Guards the open stream and everything bound to it; never held while raising events.
    readonly Lock _gate = new();
    Fa.AudioStream? _stream;
    CancellationTokenSource? _streamLifetime;
    ConvertedAudio? _converted;
    int _sampleRate;
    double _duration;
    MediaAnalysisResult? _activeTrimWindow;
    AudioEqualizerSettings _equalizer = AudioEqualizerSettings.Disabled;
    bool _loopPlayback;
    double _userVolume = 1;
    int _fadeSession;
    bool _loudnessReported;
    bool _disposed;

    readonly Lock _stateGate = new();
    AudioEngineState _state = AudioEngineState.Stopped;

    /// <summary>Creates the engine and opens the default output device.</summary>
    /// <param name="configuration">The player's configuration: soundfont, silence trim, normalization, device.</param>
    /// <param name="nullOutput">Plays to FatzAudio's null device (tests, machines without sound).</param>
    internal FatzAudioEngine(MediaPlayerConfiguration configuration, bool nullOutput = false)
    {
        _silenceTrim = configuration.SilenceTrim;
        _normalization = configuration.Normalization;
        _diagnosticOwner = DiagnosticRouter.Owner;
        var cacheDirectory = EnsureCacheDirectory(FatzAudioMappings.ResolveCacheDirectory(_normalization.CacheDirectory));
        _engine = Fa.AudioEngine.Create(new Fa.AudioEngineOptions { NullOutput = nullOutput, CacheDirectory = cacheDirectory });
        try
        {
            // FatzAudio's loudness normalization, with its limiter on while normalization is on.
            _engine.SetLimiter(Fa.LimiterMode.Auto, _normalization.TruePeakCeilingDbtp);
            ApplyNormalizationMode();
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

        DiagnosticRouter.Raise(
            MediaDiagnosticLevel.Info,
            Category,
            $"FatzAudio {Fa.AudioEngine.NativeVersion} ready on the {(nullOutput ? "null" : "default")} output device; MIDI soundfont: {(_soundFont is null ? "system bank" : configuration.MidiSoundfontPath)}; normalization {_normalization.TargetLufs:0.#} LUFS, limiter {_normalization.TruePeakCeilingDbtp:0.#} dBTP, cache: {cacheDirectory ?? "off"}");
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
            ApplyNormalizationMode();
        }
    } = true;

    /// <summary>
    /// FatzAudio's gain for the open song as a multiplier (1 until its loudness is known, or with no
    /// song). With normalization off it is the gain FatzAudio would apply, as the BASS engine reports
    /// its measured gain whatever the switch says.
    /// </summary>
    public double NormalizationGain
    {
        get
        {
            lock (_gate)
            {
                if (_stream is not { } stream)
                    return 1;

                try
                {
                    var loudness = stream.GetLoudness();
                    return FatzAudioMappings.GainFromDecibels(Normalize
                        ? loudness.GainDb
                        : FatzAudioMappings.TrackGainDecibels(loudness.IntegratedLufs, loudness.IsFinal, _normalization.TargetLufs));
                }
                catch (Exception ex) when (ex is Fa.AudioException or ObjectDisposedException)
                {
                    return 1;
                }
            }
        }
    }

    /// <summary>
    /// Always true: FatzAudio starts on its running estimate and glides to the measured loudness,
    /// so a play-on-load has nothing to wait for here (FatzAudio request 12 would hold the start).
    /// </summary>
    public bool IsNormalizationReady => true;

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

    /// <summary>The bus volume (the user volume), for tests.</summary>
    internal float OutputVolume => _bus.Volume;

    /// <summary>The engine is ready from construction (the factory opens the device).</summary>
    public bool Init(int device = -1, int sampleRate = 44100) => !_disposed;

    public bool Open(string filePath) => Open(filePath, analyzeSilence: true);

    bool IAudioAnalysisOpenControl.Open(string filePath, bool analyzeSilence) => Open(filePath, analyzeSilence);

    bool Open(string filePath, bool analyzeSilence)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        return OpenCore(
            OpenedSource.ForFile(filePath),
            filePath,
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
            detectSilence: false,
            archiveEntry);
    }

    bool OpenCore(OpenedSource source, string label, bool detectSilence, IArchiveEntryInfo? archiveEntry = null)
    {
        if (_disposed)
            return false;

        CloseStream();

        Fa.AudioStream? stream = null;
        ConvertedAudio? converted = null;
        try
        {
            try
            {
                stream = source.Open(_engine, PlaybackOptions);
            }
            catch (Fa.AudioException ex) when (ex.ErrorCode == Fa.ErrorCode.UnsupportedFormat
                && (source.Path ?? archiveEntry?.Key) is { } path
                && FfmpegAudioFallback.CanRetryWithFfmpeg(path))
            {
                // A format FatzAudio does not decode (WMA off Windows, DTS, ...): FFmpeg converts it
                // to a float WAV, as for BASS (archive entries through a temp file, as there).
                converted = (source.Path is null && archiveEntry is not null ? TakeArchiveConversion(archiveEntry) : FfmpegAudioFallback.Take(path, MediaOpenCancellation.Current))
                    ?? throw new InvalidOperationException($"FFmpeg could not convert '{path}'.", ex);
                source = converted.IsInMemory
                    ? OpenedSource.ForMemory(converted.InMemoryData, path)
                    : OpenedSource.ForFile(converted.TempPath!);
                stream = source.Open(_engine, PlaybackOptions);
                DiagnosticRouter.Raise(MediaDiagnosticLevel.Info, Category, $"FatzAudio cannot decode '{Path.GetFileName(path)}'; playing an FFmpeg conversion.", sourcePath: path);
            }

            var info = stream.Info;
            var duration = info.Duration?.TotalSeconds ?? 0;
            var trimWindow = detectSilence ? DetectTrimWindow(stream, info, duration, label) : null;

            stream.Ended += OnStreamEnded;
            stream.Looped += OnStreamLooped;
            stream.DeviceChanged += OnStreamDeviceChanged;
            stream.LoudnessFinal += OnStreamLoudnessFinal;
            lock (_gate)
            {
                // From here CloseStream owns the stream and a temp-file conversion. FatzAudio copies
                // memory it opens, so archive bytes and in-memory conversions are not kept.
                var opened = stream;
                _stream = stream;
                _streamLifetime = new CancellationTokenSource();
                _converted = converted is { IsInMemory: false } ? converted : null;
                stream = null;
                converted = null;
                _sampleRate = info.Format.SampleRate;
                _duration = double.IsFinite(duration) ? duration : 0;
                _activeTrimWindow = trimWindow;
                _loudnessReported = false;
                if (trimWindow is not null)
                {
                    try
                    {
                        if (trimWindow.PlayableStart > 0)
                            opened.Markers.CueIn = TimeSpan.FromSeconds(trimWindow.PlayableStart);
                        if (trimWindow.PlayableEnd < duration)
                            opened.Markers.CueOut = TimeSpan.FromSeconds(trimWindow.PlayableEnd);
                        opened.Position = TimeSpan.FromSeconds(trimWindow.PlayableStart);
                    }
                    catch (Exception ex) when (ex is Fa.AudioException or ArgumentException or InvalidOperationException)
                    {
                        DiagnosticRouter.Raise(MediaDiagnosticLevel.Warning, Category, $"Silence trim could not be applied, playing untrimmed: {ex.Message}", sourcePath: label, innerException: ex);
                        _activeTrimWindow = null;
                        try
                        {
                            opened.Markers.CueIn = null;
                            opened.Markers.CueOut = null;
                            opened.Position = TimeSpan.Zero;
                        }
                        catch (Exception resetEx) when (resetEx is Fa.AudioException or ArgumentException or InvalidOperationException)
                        {
                            // Best effort: the open still succeeds.
                        }
                    }
                }

                opened.Loop = _loopPlayback;
                ApplyEqualizerLocked();
                CurrentTempo = 0;
            }

            ApplyOutputVolume(TimeSpan.Zero);
            DiagnosticRouter.Raise(
                MediaDiagnosticLevel.Info,
                Category,
                $"Audio opened: {Path.GetFileName(label)} ({info.Codec}, {info.Format.SampleRate}Hz, {info.Format.Channels}ch, {_duration:F3}s)",
                sourcePath: label);
            Opened?.Invoke(this, EventArgs.Empty);
            SetState(AudioEngineState.Stopped);

            // Tags and the scan cache are final within a millisecond, possibly before the stream
            // became current (its event was then ignored).
            Fa.AudioStream? current;
            lock (_gate)
                current = _stream;
            if (current is not null)
            {
                OnStreamEvent(current, "Loudness", () =>
                {
                    if (current.GetLoudness().IsFinal)
                        ReportLoudness(current);
                });
            }

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

    static ConvertedAudio? TakeArchiveConversion(IArchiveEntryInfo archiveEntry)
    {
        var tempPath = TempMediaFile.CreateFromArchiveEntry(archiveEntry);
        try
        {
            return FfmpegAudioFallback.Take(tempPath, MediaOpenCancellation.Current);
        }
        finally
        {
            TempMediaFile.TryDelete(tempPath);
        }
    }

    /// <summary>
    /// Finds the leading and trailing silence with FatzAudio (it decodes only the edges, so it is
    /// ready before the first play) and applies our padding and edge switches.
    /// </summary>
    MediaAnalysisResult? DetectTrimWindow(Fa.AudioStream stream, Fa.StreamInfo info, double duration, string label)
    {
        if (!double.IsFinite(duration) || duration <= 0)
            return null;

        try
        {
            var silence = stream.DetectSilence(FatzAudioMappings.ToSilenceOptions(_silenceTrim));
            var sampleRate = silence.SampleRate > 0 ? silence.SampleRate : info.Format.SampleRate;
            var edges = FatzAudioMappings.ToEdgeRegions(silence.FirstAudible, silence.LastAudible, silence.Length, sampleRate, duration);
            var result = SilenceTrimCalculator.Calculate(duration, edges, 1.0, _silenceTrim);
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

        CloseStream();
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

    /// <summary>The song's true peak from FatzAudio's loudness measurement (see <see cref="TruePeakAsync"/>).</summary>
    public Task<double> AnalyzePeakQuickAsync(CancellationToken cancellationToken) => TruePeakAsync(cancellationToken);

    /// <summary>The song's true peak from FatzAudio's loudness measurement (see <see cref="TruePeakAsync"/>).</summary>
    public Task<double> AnalyzePeakAsync(CancellationToken cancellationToken) => TruePeakAsync(cancellationToken);

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        CloseStream();
        SetState(AudioEngineState.Stopped);
        _bus.Dispose();
        _output.Dispose();
        _soundFont?.Dispose();
        _engine.Dispose();
    }

    /// <summary>
    /// The open song's peak as a linear level (1 = full scale): FatzAudio's true peak once its
    /// loudness is final (from tags, the cache or a scan; tags give the tagged sample peak). 0 with
    /// no song, for digital silence, when no peak is known, or when the song closes first.
    /// </summary>
    async Task<double> TruePeakAsync(CancellationToken cancellationToken)
    {
        Fa.AudioStream? stream;
        CancellationToken lifetime;
        lock (_gate)
        {
            stream = _stream;
            lifetime = _streamLifetime?.Token ?? CancellationToken.None;
        }

        if (stream is null)
            return 0;

        var final = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnFinal(object? sender, EventArgs e) => final.TrySetResult();
        stream.LoudnessFinal += OnFinal;
        try
        {
            if (!stream.GetLoudness().IsFinal)
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime);
                try
                {
                    await final.Task.WaitAsync(linked.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    return 0;
                }
            }

            var peakDbtp = stream.GetLoudness().TruePeakDbtp;
            return double.IsFinite(peakDbtp) ? Math.Pow(10, peakDbtp / 20) : 0;
        }
        catch (Exception ex) when (ex is Fa.AudioException or ObjectDisposedException)
        {
            return 0;
        }
        finally
        {
            stream.LoudnessFinal -= OnFinal;
        }
    }

    /// <summary>
    /// Creates the cache folder and its parents: FatzAudio creates only the last level, so the default
    /// <c>%LOCALAPPDATA%\FatzMedia.Video\AudioCache</c> was never written on a machine without the
    /// parent folder (owner smoke 2026-11-05). Null (no cache) when it cannot be created.
    /// </summary>
    static string? EnsureCacheDirectory(string? directory)
    {
        if (directory is null)
            return null;

        try
        {
            _ = Directory.CreateDirectory(directory);
            return directory;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Warning, Category, $"Loudness cache folder '{directory}' could not be created ({ex.Message}); every song is measured on each run.", innerException: ex);
            return null;
        }
    }

    /// <summary>FatzAudio's normalization for every stream of this engine: on at the target, or off.</summary>
    void ApplyNormalizationMode()
    {
        if (_disposed)
            return;

        try
        {
            _engine.SetNormalization(Normalize ? Fa.NormalizeMode.Track : Fa.NormalizeMode.Off, _normalization.TargetLufs);
        }
        catch (Exception ex) when (ex is Fa.AudioException or ObjectDisposedException or ArgumentException)
        {
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Warning, Category, $"Normalization could not be set: {ex.Message}", innerException: ex);
        }
    }

    /// <summary>The user volume, on the player's bus (normalization is FatzAudio's, on top of it).</summary>
    void ApplyOutputVolume(TimeSpan ramp)
    {
        if (_disposed)
            return;

        var target = (float)_userVolume;
        try
        {
            if (ramp > TimeSpan.Zero)
                _bus.Ramp(Fa.AudioParameter.Volume, target, ramp);
            else
                _bus.Volume = target;
        }
        catch (Exception ex) when (ex is Fa.AudioException or ObjectDisposedException)
        {
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Warning, Category, $"Volume could not be set: {ex.Message}", innerException: ex);
        }
    }

    bool ApplyEqualizerLocked() =>
        TryLocked(stream => stream.SetEqualizer(FatzAudioMappings.ToEqualizerBands(_equalizer)), "Equalizer");

    void CloseStream()
    {
        Fa.AudioStream? stream;
        ConvertedAudio? converted;
        CancellationTokenSource? lifetime;
        lock (_gate)
        {
            stream = _stream;
            converted = _converted;
            lifetime = _streamLifetime;
            _stream = null;
            _streamLifetime = null;
            _converted = null;
            _duration = 0;
            _sampleRate = 0;
            _activeTrimWindow = null;
            _fadeSession = NextSession(_fadeSession);
        }

        // Ends any peak query waiting on this song (not disposed: a query may still link to it).
        lifetime?.Cancel();
        if (stream is not null)
        {
            stream.Ended -= OnStreamEnded;
            stream.Looped -= OnStreamLooped;
            stream.DeviceChanged -= OnStreamDeviceChanged;
            stream.LoudnessFinal -= OnStreamLoudnessFinal;
            stream.Dispose();
        }

        converted?.Dispose();
    }

    void EndFadeSessionLocked(bool restoreGain)
    {
        _fadeSession = NextSession(_fadeSession);
        if (restoreGain)
            _ = TryLocked(static stream => stream.Volume = 1f, "Fade reset");
    }

    double ReadClockLocked()
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

    bool TryLocked(Action<Fa.AudioStream> action, string operation)
    {
        if (_stream is not { } stream)
            return false;

        try
        {
            action(stream);
            return true;
        }
        catch (Exception ex) when (ex is Fa.AudioException or ObjectDisposedException or InvalidOperationException or ArgumentException)
        {
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Warning, Category, $"{operation} failed: {ex.Message}", innerException: ex);
            return false;
        }
    }

    void SetState(AudioEngineState state)
    {
        lock (_stateGate)
        {
            if (_state == state)
                return;

            _state = state;
        }

        StateChanged?.Invoke(this, state);
    }

    bool IsCurrent(object? sender)
    {
        lock (_gate)
            return sender is not null && ReferenceEquals(sender, _stream);
    }

    // FatzAudio's event thread: re-enter the player's diagnostics, and never let a subscriber's
    // exception reach the native side.
    void OnStreamEnded(object? sender, EventArgs e) => OnStreamEvent(sender, "End", () =>
    {
        double end;
        lock (_gate)
        {
            // Played again (or seeked) before this event arrived: the pass it reports is gone.
            if (sender is Fa.AudioStream { State: Fa.StreamState.Playing or Fa.StreamState.Paused })
                return;

            // A fade that ran to the end must not leave a replay muted.
            EndFadeSessionLocked(restoreGain: true);
            end = _activeTrimWindow?.PlayableEnd ?? _duration;
        }

        SetState(AudioEngineState.Stopped);
        PositionChanged?.Invoke(this, end);
        PlaybackEnded?.Invoke(this, EventArgs.Empty);
    });

    // One event per wrap, raised as the BASS engine does: PlaybackEnded while still Playing.
    void OnStreamLooped(object? sender, Fa.StreamEventArgs e) => OnStreamEvent(sender, "Loop", () =>
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

    void OnStreamDeviceChanged(object? sender, Fa.StreamEventArgs e) => OnStreamEvent(sender, "Device change", () =>
    {
        string? deviceName = null;
        try
        {
            deviceName = _engine.OutputDevices.FirstOrDefault(static device => device.IsDefault)?.Name;
        }
        catch (Exception ex) when (ex is Fa.AudioException or ObjectDisposedException)
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

    void OnStreamLoudnessFinal(object? sender, EventArgs e) => OnStreamEvent(sender, "Loudness", () =>
    {
        if (sender is Fa.AudioStream stream)
            ReportLoudness(stream);
    });

    /// <summary>Logs the song's measured loudness and raises <see cref="NormalizationGainChanged"/>, once per open.</summary>
    void ReportLoudness(Fa.AudioStream stream)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(stream, _stream) || _loudnessReported)
                return;

            _loudnessReported = true;
        }

        var loudness = stream.GetLoudness();
        DiagnosticRouter.Raise(
            MediaDiagnosticLevel.Info,
            Category,
            $"Loudness {loudness.IntegratedLufs:F1} LUFS, true peak {loudness.TruePeakDbtp:F1} dBTP ({loudness.Source}); normalization gain {20 * Math.Log10(NormalizationGain):+0.0;-0.0;0.0} dB{(Normalize ? string.Empty : " (normalization off)")}");
        NormalizationGainChanged?.Invoke(this, NormalizationGain);
    }

    void OnStreamEvent(object? sender, string what, Action handle)
    {
        using var owner = DiagnosticRouter.EnterOwner(_diagnosticOwner);
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

    static int NextSession(int session) => session == int.MaxValue ? 1 : session + 1;

    /// <summary>Where the open media comes from, so a second (decode-only) stream can be opened for analysis.</summary>
    sealed class OpenedSource
    {
        readonly ReadOnlyMemory<byte> _data;

        OpenedSource(string? path, ReadOnlyMemory<byte> data, string label)
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
