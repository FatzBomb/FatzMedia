#pragma warning disable IDE0130
namespace FatzMedia.Video;

/// <summary>
/// Describes reusable media analysis and its configured playable source window.
/// </summary>
public sealed record MediaAnalysisResult
{
    public MediaAnalysisResult(
        double sourceDuration,
        double playableStart,
        double playableEnd,
        double effectiveDuration,
        IEnumerable<SilenceRegion>? silenceRegions,
        double normalizationGain)
    {
        SourceDuration = sourceDuration;
        PlayableStart = playableStart;
        PlayableEnd = playableEnd;
        EffectiveDuration = effectiveDuration;
        SilenceRegions = Array.AsReadOnly((silenceRegions ?? []).ToArray());
        NormalizationGain = normalizationGain;
    }

    /// <summary>
    /// Gets the measured duration of the complete source.
    /// </summary>
    public double SourceDuration { get; }

    /// <summary>
    /// Gets the source-time start of the configured playable window.
    /// </summary>
    public double PlayableStart { get; }

    /// <summary>
    /// Gets the source-time end of the configured playable window.
    /// </summary>
    public double PlayableEnd { get; }

    /// <summary>
    /// Gets the duration of the configured playable source window.
    /// </summary>
    public double PlayableDuration => PlayableEnd - PlayableStart;

    /// <summary>
    /// Gets the host-facing duration used for progress and scheduling.
    /// </summary>
    public double EffectiveDuration { get; }

    /// <summary>
    /// Gets the detected source-time silence regions.
    /// </summary>
    public IReadOnlyList<SilenceRegion> SilenceRegions { get; }

    /// <summary>
    /// Gets the analyzed normalization gain multiplier.
    /// </summary>
    public double NormalizationGain { get; }
}
