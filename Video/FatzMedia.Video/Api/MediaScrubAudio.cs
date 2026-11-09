#pragma warning disable IDE0130
namespace FatzMedia.Video;

/// <summary>What audio does during an interactive scrub (seek-bar drag).</summary>
public enum MediaScrubAudio
{
    /// <summary>Audio plays from every drag position, as an ordinary seek does.</summary>
    Audible = 0,

    /// <summary>
    /// Audio is muted while the drag keeps moving and comes back when it pauses (no scrub seek
    /// for a short quiet period) or ends, so a held drag plays from where it rests.
    /// </summary>
    SilentWhileMoving = 1,
}
