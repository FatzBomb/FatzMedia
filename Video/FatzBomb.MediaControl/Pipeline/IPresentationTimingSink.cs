using FatzBomb.MediaControl.Core.Graphics.Composition;

namespace FatzBomb.MediaControl.Pipeline;

/// <summary>
/// The presentation surfaces on one display and that display's vertical-blank timing (Stage
/// 017A slices 4 and 5). Surfaces on the same monitor share a group, so they present together.
/// </summary>
internal readonly record struct PresentationTimingGroup(
    PresentationVsyncTiming Timing,
    bool IncludesPrimary,
    IReadOnlyList<string> SurfaceIds);

/// <summary>Fills <paramref name="destination"/> (cleared by the caller) with the current groups.</summary>
internal delegate void PresentationTimingSource(List<PresentationTimingGroup> destination);

/// <summary>
/// Lets the presentation coordinator hand the video pipeline the vertical-blank timing of the
/// displays showing its surfaces, for <see cref="VideoFrameTiming.DisplayLocked"/> timing.
/// Internal so the public engine contract does not change.
/// </summary>
internal interface IPresentationTimingSink
{
    /// <summary>
    /// Sets the source of display timing groups, or clears it with null. Called from the video
    /// presentation thread once per display refresh, so it must be cheap.
    /// </summary>
    void SetPresentationTimingSource(PresentationTimingSource? source);
}

/// <summary>Selects how the video presentation loop times frames (Stage 017A slice 4).</summary>
internal interface IVideoFrameTimingControl
{
    VideoFrameTiming FrameTiming { get; set; }
}
