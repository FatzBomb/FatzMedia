#pragma warning disable IDE0130
namespace FatzMedia.Video;

/// <summary>How the video presentation loop decides when each decoded frame is shown (Stage 017A).</summary>
public enum VideoFrameTiming
{
    /// <summary>
    /// The loop wakes on the video's own frame times and presents each frame as it comes due;
    /// the display shows it at its next refresh. Works with every renderer and surface.
    /// </summary>
    SourceCadence = 0,

    /// <summary>
    /// The loop wakes just before each refresh of the display showing the video and presents the
    /// frame for the moment that refresh reaches the screen, using the display's vertical-blank
    /// timing from the renderer and a jitter-filtered media clock. Every frame keeps the same
    /// number of refreshes where the rates allow it (3:2 for 24 fps on 60 Hz, never 3:3:1).
    /// Each display runs its own timing when outputs are on monitors with different rates.
    /// Falls back to <see cref="SourceCadence"/> while the renderer reports no vertical-blank
    /// timing (software output, WPF-composited surfaces).
    /// </summary>
    DisplayLocked = 1,
}
