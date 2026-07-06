namespace FatzBombMedia.MediaControl.Pipeline;

internal static class SeekHelpers
{
    public static double NormalizeSeekTarget(double seconds, double durationSeconds)
    {
        if (!double.IsFinite(seconds) || seconds <= 0)
            return 0;

        return durationSeconds > 0 ? Math.Clamp(seconds, 0, durationSeconds) : seconds;
    }
}