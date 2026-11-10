using System.Runtime.InteropServices;
using FatzMedia.Video.Core.Audio;
using Fa = global::FatzMedia.Audio;

namespace FatzMedia.Video.Audio;

/// <summary>
/// Video audio, Audio-mode soundtracks and network audio through FFmpeg on the FatzAudio engine's device
/// (037A slice 5): <see cref="VideoAudioEngine"/> decodes with FFmpeg and pushes float PCM into a FatzAudio
/// push stream, stamped with its place on the engine's continuous timeline, so FatzAudio's presentation clock
/// (what is heard now, device latency included, tempo applied) is the position the picture follows.
/// </summary>
/// <remarks>
/// <para>Only PCM crosses: no FFmpeg object reaches FatzAudio. Codecs FatzAudio could decode itself (packet
/// streams) are not used: FFmpeg already decodes every soundtrack here, and one path is simpler to keep right.</para>
/// <para>Gain stages as on the file engine: the stream's volume is the fade stage, this output's own bus the
/// user volume, normalization FatzAudio's loudness on top. The stream names the song
/// (<see cref="Fa.StreamOptions.SourceIdentity"/>), so FatzAudio's loudness cache applies; a local file of up to
/// <see cref="MaxScanSeconds"/> is also scanned by path in the background (a film is left to the running
/// estimate: its scan would cost minutes of CPU).</para>
/// <para>After a seek or stop FatzAudio's clock holds where it was until audio of the new position is queued or
/// heard; until then the position is the restart target.</para>
/// </remarks>
sealed class FatzAudioPushAudioOutput : IPushAudioOutput
{
    const string Category = "FatzAudioVideoAudio";

    /// <summary>Longest local file whose loudness is scanned by path when it opens: 15 minutes.</summary>
    internal const double MaxScanSeconds = 15 * 60;

    // A push whose start is this close to where the previous one ended continues it (no timestamp):
    // containers round frame times (Matroska to 1 ms), and contiguous PCM is never re-stamped for that.
    const double ContinuationToleranceSeconds = 0.02;
    const int MicrosecondsPerSecond = 1_000_000;

    readonly FatzAudioEngine _owner;
    readonly Fa.AudioEngine _engine;
    readonly Fa.MixBus _bus;
    readonly NormalizationOptions _normalization;
    readonly bool _decodesPackets;
    readonly object? _diagnosticOwner;

    // Guards the stream and everything bound to it: the engine calls most members under its own lock,
    // the fade runner and the equalizer come from other threads.
    readonly Lock _gate = new();
    Fa.FeedAudioStream? _stream;
    // Loudness scans still running; they cannot be cancelled. Close does not wait for them, Dispose does.
    readonly HashSet<Task> _scans = [];
    double _volume = 1;
    int _pitch;
    int _tempo;
    AudioEqualizerSettings _equalizer = AudioEqualizerSettings.Disabled;
    int _fadeSession;
    double? _nextContinuousStart;
    // Guarded by _gate: a failed packet push was reported for this stream.
    bool _packetFailureReported;
    bool _holding;
    double _heldPosition;
    double _clockAtRestart;
    bool _disposed;

    internal FatzAudioPushAudioOutput(FatzAudioEngine owner, Fa.AudioEngine engine, Fa.OutputDevice device, NormalizationOptions normalization, bool decodesPackets = false)
    {
        _owner = owner;
        _decodesPackets = decodesPackets;
        _engine = engine;
        _normalization = normalization;
        _diagnosticOwner = DiagnosticRouter.Owner;
        _bus = device.CreateBus();
    }

    public bool IsCreated
    {
        get
        {
            lock (_gate)
                return _stream is not null;
        }
    }

    public event EventHandler<double>? NormalizationGainChanged;

    /// <summary>The player's normalization switch: FatzAudio's normalization is per engine.</summary>
    public bool Normalize
    {
        get => _owner.Normalize;
        set => _owner.Normalize = value;
    }

    /// <summary>FatzAudio's gain for the stream as a multiplier, as the file engine reports it.</summary>
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

    /// <summary>Always: FatzAudio starts on its running estimate and glides.</summary>
    public bool IsNormalizationReady => true;

    public AudioEqualizerSettings Equalizer
    {
        get
        {
            lock (_gate)
                return _equalizer;
        }
    }

    public bool Create(int sampleRate, int channels, string? sourcePath) =>
        OpenFeed(options => _engine.CreatePushStream(new Fa.AudioFormat(Fa.SampleFormat.Float32, sampleRate, channels), options), sourcePath);

