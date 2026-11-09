
using FatzMedia.Video.Core.Cdg;

namespace FatzMedia.Video.Core.Abstractions;
/// <summary>
/// UI-agnostic CDG decoding/engine abstraction.
/// Implementations should not depend on WPF/WinUI types.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1716", Justification = "Stop is the natural name; a C#-only API where the VB keyword clash does not matter and a rename would break every host.")]
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

    /// <summary>Loads CDG alongside audio. The BASS handle is not used: call <see cref="Load(string)"/>.</summary>
    [Obsolete("The audio handle is BASS-specific and unused; call Load. Removed with BASS (037A).")]
    void LoadPaired(string filePath, int audioStreamHandle);

    /// <summary>Loads CDG alongside audio. The BASS handle is not used: call <see cref="Load(IArchiveEntryInfo)"/>.</summary>
    [Obsolete("The audio handle is BASS-specific and unused; call Load. Removed with BASS (037A).")]
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
    /// Renders CDG graphics at the specified time position, optionally as an authoritative seek
    /// frame.
    /// </summary>
    /// <param name="seconds">Time position in seconds to render at</param>
    /// <param name="forceRebuild">
    /// True for an authoritative frame: the state is guaranteed to equal a replay from the first
    /// packet and the frame is invalidated even if nothing changed. <see cref="Cdg.CdgEngine"/>
    /// restores the nearest replay checkpoint when it has to move back (Stage 022A slice 4).
    /// </param>
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
