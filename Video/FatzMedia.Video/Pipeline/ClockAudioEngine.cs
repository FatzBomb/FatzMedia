
using FatzMedia.Video.Core.Abstractions;

namespace FatzMedia.Video.Pipeline;

// Compatibility adapter for the existing non-null Audio API. Internal timing and tempo paths
// use IClockSource directly rather than treating a virtual clock as an audio engine.
sealed class ClockAudioEngine(IClockSource clock) : IAudioEngine, IAudioLoopControl
{
    public bool LoopPlayback { get; set; }
    readonly IClockSource _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    public AudioEngineState State { get; private set; } = AudioEngineState.Stopped;
    public bool Normalize { get; set; }
    public double NormalizationGain => 1.0;
    public bool IsNormalizationReady => true;
    public int CurrentTempo => _clock.TempoPercent;
    public double Duration => _clock.DurationSeconds;

    public double Position
    {
        get => _clock.PositionSeconds;
        set
        {
            _clock.Seek(value);
            PositionChanged?.Invoke(this, _clock.PositionSeconds);
        }
    }

#pragma warning disable CS0067
    public event EventHandler<double>? NormalizationGainChanged;
#pragma warning restore CS0067
    public event EventHandler<AudioEngineState>? StateChanged;
    public event EventHandler? Opened;
    public event EventHandler? Closed;
    public event EventHandler<double>? PositionChanged;
#pragma warning disable CS0067
    public event EventHandler? PlaybackEnded;
#pragma warning restore CS0067

    public bool Init(int sampleRate = 44100) =>
        throw new NotSupportedException("ClockAudioEngine does not support device initialization.");
    public bool Open(string filePath) =>
        throw new NotSupportedException("ClockAudioEngine does not support opening audio files.");
    public bool Open(IArchiveEntryInfo archiveEntry) =>
        throw new NotSupportedException("ClockAudioEngine does not support opening audio archives.");

    public bool Play()
    {
        _clock.Start(_clock.PositionSeconds);
        SetState(AudioEngineState.Playing);
        Opened?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public bool Pause()
    {
        _clock.Pause();
        SetState(AudioEngineState.Paused);
        return true;
    }

    public bool Stop()
    {
        _clock.Stop();
        SetState(AudioEngineState.Stopped);
        PositionChanged?.Invoke(this, 0);
        return true;
    }

    public void Close()
    {
        SetState(AudioEngineState.Stopped);
        Closed?.Invoke(this, EventArgs.Empty);
    }

    public void SetVolume(double volume01) =>
        throw new NotSupportedException("ClockAudioEngine does not support volume control.");
    public void SetPitch(int semitones) =>
        throw new NotSupportedException("ClockAudioEngine does not support pitch control.");

    public void SetTempo(int percent) => _clock.SetTempo(percent);

    public Task<double> AnalyzePeakQuickAsync(CancellationToken cancellationToken) =>
        throw new NotSupportedException("ClockAudioEngine does not support peak analysis.");

    public Task<double> AnalyzePeakAsync(CancellationToken cancellationToken) =>
        throw new NotSupportedException("ClockAudioEngine does not support peak analysis.");

    public void Dispose() => Close();

    void SetState(AudioEngineState state)
    {
        if (State == state)
            return;

        State = state;
        StateChanged?.Invoke(this, state);
    }
}
