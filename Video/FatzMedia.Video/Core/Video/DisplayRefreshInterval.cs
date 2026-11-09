namespace FatzMedia.Video.Core.Video;

/// <summary>
/// Validates a display refresh-rate ratio and converts it to a presentation
/// interval. This is platform-neutral so non-Windows display implementations can
/// apply the same safety bounds.
/// </summary>
public static class DisplayRefreshInterval
{
    public const double FallbackSeconds = 1.0 / 60.0;

    /// <summary>
    /// Returns a usable presentation interval for <paramref name="numerator"/> /
    /// <paramref name="denominator"/>, or the 60Hz fallback when the derived rate
    /// is outside the supported 23-600Hz range.
    /// </summary>
    public static double FromRefreshRate(uint numerator, uint denominator)
    {
        if (numerator == 0 || denominator == 0)
            return FallbackSeconds;

        var refreshHz = (double)numerator / denominator;
        return !double.IsFinite(refreshHz) || refreshHz < 23 || refreshHz > 600
            ? FallbackSeconds
            : 1.0 / refreshHz;
    }
}