    /// <summary>
    /// A packet stream when FatzAudio is to decode soundtracks (<c>MediaPlayerConfiguration.AudioEngineDecodesSoundtracks</c>)
    /// and the codec is one it decodes on every platform; timestamps in microseconds of our continuous timeline.
    /// </summary>
    public bool CreateForPackets(PacketCodecInfo codec, string? sourcePath)
    {
        if (!_decodesPackets || !FatzAudioMappings.DecodesPackets(codec.Codec))
            return false;

        var parameters = new Fa.CodecParameters(codec.Codec, codec.SampleRate, codec.Channels)
        {
            ExtraData = codec.ExtraData,
            BitRate = codec.BitRate,
            BlockAlign = codec.BlockAlign,
            BitsPerCodedSample = codec.BitsPerCodedSample,
            TimeBase = new Fa.Rational(1, MicrosecondsPerSecond),
        };
        if (!OpenFeed(options => _engine.OpenPackets(parameters, options), sourcePath))
            return false;

        DiagnosticRouter.Raise(MediaDiagnosticLevel.Info, Category, $"FatzAudio decodes the soundtrack ({codec.Codec}, {codec.SampleRate} Hz, {codec.Channels} channels); FFmpeg only demuxes.", sourcePath: sourcePath);
        return true;
    }

    public bool PushPacket(ReadOnlySpan<byte> data, double continuousStartSeconds, double durationSeconds)
    {
        lock (_gate)
        {
            if (_stream is not Fa.PacketAudioStream stream || data.IsEmpty)
                return false;

            var stamp = _nextContinuousStart is { } expected && Math.Abs(continuousStartSeconds - expected) <= ContinuationToleranceSeconds
                ? Fa.FeedAudioStream.NoTimestamp
                : (long)Math.Round(Math.Max(0, continuousStartSeconds) * MicrosecondsPerSecond);
            try
            {
                stream.PushPacket(data, stamp, (long)Math.Round(Math.Max(0, durationSeconds) * MicrosecondsPerSecond));
            }
            catch (Exception ex) when (ex is Fa.AudioException or ObjectDisposedException or ArgumentException)
            {
                // Once per stream: a track FatzAudio cannot decode fails on every packet (tens a second).
                if (!_packetFailureReported)
                {
                    _packetFailureReported = true;
                    DiagnosticRouter.Raise(MediaDiagnosticLevel.Warning, Category, $"Packet push failed: {ex.Message}", innerException: ex);
                }

                return false;
            }

            _nextContinuousStart = continuousStartSeconds + durationSeconds;
            return true;
        }
    }

    /// <summary>Opens a fed stream (PCM or packets) attached to this output's bus and paused, with the current settings.</summary>
    bool OpenFeed(Func<Fa.StreamOptions, Fa.FeedAudioStream> open, string? sourcePath)
    {
        Fa.AudioFingerprint? identity = null;
        if (sourcePath is not null)
        {
            try
            {
                identity = Fa.AudioFingerprint.Compute(sourcePath);
            }
            catch (Exception ex) when (ex is Fa.AudioException or IOException or UnauthorizedAccessException)
            {
                DiagnosticRouter.Raise(MediaDiagnosticLevel.Info, Category, $"No loudness identity for the soundtrack ({ex.Message}); FatzAudio uses its running estimate.", sourcePath: sourcePath);
            }
        }

        Fa.FeedAudioStream? stream = null;
        try
        {
            stream = open(new Fa.StreamOptions { SourceIdentity = identity });
            stream.LoudnessFinal += OnLoudnessFinal;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _stream = stream;
                stream = null;
                // Attached and paused: nothing plays until Play.
                _stream.PlayOn(_bus);
                _stream.Pause();
                _stream.Pitch = Math.Clamp(_pitch, -60, 60);
                _stream.Tempo = FatzAudioMappings.ToTempo(_tempo);
                _stream.SetEqualizer(FatzAudioMappings.ToEqualizerBands(_equalizer));
                _bus.Volume = (float)_volume;
                StartTimelineLocked(0);
            }

            return true;
        }
        catch (Exception ex) when (ex is Fa.AudioException or ObjectDisposedException or InvalidOperationException or ArgumentException)
        {
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Error, Category, $"FatzAudio soundtrack stream failed: {ex.Message}", sourcePath: sourcePath, innerException: ex);
            if (stream is not null)
            {
                stream.LoudnessFinal -= OnLoudnessFinal;
                stream.Dispose();
            }

