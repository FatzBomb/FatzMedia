#pragma warning disable IDE0130
namespace FatzMedia.Video;

/// <summary>
/// Raised after the prepared item became the current media.
/// </summary>
public sealed class PreparedNextTransitionedEventArgs(
    MediaInputSource source,
    MediaAnalysisResult? analysis,
    bool isAutomatic) : EventArgs
{
    /// <summary>Gets the source that is now current.</summary>
    public MediaInputSource Source { get; } = source ?? throw new ArgumentNullException(nameof(source));

    /// <summary>Gets the analysis captured during preparation, when available.</summary>
    public MediaAnalysisResult? Analysis { get; } = analysis;

    /// <summary>Gets whether the transition was triggered by the end of the previous media.</summary>
    public bool IsAutomatic { get; } = isAutomatic;
}
