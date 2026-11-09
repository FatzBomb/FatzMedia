using FatzMedia.Video.Core.Abstractions;

namespace FatzMedia.Video.Pipeline;

/// <summary>
/// The audio engine of a Background-mode player (Stage 043A), and of any player whose platform
/// could not load BASS (ENG-033): it never initializes an audio output, never calls BASS, opens
/// nothing, and accepts volume, pitch, tempo and normalization settings without effect. Such a
/// player never opens audio, so the engine only fills the audio slot of the media-engine graph.
/// </summary>
sealed class SilentAudioEngine : IAudioEngine
{
    public AudioEngineState State => AudioEngineState.Stopped;
    public int PlayHandle => 0;
    public int SourceHandle => 0;
    public bool Normalize { get; set; }
    public double NormalizationGain => 1.0;
    public bool IsNormalizationReady => true;
    public int CurrentTempo { get; private set; }
    public double Duration => 0;

    public double Position
    {
        get => 0;
        set { }
    }

    public event EventHandler<double>? NormalizationGainChanged { add { } remove { } }
    public event EventHandler<AudioEngineState>? StateChanged { add { } remove { } }
    public event EventHandler? Opened { add { } remove { } }
    public event EventHandler? Closed { add { } remove { } }
    public event EventHandler<double>? PositionChanged { add { } remove { } }
    public event EventHandler? PlaybackEnded { add { } remove { } }

    /// <summary>Does nothing and reports no output: the player has no audio device.</summary>
    public bool Init(int device = -1, int sampleRate = 44100) => false;

    public bool Open(string filePath) => false;

    public bool Open(IArchiveEntryInfo archiveEntry) => false;

    public bool Play() => false;

    public bool Pause() => false;

    public bool Stop() => false;

    public void Close()
    {
    }

    public void SetVolume(double volume01)
    {
    }

    public void SetPitch(int semitones)
    {
    }

    public void SetTempo(int percent) => CurrentTempo = percent;

    public Task<double> AnalyzePeakQuickAsync(CancellationToken cancellationToken) => Task.FromResult(1.0);

    public Task<double> AnalyzePeakAsync(CancellationToken cancellationToken) => Task.FromResult(1.0);

    public void Dispose()
    {
    }
}