            Release();
            return false;
        }
    }

    public void Release()
    {
        Fa.FeedAudioStream? stream;
        lock (_gate)
        {
            stream = _stream;
            _stream = null;
            _fadeSession = NextSession(_fadeSession);
            _nextContinuousStart = null;
            _holding = false;
            _packetFailureReported = false;
        }

        if (stream is not null)
        {
            stream.LoudnessFinal -= OnLoudnessFinal;
            stream.Dispose();
        }
    }

    public bool Push(ReadOnlySpan<byte> interleavedFloat, double continuousStartSeconds)
    {
        lock (_gate)
        {
            if (_stream is not Fa.PushAudioStream stream || interleavedFloat.IsEmpty)
                return false;

            // Stamp the first push after a restart, and any jump; contiguous audio just continues.
            TimeSpan? stamp = _nextContinuousStart is { } expected && Math.Abs(continuousStartSeconds - expected) <= ContinuationToleranceSeconds
                ? null
                : TimeSpan.FromSeconds(Math.Max(0, continuousStartSeconds));
            var samples = MemoryMarshal.Cast<byte, float>(interleavedFloat);
            try
            {
                stream.Push(samples, stamp);
            }
            catch (Exception ex) when (ex is Fa.AudioException or ObjectDisposedException or ArgumentException)
            {
                DiagnosticRouter.Raise(MediaDiagnosticLevel.Warning, Category, $"Push failed: {ex.Message}", innerException: ex);
                return false;
            }

            var channels = Math.Max(1, stream.Format.Channels);
            _nextContinuousStart = continuousStartSeconds + (samples.Length / channels / (double)stream.Format.SampleRate);
            return true;
        }
    }

    public bool Play() => Try(static stream => stream.Play(), "Play");

    public bool Resume() => Play();

    public bool Pause() => Try(static stream => stream.Pause(), "Pause");

    public void Restart(double positionBaseSeconds, bool stop)
    {
        lock (_gate)
        {
            if (_stream is not { } stream)
                return;

            try
            {
                stream.Pause();
                stream.Flush();
            }
            catch (Exception ex) when (ex is Fa.AudioException or ObjectDisposedException)
            {
                DiagnosticRouter.Raise(MediaDiagnosticLevel.Warning, Category, $"Restart failed: {ex.Message}", innerException: ex);
            }

            StartTimelineLocked(positionBaseSeconds);
        }
    }

    /// <summary>FatzAudio's clock (our continuous timeline); the restart target until audio of it is queued.</summary>
    public double GetContinuousPosition(double positionBaseSeconds)
    {
        lock (_gate)
        {
            if (_stream is not { } stream)
                return positionBaseSeconds;

            double clock;
            try
            {
                clock = stream.Clock.TotalSeconds;
            }
            catch (ObjectDisposedException)
            {
                return positionBaseSeconds;
            }

            if (_holding)
            {
                if (clock == _clockAtRestart)
                    return _heldPosition;

                _holding = false;
            }

            return clock;
        }
    }

    /// <summary>Unchanged: FatzAudio's clock already includes the device latency.</summary>
    public double ToHeardPosition(double seconds) => seconds;

    public void SetVolume(double volume01) => ApplyVolume(volume01, TimeSpan.Zero);

    public void SlideVolume(double volume01, TimeSpan duration) => ApplyVolume(volume01, duration);

    public void SetPitch(int semitones)
    {
        lock (_gate)
        {
            _pitch = semitones;
            _ = TryLocked(stream => stream.Pitch = Math.Clamp(semitones, -60, 60), "Pitch");
        }
    }

    public void SetTempo(int percent)
    {
        lock (_gate)
        {
            _tempo = percent;
            _ = TryLocked(stream => stream.Tempo = FatzAudioMappings.ToTempo(percent), "Tempo");
        }
    }

    public bool SetEqualizer(AudioEqualizerSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (_gate)
        {
            _equalizer = settings;
            return _stream is null || TryLocked(stream => stream.SetEqualizer(FatzAudioMappings.ToEqualizerBands(settings)), "Equalizer");
        }
    }

    public int BeginFadeSession()
    {
        lock (_gate)
        {
            if (_stream is null)
                return 0;

            _fadeSession = NextSession(_fadeSession);
            return _fadeSession;
        }
    }

    public bool SetFadeGain(int session, double gain, TimeSpan ramp)
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

    public void EndFadeSession()
    {
        lock (_gate)
        {
            _fadeSession = NextSession(_fadeSession);
            _ = TryLocked(static stream => stream.Volume = 1f, "Fade reset");
        }
    }

    /// <summary>
    /// Scans a local file of up to <see cref="MaxScanSeconds"/> by path in the background, so its loudness is
    /// measured once and cached; FatzAudio glides the playing stream to it. Files FatzAudio cannot open (a
    /// container it does not read) and temporary copies (an archive entry: the engine deletes the copy when the
    /// song closes, and a scan cannot be stopped) keep the running estimate.
    /// </summary>
    public void StartNormalization(string filePath, string? cacheKey, double durationSeconds, bool temporaryCopy)
    {
        if (temporaryCopy || durationSeconds is <= 0 or > MaxScanSeconds || !double.IsFinite(durationSeconds))
            return;

        lock (_gate)
        {
            if (_disposed || _stream is not { } stream || IsLoudnessFinal(stream))
                return;

            var scan = Task.Run(() => Scan(stream, filePath));
            _ = _scans.Add(scan);
            _ = scan.ContinueWith(done => { lock (_gate) _ = _scans.Remove(done); }, TaskScheduler.Default);
        }
    }

    // FatzAudio's cache keeps the result for the next play; the playing stream gets it now (a scan by
    // path does not reach a push stream by itself).
    void Scan(Fa.FeedAudioStream stream, string filePath)
    {
        using var owner = DiagnosticRouter.EnterOwner(_diagnosticOwner);
        try
        {
            var loudness = _engine.ScanLoudness(filePath);
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Info, Category, $"Soundtrack loudness {loudness.IntegratedLufs:0.0} LUFS, true peak {loudness.TruePeakDbtp:0.0} dBTP ({loudness.Source})", sourcePath: filePath);
            lock (_gate)
            {
                if (!ReferenceEquals(stream, _stream) || !double.IsFinite(loudness.IntegratedLufs))
                    return;

                stream.SetLoudness(loudness.IntegratedLufs, loudness.RangeLu, loudness.TruePeakDbtp);
            }

            // A supplied loudness raises no LoudnessFinal.
            QueueNormalizationGainChanged(stream);
        }
        catch (Exception ex)
        {
            // Every failure ends here: Dispose waits for the scan and must not throw.
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Info, Category, $"Soundtrack not scanned ({ex.Message}); FatzAudio uses its running estimate.", sourcePath: filePath);
        }
    }

    public void Dispose()
    {
        lock (_gate)
            _disposed = true;

        Release();
        // Running scans use the FatzAudio engine, which the player disposes after its pipelines.
        Task[] scans;
        lock (_gate)
            scans = [.. _scans];
        Task.WaitAll(scans);
        _bus.Dispose();
    }

    void ApplyVolume(double volume01, TimeSpan ramp)
    {
        lock (_gate)
        {
            _volume = Math.Clamp(double.IsFinite(volume01) ? volume01 : 0, 0, 1);
            try
            {
                if (ramp > TimeSpan.Zero)
                    _bus.Ramp(Fa.AudioParameter.Volume, (float)_volume, ramp);
                else
                    _bus.Volume = (float)_volume;
            }
            catch (Exception ex) when (ex is Fa.AudioException or ObjectDisposedException)
            {
                DiagnosticRouter.Raise(MediaDiagnosticLevel.Warning, Category, $"Volume could not be set: {ex.Message}", innerException: ex);
            }
        }
    }

    void StartTimelineLocked(double positionBaseSeconds)
    {
        _nextContinuousStart = null;
        _holding = true;
        _heldPosition = positionBaseSeconds;
        try
        {
            _clockAtRestart = _stream?.Clock.TotalSeconds ?? 0;
        }
        catch (ObjectDisposedException)
        {
            _clockAtRestart = 0;
        }
    }

    void OnLoudnessFinal(object? sender, EventArgs e)
    {
        if (sender is Fa.FeedAudioStream stream)
            QueueNormalizationGainChanged(stream);
    }

    /// <summary>
    /// Raises <see cref="NormalizationGainChanged"/> on the thread pool, for the stream still playing: host
    /// handlers never run on the scan thread (which <see cref="Release"/> joins under the engine's lock) or
    /// on FatzAudio's event thread (which a stream's disposal may wait for).
    /// </summary>
    void QueueNormalizationGainChanged(Fa.FeedAudioStream stream)
    {
        using var owner = DiagnosticRouter.EnterOwner(_diagnosticOwner);
        _ = Task.Run(() =>
        {
            try
            {
                lock (_gate)
                {
                    if (!ReferenceEquals(stream, _stream))
                        return;
                }

                NormalizationGainChanged?.Invoke(this, NormalizationGain);
            }
            catch (Exception ex)
            {
                DiagnosticRouter.Raise(MediaDiagnosticLevel.Error, Category, $"Loudness handling failed: {ex.Message}", innerException: ex);
            }
        });
    }

    static bool IsLoudnessFinal(Fa.AudioStream stream)
    {
        try
        {
            return stream.GetLoudness().IsFinal;
        }
        catch (Exception ex) when (ex is Fa.AudioException or ObjectDisposedException)
        {
            return false;
        }
    }

    bool Try(Action<Fa.FeedAudioStream> action, string operation)
    {
        lock (_gate)
            return TryLocked(action, operation);
    }

    bool TryLocked(Action<Fa.FeedAudioStream> action, string operation)
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

    static int NextSession(int session) => session == int.MaxValue ? 1 : session + 1;
}
