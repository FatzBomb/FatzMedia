namespace FatzMedia.Video.Pipeline;

static class SeekHelpers
{
    public static double NormalizeSeekTarget(double seconds, double durationSeconds) =>
        !double.IsFinite(seconds) || seconds <= 0 ? 0 : durationSeconds > 0 ? Math.Clamp(seconds, 0, durationSeconds) : seconds;
}
