namespace FatzBombMedia.MediaControl.Core.Abstractions;
/// <summary>
/// UI-agnostic audio playback engine abstraction.
/// Implementations provide decode/playback, transport controls,
/// and pitch/tempo/volume controls. No WPF types here.
/// </summary>
public interface IAudioEngine : IDisposable
{
    /// <summary>
    /// Gets the current state of the audio engine (e.g., Playing, Paused, Stopped).
    /// </summary>
    AudioEngineState State { get; }

    /// <summary>
    /// Gets the underlying play handle if available (0 when closed).
    /// Useful for integration with other components (e.g., CDG sync).
    /// </summary>
    int PlayHandle { get; }

    /// <summary>
    /// Enables/disables normalization when applying volume.
    /// </summary>
    bool Normalize { get; set; }

    /// <summary>
    /// Current normalization gain (e.g., 0.95/peak), defaults to 1.
    /// </summary>
    double NormalizationGain { get; }

    /// <summary>
    /// True when normalization does not need to delay playback.
    /// </summary>
    bool IsNormalizationReady { get; }

    /// <summary>
    /// Event raised when normalization gain changes (after analysis completes).
    /// </summary>
    event EventHandler<double>? NormalizationGainChanged;

    /// <summary>
    /// Quick/approximate peak analysis intended for pre-play use. Should be fast.
    /// </summary>
    Task<double> AnalyzePeakQuickAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Full/best-effort peak analysis. May be slower but more accurate.
    /// </summary>
    Task<double> AnalyzePeakAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Initializes the audio engine/device.
    /// </summary>
    /// <param name="device">Device index (-1 default).</param>
    /// <param name="sampleRate">Device sample rate (e.g., 44100).</param>
    /// <returns>True if initialized.</returns>
    bool Init(int device = -1, int sampleRate = 44100);

    /// <summary>
    /// Opens a media file for playback.
    /// </summary>
    bool Open(string filePath);

    /// <summary>
    /// Opens an archive entry (e.g., from zip/7z) for playback.
    /// Implementations may buffer the entry to memory.
    /// </summary>
    bool Open(IArchiveEntryInfo archiveEntry);

    /// <summary>
    /// Begins playback.
    /// </summary>
    bool Play();

    /// <summary>
    /// Pauses playback.
    /// </summary>
    bool Pause();

    /// <summary>
    /// Stops playback and resets position to zero if possible.
    /// </summary>
    bool Stop();

    /// <summary>
    /// Closes any open streams/handles but keeps device initialized.
    /// </summary>
    void Close();

    /// <summary>
    /// Total media duration in seconds if known, otherwise 0.
    /// </summary>
    double Duration { get; }

    /// <summary>
    /// Current playback position in seconds.
    /// </summary>
    double Position { get; set; }

    /// <summary>
    /// Sets 0..1 base volume on the underlying stream.
    /// Implementations should apply normalization to the effective volume
    /// when Normalize is enabled.
    /// </summary>
    bool SetVolume(double volume01);

    /// <summary>
    /// Sets pitch in semitones [-60..+60] if supported.
    /// </summary>
    bool SetPitch(int semitones);

    /// <summary>
    /// Sets tempo as percentage (-95..5000). 0 means normal speed.
    /// </summary>
    bool SetTempo(int percent);

    /// <summary>
    /// Gets the current tempo as percentage (0 = normal speed).
    /// </summary>
    int CurrentTempo { get; }

    /// <summary>
    /// Raised when the audio engine state changes (e.g., Playing, Paused, Stopped).
    /// </summary>
    event EventHandler<AudioEngineState>? StateChanged;

    /// <summary>
    /// Raised when the audio engine is successfully opened.
    /// </summary>
    event EventHandler? Opened;

    /// <summary>
    /// Raised when the audio engine is closed.
    /// </summary>
    event EventHandler? Closed;

    /// <summary>
    /// Raised when the playback position changes (seek, playback, etc).
    /// </summary>
    event EventHandler<double>? PositionChanged;

    event EventHandler? PlaybackEnded;
}

public enum AudioEngineState
{
    Null = 0,
    Playing,
    Paused,
    Stopped,
    Stalled
}
