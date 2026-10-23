using FatzBomb.MediaControl.Core.Abstractions;
using FatzBomb.MediaControl.Core.Audio;
using FatzBomb.MediaControl.Core.Audio.Fade;
using System.Diagnostics;

namespace FatzBomb.MediaControl.Pipeline;

/// <summary>
/// Plays the soundtrack of a video file with no video decoder (Stage 043A, Audio mode). It wraps
/// the <see cref="VideoAudioEngine"/> that video playback already uses (FFmpeg decode into a BASS
/// push stream, with tempo, pitch, equalizer, fades and normalization) and runs the decode pump
/// that <see cref="VideoPipeline"/> otherwise drives, so the file plays as plain audio content.
/// </summary>
/// <remarks>
/// The push stream never ends by itself, so the end is detected here: the position reaches the
/// duration, or it stops advancing while playing for <see cref="EndStallSeconds"/> (the audio ran
/// out before the container's duration, which includes the picture). A gapless loop wrap
/// (<see cref="VideoAudioEngine.CompletedLoopCount"/>) is reported through
/// <see cref="PlaybackEnded"/>, as the plain audio engine reports its loop wraps.
/// </remarks>
internal sealed class SoundtrackAudioEngine : IAudioEngine, IAudioLoopControl, IAudioFilterHost, IAudioFadeGainTarget, IAudioVolumeSlide
{
    internal const double EndToleranceSeconds = 0.05;
    internal const double EndStallSeconds = 1.0;
    private const double StallPositionEpsilonSeconds = 0.0005;

    private readonly VideoAudioEngine _audio;
    private readonly PlaybackScheduler _pump = new("SoundtrackDecode");
    private readonly Lock _gate = new();
    private int _generation;
    private int _observedLoopCount;
    private bool _ended;
    private bool _loopPlayback;
    private bool _disposed;

    public SoundtrackAudioEngine(IOutputLatencyProvider outputLatencyProvider, int bassDeviceIndex)
    {
        _audio = new VideoAudioEngine(outputLatencyProvider, bassDeviceIndex);
        _audio.StateChanged += OnAudioStateChanged;
        _audio.Opened += OnAudioOpened;
        _audio.Closed += OnAudioClosed;
        _audio.PositionChanged += OnAudioPositionChanged;
        _audio.NormalizationGainChanged += OnAudioNormalizationGainChanged;
    }

    public AudioEngineState State => _audio.State;
    public int PlayHandle => _audio.PlayHandle;
    public int SourceHandle => _audio.SourceHandle;
    public bool IsOpen => _audio.IsOpen;

    public bool Normalize
    {
        get => _audio.Normalize;
        set => _audio.Normalize = value;
    }

    public double NormalizationGain => _audio.NormalizationGain;
    public bool IsNormalizationReady => _audio.IsNormalizationReady;
    public int CurrentTempo => _audio.CurrentTempo;
    public double Duration => _audio.Duration;

    public double Position
    {
        get => _audio.Position;
        set => Seek(value);
    }

    public bool LoopPlayback
    {
        get => Volatile.Read(ref _loopPlayback);
        set
        {
            Volatile.Write(ref _loopPlayback, value);
            _audio.LoopPlayback = value;
        }
    }

    public event EventHandler<double>? NormalizationGainChanged;
    public event EventHandler<AudioEngineState>? StateChanged;
    public event EventHandler? Opened;
    public event EventHandler? Closed;
    public event EventHandler<double>? PositionChanged;
    public event EventHandler? PlaybackEnded;

    /// <summary>Audio output is initialized by the player's audio engine; nothing to do here.</summary>
    public bool Init(int device = -1, int sampleRate = 44100) => true;

    public bool Open(string filePath) => OpenCore(() => _audio.Open(filePath));

    public bool Open(IArchiveEntryInfo archiveEntry) => OpenCore(() => _audio.Open(archiveEntry));

    private bool OpenCore(Func<bool> open)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        StopPump();

        // Open closes the previous file, which clears the inner loop flag and loop count.
        if (!open())
            return false;

