#pragma warning disable IDE0130
namespace FatzMedia.Video;

/// <summary>
/// Immutable snapshot of the depth-one prepared-next slot.
/// </summary>
public sealed record PreparedNextInfo
{
    /// <summary>Gets the snapshot of an empty slot.</summary>
    public static PreparedNextInfo Empty { get; } = new(PreparedNextState.Empty, null, null);

    public PreparedNextInfo(PreparedNextState state, MediaInputSource? source, MediaAnalysisResult? analysis)
    {
        State = state;
        Source = source;
        Analysis = analysis;
    }

    /// <summary>Gets the slot state.</summary>
    public PreparedNextState State { get; }

    /// <summary>Gets the prepared source, or <see langword="null"/> when the slot is empty.</summary>
    public MediaInputSource? Source { get; }

    /// <summary>
    /// Gets the analysis produced during preparation. <see langword="null"/> while analyzing, for
    /// sources the analyzer does not cover (video, CDG, images, tracker formats), or when analysis
    /// was unavailable.
    /// </summary>
    public MediaAnalysisResult? Analysis { get; }
}
