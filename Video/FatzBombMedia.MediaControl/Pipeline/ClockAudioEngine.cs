
using FatzBombMedia.MediaControl.Core.Abstractions;

namespace FatzBombMedia.MediaControl.Pipeline;

internal sealed class ClockAudioEngine(PlaybackClock clock) : IAudioEngine
{
    private readonly PlaybackClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    public AudioEngineState State { get; private set; } = AudioEngineState.Stopped;
    public int PlayHandle => 0;
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

    public event EventHandler<double>? NormalizationGainChanged;
    public event EventHandler<AudioEngineState>? StateChanged;
    public event EventHandler? Opened;
    public event EventHandler? Closed;
    public event EventHandler<double>? PositionChanged;
    event EventHandler? IAudioEngine.PlaybackEnded
    {
        add { }
        remove { }
    }

    public bool Init(int device = -1, int sampleRate = 44100) => true;
    public bool Open(string filePath) => false;
    public bool Open(IArchiveEntryInfo archiveEntry) => false;

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

    public bool SetVolume(double volume01) => true;
    public bool SetPitch(int semitones) => false;

    public bool SetTempo(int percent)
    {
        _clock.SetTempo(percent);
        return true;
    }

    public Task<double> AnalyzePeakQuickAsync(CancellationToken cancellationToken)
    {
        NormalizationGainChanged?.Invoke(this, NormalizationGain);
        return Task.FromResult(NormalizationGain);
    }

    public Task<double> AnalyzePeakAsync(CancellationToken cancellationToken)
    {
        NormalizationGainChanged?.Invoke(this, NormalizationGain);
        return Task.FromResult(NormalizationGain);
    }

    public void Dispose() => Close();

    private void SetState(AudioEngineState state)
    {
        if (State == state)
        {
            return;
        }

        State = state;
        StateChanged?.Invoke(this, state);
    }
}
