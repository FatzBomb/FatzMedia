#pragma warning disable IDE0130
namespace FatzMedia.Video;

/// <summary>
/// Lifecycle of the depth-one prepared-next slot.
/// </summary>
public enum PreparedNextState
{
    /// <summary>No item is prepared.</summary>
    Empty = 0,

    /// <summary>A candidate was accepted and its analysis is running.</summary>
    Analyzing = 1,

    /// <summary>The candidate is ready to transition.</summary>
    Ready = 2,

    /// <summary>The prepared item is being loaded as the current media.</summary>
    Transitioning = 3,

    /// <summary>
    /// The prepared file changed on disk after preparation; the host must prepare it again.
    /// </summary>
    Stale = 4
}
