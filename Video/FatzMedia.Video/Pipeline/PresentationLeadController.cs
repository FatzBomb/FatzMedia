namespace FatzMedia.Video.Pipeline;

/// <summary>
/// Pure rules of the adaptive presentation lead extracted from <see cref="VideoPipeline"/>
/// (Stage 041A, the second 015A seam): how far ahead of the clock a frame is chosen, in display
/// refreshes, and how each 120-frame diagnostics window nudges it. Values and rules are
/// unchanged from the original inline code.
/// </summary>
internal static class PresentationLeadController
{
    public const double DefaultLeadRefreshes = 0.75;
    public const double MinimumLeadRefreshes = 0.50;
    public const double MaximumLeadRefreshes = 1.25;
    public const double IncreaseStepRefreshes = 0.125;
    public const double DecreaseStepRefreshes = 0.0625;
    public const double ClockLateIncreaseThresholdRefreshes = 0.65;
    public const double EarlyDecreaseThresholdRefreshes = 1.75;
    public const double GoodClockLateThresholdRefreshes = 0.35;
    public const int LateSampleIncreaseThreshold = 8;

    /// <summary>The configured lead, or the default when invalid, clamped to the controller's range.</summary>
    public static double InitialLeadRefreshes(double configured) =>
        Math.Clamp(
            double.IsFinite(configured) && configured > 0 ? configured : DefaultLeadRefreshes,
            MinimumLeadRefreshes,
            MaximumLeadRefreshes);

    /// <summary>
    /// The lead in seconds: <paramref name="leadRefreshes"/> refreshes (60 Hz when the refresh is
    /// unknown), at most 45 % of a frame (30 fps when unknown) and at most 30 ms.
    /// </summary>
    public static double LeadSeconds(double refreshIntervalSeconds, double framesPerSecond, double leadRefreshes)
    {
        if (!double.IsFinite(refreshIntervalSeconds) || refreshIntervalSeconds <= 0)
            refreshIntervalSeconds = 1.0 / 60.0;

        var frameDuration = framesPerSecond > 0 ? 1.0 / framesPerSecond : 1.0 / 30.0;
        var maxLead = Math.Clamp(frameDuration * 0.45, 0.0, 0.030);
        return Math.Clamp(refreshIntervalSeconds * leadRefreshes, 0.0, maxLead);
    }

    /// <summary>
    /// How late against the clock a present may be before it counts as late: the larger of 0.65
    /// refreshes and 0.75 frames.
    /// </summary>
    public static double ClockLateThresholdMilliseconds(double refreshIntervalMs, double framesPerSecond)
    {
        var refreshThresholdMs = double.IsFinite(refreshIntervalMs) && refreshIntervalMs > 0
            ? refreshIntervalMs * ClockLateIncreaseThresholdRefreshes
            : 0;
        var frameIntervalMs = framesPerSecond > 0 ? 1000.0 / framesPerSecond : 0;
        var sourceThresholdMs = frameIntervalMs > 0 ? frameIntervalMs * 0.75 : 0;
        return Math.Max(refreshThresholdMs, sourceThresholdMs);
    }

    /// <summary>
    /// The lead after a diagnostics window. It increases a step when presents ran late (enough
    /// late samples, or one more than twice the threshold) and decreases a half step when nothing
    /// was late and frames were consistently early.
    /// </summary>
    public static double Adjust(
        double activeLeadRefreshes,
        int clockLateSamples,
        double maxClockLateMs,
        double maxEarlyMs,
        double refreshIntervalMs,
        double framesPerSecond)
    {
        var lateThresholdMs = ClockLateThresholdMilliseconds(refreshIntervalMs, framesPerSecond);
        var goodLateThresholdMs = refreshIntervalMs * GoodClockLateThresholdRefreshes;
        var earlyDecreaseThresholdMs = refreshIntervalMs * EarlyDecreaseThresholdRefreshes;

        if (clockLateSamples >= LateSampleIncreaseThreshold || maxClockLateMs > lateThresholdMs * 2.0)
            return Math.Min(MaximumLeadRefreshes, activeLeadRefreshes + IncreaseStepRefreshes);
        if (clockLateSamples == 0 && maxClockLateMs < goodLateThresholdMs && maxEarlyMs > earlyDecreaseThresholdMs)
            return Math.Max(MinimumLeadRefreshes, activeLeadRefreshes - DecreaseStepRefreshes);
        return activeLeadRefreshes;
    }
}
