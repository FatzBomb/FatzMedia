namespace FatzBombMedia.MediaControl.Core.Abstractions;

using FatzBombMedia.MediaControl.Core.Cdg;

/// <summary>
/// UI-agnostic CDG decoding/engine abstraction.
/// Implementations should not depend on WPF/WinUI types.
/// </summary>
public interface ICdgEngine : IDisposable
{
    // Geometry
    int FrameWidth { get; }
    int FrameHeight { get; }
    ReadOnlyMemory<int> FrameArgb { get; }

    /// <summary>
    /// The CDG background tint color (premultiplied ARGB) used for the full-rectangle
    /// GPU tint pass behind the CDG graphics.  Updated when background color or opacity
    /// changes via CDG packet commands.
    /// </summary>
    uint TintArgb { get; }

    /// <summary>
    /// Copies the current rendered ARGB frame into the supplied destination.
    /// Implementations should make this copy atomic with packet replay.
    /// </summary>
    /// <param name="destination">Destination buffer with at least FrameWidth * FrameHeight entries.</param>
    void CopyFrameArgb(Span<int> destination);

    // Load CDG alongside audio (paired with an audio channel/engine)
    void LoadPaired(string filePath, int audioStreamHandle);
    void LoadPaired(IArchiveEntryInfo archiveEntry, int audioStreamHandle);

    // Load CDG (no pairing metadata required)
    void Load(string filePath);
    void Load(IArchiveEntryInfo archiveEntry);

    void Play();
    void Pause();
    void Stop();
    void Reset();

    // Optional seek support when synchronized to an external clock
    double TotalDurationSeconds { get; }

    /// <summary>
    /// Renders CDG graphics at the specified time position.
    /// This is used for precise synchronization with audio playback.
    /// </summary>
    /// <param name="seconds">Time position in seconds to render at</param>
    void RenderAtSeconds(double seconds);

    /// <summary>
    /// Renders CDG graphics at the specified time position, optionally rebuilding
    /// from packet zero for an authoritative seek frame.
    /// </summary>
    /// <param name="seconds">Time position in seconds to render at</param>
    /// <param name="forceRebuild">True when the current CDG state should be replayed from the beginning</param>
    void RenderAtSeconds(double seconds, bool forceRebuild);

    // Visual tweaks (use ARGB to avoid UI color dependencies)
    byte BackgroundOpacity { get; set; }
    uint? BackgroundOverrideArgb { get; set; }

    // Notify UI layers that a new frame is available to present
    event EventHandler? FrameInvalidated;

    event EventHandler<CdgEngineState>? StateChanged;
    event EventHandler? Opened;
    event EventHandler? Closed;
    event EventHandler<double>? PositionChanged;
    event EventHandler? PlaybackStarted;
}
