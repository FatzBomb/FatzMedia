
using FatzBomb.MediaControl.Core.Cdg;

namespace FatzBomb.MediaControl.Core.Abstractions;
/// <summary>
/// UI-agnostic CDG decoding/engine abstraction.
/// Implementations should not depend on WPF/WinUI types.
/// </summary>
public interface ICdgEngine : IDisposable
{
    // Geometry
    int FrameWidth { get; }
    int FrameHeight { get; }
    /// <summary>
    /// Cached full-viewport CDG background wash color in ARGB format. This value changes
    /// only when the decoded CDG background color, background override, or presentation
    /// opacity changes; lyric frame invalidations alone must not recalculate it.
    /// </summary>
    uint BackgroundWashArgb { get; }

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

    // Visual tweaks. Colors are packed as ARGB values so the core remains UI-framework independent.
    byte BackgroundOpacity { get; set; }
    uint? BackgroundOverrideArgb { get; set; }

    // Notify UI layers that a new lyric frame is available to present.
    event EventHandler? FrameInvalidated;

    // Notify renderers that the cached full-viewport wash color changed.
    event EventHandler? BackgroundWashChanged;

    event EventHandler<CdgEngineState>? StateChanged;
    event EventHandler? Opened;
    event EventHandler? Closed;
    event EventHandler? PlaybackStarted;
}
