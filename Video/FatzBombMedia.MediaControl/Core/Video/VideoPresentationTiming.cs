namespace FatzBombMedia.MediaControl.Core.Video;

internal static class VideoPresentationTiming
{
    private const double DefaultFrameDurationSeconds = 1.0 / 30.0;

    public static double GetPrimaryFrameLeadTolerance(double fps) => GetFrameLeadTolerance(fps, leadFraction: 0.25, minSeconds: 0.004, maxSeconds: 0.012);

    public static double GetBackgroundFrameLeadTolerance(double fps) =>
        // Background video is visual-only and not audio-mastered, so it can safely keep a little
        // more frame lead than primary video.  The previous 0.75-frame lead caused occasional
        // no-frame ticks on 60fps background loops; those missed presents were visible as small
        // cadence jumps.  A 1.25-frame lookahead keeps the background queue smooth without
        // affecting primary audio/video sync.
        GetFrameLeadTolerance(fps, leadFraction: 1.25, minSeconds: 0.012, maxSeconds: 0.05);

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
