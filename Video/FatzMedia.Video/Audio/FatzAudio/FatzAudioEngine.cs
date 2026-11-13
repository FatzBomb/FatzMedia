using System.Diagnostics;
using System.Globalization;
using FatzMedia.Video.Core.Abstractions;
using FatzMedia.Video.Core.Audio;
using FatzMedia.Video.Core.Audio.Fade;
using FatzMedia.Video.Core.Cdg;
using FatzMedia.Video.Core.Configuration;
using FatzMedia.Video.Core.Files;
using FatzMedia.Video.Core.Threading;
using Fa = global::FatzMedia.Audio;

namespace FatzMedia.Video.Audio;

/// <summary>
/// Plays audio files and archive entries through FatzAudio (037A), the player's audio engine. One
/// FatzAudio engine, output device and mix bus per player.
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
sealed partial class FatzAudioEngine : IAudioEngine, IAudioLoopControl, IAudioAnalysisOpenControl, IAudioTrimWindowProvider,
    IAudioFadeGainTarget, IAudioFilterHost, IAudioVolumeSlide, IAudioDeviceChangeSource,
    ICdgEngineSource, IPushAudioOutputSource
{
    const string Category = "FatzAudioEngine";

    static readonly Fa.StreamOptions PlaybackOptions = new() { NoCdg = true };

    readonly Fa.AudioEngine _engine;
    /// <summary>The output device (measurements record what it plays).</summary>
    internal Fa.OutputDevice OutputDevice { get; }
    readonly Fa.MixBus _bus;
    readonly Fa.SoundFont? _soundFont;
    readonly SilenceTrimOptions _silenceTrim;
    readonly NormalizationOptions _normalization;
    readonly KaraokeTextStyle _karaokeStyle;
    readonly CdgScalingOptions _cdgScaling;
    readonly bool _decodesSoundtracks;
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

    // The device-list watch (DeviceChanged): runs while anyone listens.
    readonly Lock _deviceWatchGate = new();
    EventHandler<AudioDeviceChangeInfo>? _deviceChanged;
    Timer? _deviceWatch;
    FatzAudioDeviceEvents? _deviceEvents;
    // The device the player was created on, brought back when it returns after going away (null: the default).
    readonly ChosenDeviceReturn? _chosenReturn;
    readonly long _watchClockStart = Stopwatch.GetTimestamp();
    string? _lastMoveNotice;
    long _lastMoveNoticeAt;

    /// <summary>Creates the engine and opens the default output device.</summary>
    /// <param name="configuration">The player's configuration: soundfont, silence trim, normalization, device.</param>
    /// <param name="nullOutput">Plays to FatzAudio's null device (tests, machines without sound).</param>
    internal FatzAudioEngine(MediaPlayerConfiguration configuration, bool nullOutput = false)
    {
        _silenceTrim = configuration.SilenceTrim;
        _normalization = configuration.Normalization;
        _karaokeStyle = configuration.KaraokeStyle.Normalize();
        _cdgScaling = configuration.CdgScaling;
        _decodesSoundtracks = configuration.AudioEngineDecodesSoundtracks;
        _diagnosticOwner = DiagnosticRouter.Owner;
        var cacheDirectory = EnsureCacheDirectory(FatzAudioMappings.ResolveCacheDirectory(_normalization.CacheDirectory));
        _engine = Fa.AudioEngine.Create(new Fa.AudioEngineOptions { NullOutput = nullOutput, CacheDirectory = cacheDirectory });
        try
        {
            FatzAudioFileTypes.Register();

            // FatzAudio's loudness normalization, with its limiter on while normalization is on.
            _engine.SetLimiter(Fa.LimiterMode.Auto, _normalization.TruePeakCeilingDbtp);
            ApplyNormalizationMode();
            if (!string.IsNullOrWhiteSpace(configuration.MidiSoundfontPath))
            {
                _soundFont = Fa.SoundFont.FromFile(configuration.MidiSoundfontPath);
                _engine.DefaultSoundFont = _soundFont;
            }

            // The chosen device, else the default: FatzAudio follows default-device changes, and an
            // output whose device goes away is reopened on the default.
            var chosen = nullOutput ? null : SelectDevice(configuration);
            OutputDevice = _engine.OpenOutput(chosen);
            _chosenReturn = chosen is null ? null : new ChosenDeviceReturn(chosen.Id, chosen.Name);
            _bus = OutputDevice.CreateBus();
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
            $"FatzAudio {Fa.AudioEngine.NativeVersion} ready on the {(nullOutput ? "null" : OutputDevice.Info?.Name ?? "default")} output device; MIDI soundfont: {(_soundFont is null ? "system bank" : configuration.MidiSoundfontPath)}; normalization {_normalization.TargetLufs:0.#} LUFS, limiter {_normalization.TruePeakCeilingDbtp:0.#} dBTP, cache: {cacheDirectory ?? "off"}");
    }

    public event EventHandler<AudioEngineState>? StateChanged;
    public event EventHandler? Opened;
    public event EventHandler? Closed;
    public event EventHandler<double>? PositionChanged;
    public event EventHandler? PlaybackEnded;
    public event EventHandler<double>? NormalizationGainChanged;

    /// <summary>
    /// Devices added and removed and every default change, each once, from a look at FatzAudio's device list each
    /// <see cref="DeviceWatchInterval"/> while anyone listens (<c>ListDevices</c>: one enumeration, no format query)
    /// and from FatzAudio's notice that it moved the output (<see cref="FatzAudioDeviceEvents"/>). The player's one
    /// device-event source.
    /// </summary>
    public event EventHandler<AudioDeviceChangeInfo>? DeviceChanged
    {
        add
        {
            lock (_deviceWatchGate)
            {
                _deviceChanged += value;
                if (_deviceChanged is not null && _deviceWatch is null && !_disposed)
                {
                    _deviceEvents = new FatzAudioDeviceEvents(OutputDevice.Info?.Id);
                    if (ReadDevices() is { } baseline)
                        _ = _deviceEvents.OnList(baseline);
                    _deviceWatch = new Timer(_ => WatchDevices(), null, DeviceWatchInterval, DeviceWatchInterval);
                }
            }
        }
        remove
        {
            Timer? stopped = null;
            lock (_deviceWatchGate)
            {
                _deviceChanged -= value;
                if (_deviceChanged is null)
                    (stopped, _deviceWatch) = (_deviceWatch, null);
            }

            stopped?.Dispose();
        }
    }

    /// <summary>How often the device list is read while someone listens to <see cref="DeviceChanged"/>.</summary>
    internal TimeSpan DeviceWatchInterval { get; set; } = TimeSpan.FromSeconds(1);

    public AudioEngineState State
    {
        get
        {
            lock (_stateGate)
                return _state;
        }
    }

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
    /// song). With normalization off it is the gain FatzAudio would apply: the measured gain whatever the
    /// switch says.
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

    /// <summary>The device opened (null: the system default), for tests.</summary>
    internal Fa.AudioDeviceInfo? OutputDeviceInfo => OutputDevice.Info;

    /// <summary>Whether the device list is being watched, for tests.</summary>
    internal bool IsWatchingDevices
    {
        get
        {
            lock (_deviceWatchGate)
                return _deviceWatch is not null;
        }
    }

    /// <summary>The bus volume (the user volume), for tests.</summary>
    internal float OutputVolume => _bus.Volume;

    /// <summary>CD+G drawn by FatzAudio on this engine (037A slice 3).</summary>
    ICdgEngine ICdgEngineSource.CreateCdgEngine() => new FatzAudioCdgEngine(_engine, _cdgScaling);

    /// <summary>Video audio, soundtracks and network audio through FFmpeg on this engine's device (037A slice 5).</summary>
    IPushAudioOutput IPushAudioOutputSource.CreatePushAudioOutput() =>
        new FatzAudioPushAudioOutput(this, _engine, OutputDevice, _normalization, _decodesSoundtracks);

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
            analyzeSilence && _silenceTrim.Enabled && !AudioSourceFiles.IsTrackerFormat(filePath));
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
            (data, length) = AudioSourceFiles.ReadArchiveEntry(archiveEntry);
        }
        catch (Exception ex)
        {
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Error, Category, $"Open failed for archive entry '{archiveEntry.Key}': {ex.Message}", sourcePath: archiveEntry.Key, innerException: ex);
            return false;
        }

        // Archive entries are never trimmed.
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
                // to a float WAV (archive entries through a temp file).
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
            var hasLyrics = HasLyrics(stream);
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
                _streamHasLyrics = hasLyrics;
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
                $"Silence detection complete: first audible {silence.FirstAudibleTime?.TotalSeconds.ToString("F3", CultureInfo.InvariantCulture) ?? "none"}s, last audible {silence.LastAudibleTime?.TotalSeconds.ToString("F3", CultureInfo.InvariantCulture) ?? "none"}s, playable {result.PlayableStart:F3}-{result.PlayableEnd:F3}s ({silence.Mode} {silence.ThresholdDb:F0} dBFS{(silence.FromCache ? ", cached" : string.Empty)})",
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

    /// <summary>The song's true peak from FatzAudio's loudness measurement (see <see cref="TruePeakAsync"/>).</summary>
    public Task<double> AnalyzePeakQuickAsync(CancellationToken cancellationToken) => TruePeakAsync(cancellationToken);

    /// <summary>The song's true peak from FatzAudio's loudness measurement (see <see cref="TruePeakAsync"/>).</summary>
    public Task<double> AnalyzePeakAsync(CancellationToken cancellationToken) => TruePeakAsync(cancellationToken);

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        Timer? watch;
        lock (_deviceWatchGate)
            (watch, _deviceWatch) = (_deviceWatch, null);
        watch?.Dispose();
        CloseStream();
        SetState(AudioEngineState.Stopped);
        _bus.Dispose();
        OutputDevice.Dispose();
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
            ForgetLyricSurfaceLocked();
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

    // One event per wrap: PlaybackEnded while still Playing.
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
            deviceName = _engine.ListDevices(Fa.DeviceKind.Playback).FirstOrDefault(static device => device.IsDefault)?.Name;
        }
        catch (Exception ex) when (ex is Fa.AudioException or ObjectDisposedException)
        {
            // The name is only for the report.
        }

        var notice = e.Value == 0
            ? $"FatzAudio moved the output to the new default device {deviceName ?? "(unknown)"}."
            : $"The output device went away; FatzAudio reopened the output on the default device {deviceName ?? "(unknown)"}.";
        // Every stream on the output gets the event: one notice per move.
        bool repeated;
        lock (_deviceWatchGate)
        {
            repeated = notice == _lastMoveNotice && Stopwatch.GetElapsedTime(_lastMoveNoticeAt) < TimeSpan.FromSeconds(2);
            _lastMoveNotice = notice;
            _lastMoveNoticeAt = Stopwatch.GetTimestamp();
        }

        if (!repeated)
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Info, Category, notice);

        // Also a change the list may not show (unplugged and back within one look, an exclusive-mode reset).
        IReadOnlyList<AudioDeviceChangeInfo> changes;
        EventHandler<AudioDeviceChangeInfo>? handlers;
        lock (_deviceWatchGate)
        {
            if (_deviceWatch is null || _deviceEvents is null)
                return;

            changes = _deviceEvents.OnOutputMoved(e.Value != 0, ReadDevices());
            handlers = _deviceChanged;
        }

        RaiseDeviceChanges(changes, handlers);
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

    /// <summary>
    /// The device <see cref="MediaPlayerConfiguration.AudioOutputDeviceId"/> names (id, else name), or null for the
    /// default. A device that is not there: the default with a warning, or an error with
    /// <see cref="AudioOutputFallbackMode.NoFallback"/>.
    /// </summary>
    Fa.AudioDeviceInfo? SelectDevice(MediaPlayerConfiguration configuration)
    {
        if (string.IsNullOrWhiteSpace(configuration.AudioOutputDeviceId))
            return null;

        var devices = _engine.OutputDevices;
        if (FatzAudioDevices.Find(devices, configuration.AudioOutputDeviceId) is { } device)
            return device;

        var known = string.Join(", ", devices.Select(static d => $"'{d.Name}'"));
        if (configuration.AudioOutputFallbackMode == AudioOutputFallbackMode.NoFallback)
            throw new InvalidOperationException($"Audio output device '{configuration.AudioOutputDeviceId}' was not found. Devices: {known}.");

        DiagnosticRouter.Raise(
            MediaDiagnosticLevel.Warning,
            Category,
            $"Audio output device '{configuration.AudioOutputDeviceId}' was not found; playing on the system default. Devices: {known}.");
        return null;
    }

    IReadOnlyList<FatzAudioDevices.Snapshot>? ReadDevices()
    {
        try
        {
            return FatzAudioDevices.ToSnapshots(_engine.ListDevices(Fa.DeviceKind.Playback));
        }
        catch (Exception ex) when (ex is Fa.AudioException or ObjectDisposedException)
        {
            return null;
        }
    }

    /// <summary>One look at the device list: each change since the last look becomes a <see cref="DeviceChanged"/>.</summary>
    void WatchDevices()
    {
        using var owner = DiagnosticRouter.EnterOwner(_diagnosticOwner);
        IReadOnlyList<AudioDeviceChangeInfo> changes;
        EventHandler<AudioDeviceChangeInfo>? handlers;
        // Held while reading, so Dispose (which takes it) never frees the engine under a read.
        lock (_deviceWatchGate)
        {
            if (_deviceWatch is null || _deviceEvents is null || ReadDevices() is not { } now)
                return;

            // No baseline yet (the first read failed): this look is the baseline, not a burst of "added".
            changes = _deviceEvents.OnList(now);
            handlers = _deviceChanged;
            if (_chosenReturn?.OnList(now, Stopwatch.GetElapsedTime(_watchClockStart)) is { Step: not ChosenDeviceReturn.Step.None } decision)
                FollowChosenDeviceLocked(decision);
        }

        RaiseDeviceChanges(changes, handlers);
    }

    /// <summary>
    /// The chosen device went away (play on the default now, instead of when the system notices), came back (move the
    /// output back to it), or the user chose another default meanwhile (stop waiting). Caller holds _deviceWatchGate.
    /// </summary>
    void FollowChosenDeviceLocked(ChosenDeviceReturn.Decision decision)
    {
        var name = _chosenReturn!.ChosenName;
        try
        {
            switch (decision.Step)
            {
                case ChosenDeviceReturn.Step.MoveToDefault:
                    OutputDevice.MoveTo(null);
                    _deviceEvents?.OnMovedTo(null);
                    DiagnosticRouter.Raise(MediaDiagnosticLevel.Info, Category, $"The chosen output device {name} went away: playing on the default device until it returns.");
                    break;
                case ChosenDeviceReturn.Step.MoveBack when decision.Device is { } back:
                    // From the look just taken: ListDevices has no native rate or channels either.
                    var device = new Fa.AudioDeviceInfo(back.Id, back.Name, Fa.DeviceKind.Playback, back.IsDefault, 0, 0);
                    OutputDevice.MoveTo(device);
                    _deviceEvents?.OnMovedTo(device.Id);
                    DiagnosticRouter.Raise(MediaDiagnosticLevel.Info, Category, $"Back on {device.Name}: the chosen output device returned.");
                    break;
                case ChosenDeviceReturn.Step.StopWaiting:
                    DiagnosticRouter.Raise(MediaDiagnosticLevel.Info, Category, $"The default output device was changed while {name} was away: no longer waiting for it.");
                    break;
            }
        }
        catch (Exception ex) when (ex is Fa.AudioException or ObjectDisposedException)
        {
            // A device just plugged in may not open yet: the next look tries again.
            if (decision.Step == ChosenDeviceReturn.Step.MoveBack)
                _chosenReturn.MoveBackFailed();
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Warning, Category, $"Could not move the output for {name}: {ex.Message}", innerException: ex);
        }
    }

    void RaiseDeviceChanges(IReadOnlyList<AudioDeviceChangeInfo> changes, EventHandler<AudioDeviceChangeInfo>? handlers)
    {
        // The player logs each change it forwards (PlayerCore), so the engine does not log it too.
        foreach (var change in changes)
        {
            try
            {
                handlers?.Invoke(this, change);
            }
            catch (Exception ex)
            {
                DiagnosticRouter.Raise(MediaDiagnosticLevel.Error, Category, $"Device change handling failed: {ex.Message}", innerException: ex);
            }
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
