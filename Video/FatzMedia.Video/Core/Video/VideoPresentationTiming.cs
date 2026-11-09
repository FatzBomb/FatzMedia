namespace FatzMedia.Video.Core.Video;

internal static class VideoPresentationTiming
{
    private const double DefaultFrameDurationSeconds = 1.0 / 30.0;

    public static double GetPrimaryFrameLeadTolerance(double fps) => GetFrameLeadTolerance(fps, leadFraction: 0.25, minSeconds: 0.004, maxSeconds: 0.012);

    public static double GetPrimaryFrameSelectionLeadLimit(double fps) => GetFrameLeadTolerance(fps, leadFraction: 0.25, minSeconds: 0.004, maxSeconds: 0.012);

    public static double GetPrimaryPresentationInterval(double fps, double fallbackDisplayIntervalSeconds) =>
        !double.IsFinite(fps) || fps <= 0
            ? double.IsFinite(fallbackDisplayIntervalSeconds) && fallbackDisplayIntervalSeconds > 0
            ? fallbackDisplayIntervalSeconds
            : DefaultFrameDurationSeconds
            : Math.Clamp(1.0 / fps, 1.0 / 120.0, 1.0 / 15.0);

    public static double GetBackgroundPresentationInterval(double fps) =>
        !double.IsFinite(fps) || fps <= 0
            ? DefaultFrameDurationSeconds
            : Math.Clamp(1.0 / fps, 1.0 / 120.0, 1.0 / 15.0);

    private static double GetFrameLeadTolerance(double fps, double leadFraction, double minSeconds, double maxSeconds)
    {
        if (!double.IsFinite(fps) || fps <= 0)
            return DefaultFrameDurationSeconds;

        var frameDuration = 1.0 / fps;
        return Math.Clamp(frameDuration * leadFraction, minSeconds, maxSeconds);
    }
}
