namespace FatzMedia.Video.Pipeline;

/// <summary>
/// Pure video seek policy extracted from <see cref="VideoPipeline"/> (Stage 015A slice 1):
/// target normalization, superseding-target threshold, seek-frame acceptance, and decode
/// budgets. Values and rules are unchanged from the original inline code.
/// </summary>
internal static class VideoSeekPolicy
{
    /// <summary>Targets closer than this to the active target do not supersede it.</summary>
    public const double TargetChangeThresholdSeconds = 0.025;

    public const int HardwarePreviewDecodeBudgetMilliseconds = 85;
    public const int HardwareSettleDecodeBudgetMilliseconds = 125;
    public const int SoftwarePreviewDecodeBudgetMilliseconds = 65;
    public const int SoftwareSettleDecodeBudgetMilliseconds = 95;
    public const int HardwarePreviewDecodeMaxAttempts = 32;
    public const int HardwareSettleDecodeMaxAttempts = 72;
    public const int SoftwarePreviewDecodeMaxAttempts = 20;
    public const int SoftwareSettleDecodeMaxAttempts = 44;

    /// <summary>Non-finite and negative targets become 0; known durations clamp the target.</summary>
    public static double NormalizeTarget(double seconds, double duration) =>
        !double.IsFinite(seconds) || seconds <= 0 ? 0 : duration > 0 ? Math.Clamp(seconds, 0, duration) : seconds;

    public static bool IsMeaningfullyDifferentTarget(double target, double previousTarget) =>
        double.IsNaN(previousTarget) || Math.Abs(target - previousTarget) > TargetChangeThresholdSeconds;

    /// <summary>
    /// Preview seeks may show a nearby frame on either side of the target. Commit seeks never
    /// accept a frame earlier than the target (beyond 1 ms), so long-GOP files do not resume
    /// far behind the requested position.
    /// </summary>
    public static bool IsFrameAcceptable(double frameTime, double target, double maxFrameDistance, bool requireAtOrAfterTarget)
    {
        if (Math.Abs(frameTime - target) > maxFrameDistance)
            return false;

        return !requireAtOrAfterTarget || frameTime + 0.001 >= target;
    }

    /// <summary>
    /// Whether a seek may drop the oldest queued frame to make room under the queue cap: only when
    /// it is before the target and its successor is too, so the successor is at least as close and
    /// the best and fallback picks are unchanged.
    /// </summary>
    public static bool CanDropQueuedSeekFrame(double headFrameTime, double nextFrameTime, double target) =>
        headFrameTime < target && nextFrameTime >= headFrameTime && nextFrameTime <= target;

    /// <summary>
    /// Where an active scrub settles (Stage 035A): on the frame its last preview showed, so
    /// playback carries on from the picture on screen, or on the target when no preview frame
    /// was shown. A lone click never gets here; it seeks exactly.
    /// </summary>
    public static double GetScrubSettleTarget(double target, double shownPreviewFrameTime) =>
        double.IsFinite(shownPreviewFrameTime) && shownPreviewFrameTime >= 0 ? shownPreviewFrameTime : target;

    public static int GetDecodeBudgetMilliseconds(bool useFastPreviewBudget, bool isHardwareAccelerated) => useFastPreviewBudget
        ? isHardwareAccelerated ? HardwarePreviewDecodeBudgetMilliseconds : SoftwarePreviewDecodeBudgetMilliseconds
        : isHardwareAccelerated ? HardwareSettleDecodeBudgetMilliseconds : SoftwareSettleDecodeBudgetMilliseconds;

    public static int GetDecodeMaxAttempts(bool useFastPreviewBudget, bool isHardwareAccelerated) => useFastPreviewBudget
        ? isHardwareAccelerated ? HardwarePreviewDecodeMaxAttempts : SoftwarePreviewDecodeMaxAttempts
        : isHardwareAccelerated ? HardwareSettleDecodeMaxAttempts : SoftwareSettleDecodeMaxAttempts;
}

/// <summary>
/// Adaptive interval of the latest-wins video seek pump (Stage 015A slice 1): an exponential
/// moving average of seek processing time, scaled and padded, clamped to 75..300 ms. Unchanged
/// from the original inline code; thread-safe.
/// </summary>
internal sealed class AdaptiveSeekPumpTiming
{
    public const int DefaultIntervalMilliseconds = 75;
    public const int MinIntervalMilliseconds = 75;
    public const int MaxIntervalMilliseconds = 300;
    public const double IntervalMultiplier = 1.5;
    public const double IntervalPaddingMilliseconds = 25.0;
    public const double EmaPreviousWeight = 0.75;
    public const double EmaCurrentWeight = 0.25;

    private readonly Lock _gate = new();
    private double _processingMilliseconds;

    public double ProcessingMilliseconds
    {
        get
        {
            lock (_gate)
                return _processingMilliseconds;
        }
    }

    public int IntervalMilliseconds
    {
        get
        {
            var measured = ProcessingMilliseconds;
            if (measured <= 0 || !double.IsFinite(measured))
                return DefaultIntervalMilliseconds;

            var interval = (measured * IntervalMultiplier) + IntervalPaddingMilliseconds;
            return (int)Math.Round(Math.Clamp(interval, MinIntervalMilliseconds, MaxIntervalMilliseconds));
        }
    }

    public void Record(TimeSpan elapsed)
    {
        var milliseconds = elapsed.TotalMilliseconds;
        if (milliseconds <= 0 || !double.IsFinite(milliseconds))
            return;

        lock (_gate)
        {
            _processingMilliseconds = _processingMilliseconds <= 0
                ? milliseconds
                : (_processingMilliseconds * EmaPreviousWeight) + (milliseconds * EmaCurrentWeight);
        }
    }

    public void Reset()
    {
        lock (_gate)
            _processingMilliseconds = 0;
    }
}