        ResetEndTracking();
        _audio.LoopPlayback = LoopPlayback;
        _ = _audio.PrebufferAudio();
        return true;
    }

    public bool Play()
    {
        if (!_audio.IsOpen)
            return false;

        bool restartFromBeginning;
        lock (_gate)
            restartFromBeginning = _ended;
        if (restartFromBeginning)
        {
            _ = _audio.Seek(0, resumePlayback: false);
            ResetEndTracking();
        }

        _ = _audio.PrebufferAudio();
        if (!_audio.Play())
            return false;

        StartPump();
        return true;
    }

    public bool Pause()
    {
        StopPump();
        return _audio.Pause();
    }

    public bool Stop()
    {
        StopPump();
        ResetEndTracking();
        return _audio.Stop();
    }

    private void Seek(double seconds)
    {
        ResetEndTracking();
        _ = _audio.Seek(seconds);
    }

    public void Close()
    {
        StopPump();
        ResetEndTracking();
        _audio.Close();
    }

    public void SetVolume(double volume01) => _audio.SetVolume(volume01);

    public void SetPitch(int semitones) => _audio.SetPitch(semitones);

    public void SetTempo(int percent) => _audio.SetTempo(percent);

    public Task<double> AnalyzePeakQuickAsync(CancellationToken cancellationToken) => _audio.AnalyzePeakQuickAsync(cancellationToken);

    public Task<double> AnalyzePeakAsync(CancellationToken cancellationToken) => _audio.AnalyzePeakAsync(cancellationToken);

    AudioEqualizerSettings IAudioFilterHost.Equalizer => ((IAudioFilterHost)_audio).Equalizer;

    bool IAudioFilterHost.SetEqualizer(AudioEqualizerSettings settings) => ((IAudioFilterHost)_audio).SetEqualizer(settings);

    int IAudioFadeGainTarget.BeginFadeSession() => ((IAudioFadeGainTarget)_audio).BeginFadeSession();

    bool IAudioFadeGainTarget.SetFadeGain(int session, double gain, TimeSpan ramp) => ((IAudioFadeGainTarget)_audio).SetFadeGain(session, gain, ramp);

    void IAudioVolumeSlide.SlideVolume(double volume01, TimeSpan duration) => ((IAudioVolumeSlide)_audio).SlideVolume(volume01, duration);

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _pump.Dispose();
        _audio.StateChanged -= OnAudioStateChanged;
        _audio.Opened -= OnAudioOpened;
        _audio.Closed -= OnAudioClosed;
        _audio.PositionChanged -= OnAudioPositionChanged;
        _audio.NormalizationGainChanged -= OnAudioNormalizationGainChanged;
        _audio.Dispose();
    }

    private void StartPump()
    {
        int generation;
        lock (_gate)
            generation = ++_generation;
        _pump.Start(token => PumpLoop(generation, token));
    }

    private void StopPump()
    {
        lock (_gate)
            _generation++;
        _pump.StopAndWait();
    }

    private void ResetEndTracking()
    {
        lock (_gate)
        {
            _ended = false;
            _observedLoopCount = _audio.CompletedLoopCount;
        }
    }

    private void PumpLoop(int generation, CancellationToken cancellationToken)
    {
        var lastPosition = double.NaN;
        var stallStarted = 0L;
        while (!cancellationToken.IsCancellationRequested && IsCurrent(generation))
        {
            var decoded = _audio.DecodeAndPushAudio();
            if (ReportLoopWraps(generation))
            {
                lastPosition = double.NaN;
                stallStarted = 0;
            }
            else if (!LoopPlayback && _audio.State == AudioEngineState.Playing)
            {
                var position = _audio.Position;
                if (double.IsNaN(lastPosition) || Math.Abs(position - lastPosition) > StallPositionEpsilonSeconds)
                {
                    lastPosition = position;
                    stallStarted = Stopwatch.GetTimestamp();
                }

                var stalledSeconds = stallStarted == 0 ? 0 : Stopwatch.GetElapsedTime(stallStarted).TotalSeconds;
                if (HasReachedEnd(position, Duration, stalledSeconds))
                {
                    CompleteAtEnd(generation);
                    return;
                }
            }

            _ = cancellationToken.WaitHandle.WaitOne(decoded ? 1 : 10);
        }
    }

    /// <summary>
    /// The soundtrack has ended when the position reaches the duration, or when the position has
    /// not moved for <see cref="EndStallSeconds"/> while playing (the decoder ran out of audio).
    /// </summary>
    internal static bool HasReachedEnd(double position, double duration, double stalledSeconds) =>
        (duration > 0 && position >= duration - EndToleranceSeconds) || stalledSeconds >= EndStallSeconds;

    private bool IsCurrent(int generation)
    {
        lock (_gate)
            return generation == _generation;
    }

    private bool ReportLoopWraps(int generation)
    {
        var completed = _audio.CompletedLoopCount;
        lock (_gate)
        {
            if (generation != _generation || completed <= _observedLoopCount)
                return false;

            _observedLoopCount = completed;
        }

        PlaybackEnded?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private void CompleteAtEnd(int generation)
    {
        lock (_gate)
        {
            if (generation != _generation || _ended)
                return;

            _ended = true;
            _generation++;
        }

        _ = _audio.Pause();
        _audio.EndFadeSessionAtEnd();
        PlaybackEnded?.Invoke(this, EventArgs.Empty);
    }

    private void OnAudioStateChanged(object? sender, AudioEngineState state) => StateChanged?.Invoke(this, state);

    private void OnAudioOpened(object? sender, EventArgs e) => Opened?.Invoke(this, e);

    private void OnAudioClosed(object? sender, EventArgs e) => Closed?.Invoke(this, e);

    private void OnAudioPositionChanged(object? sender, double position) => PositionChanged?.Invoke(this, position);

    private void OnAudioNormalizationGainChanged(object? sender, double gain) => NormalizationGainChanged?.Invoke(this, gain);
}
