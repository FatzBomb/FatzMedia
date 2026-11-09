namespace FatzMedia.Video.Pipeline;

/// <summary>
/// The post-seek recovery state machine extracted from <see cref="VideoPipeline"/> (Stage 041A,
/// the third 015A seam). After a seek, presentation tolerates late frames for a window whose
/// length depends on how full the video queue is, then skips the adaptive lead controller for
/// two diagnostics windows. Times are Stopwatch timestamps passed in by the caller. Thread-safe:
/// the seek path begins recovery while the presentation loop reads it. Values and rules are
/// unchanged from the original inline code.
/// </summary>
sealed class PostSeekRecovery
{
    public const int RecoveryMilliseconds = 450;
    public const int UnderfilledRecoveryMilliseconds = 700;
    public const int EmptyQueueRecoveryMilliseconds = 900;
    public const int WarmupDecodeBudgetMilliseconds = 260;
    public const int WarmupRetryBudgetMilliseconds = 220;
    public const int SharedDemuxerPrerollBudgetMilliseconds = 2500;
    public const int MinimumBufferedFrames = 3;
    public const int AdaptiveSuppressionWindows = 2;

    long _recoveryUntilTimestamp;
    int _suppressionWindowsRemaining;

    /// <summary>What a diagnostics window does with the adaptive lead controller.</summary>
    public enum WindowAction
    {
        /// <summary>Recovery is active: the window's numbers reflect the seek.</summary>
        Recovering,
        /// <summary>Just after recovery: the window is skipped (one fewer remains).</summary>
        Suppressed,
        /// <summary>Normal: the controller may adjust the lead.</summary>
        Adapt,
    }

    /// <summary>The recovery window for the queue a seek left: longer for an empty or underfilled queue.</summary>
    public static int RecoveryMillisecondsFor(int bufferedFrames) =>
        bufferedFrames <= 0
            ? EmptyQueueRecoveryMilliseconds
            : bufferedFrames < MinimumBufferedFrames
                ? UnderfilledRecoveryMilliseconds
                : RecoveryMilliseconds;

    /// <summary>How many frames the post-seek warm-up decodes towards: at least the recovery minimum, at most the normal target.</summary>
    public static int WarmupTargetFrames(int targetBufferedFrames, int minBufferedFrames) =>
        Math.Min(targetBufferedFrames, Math.Max(MinimumBufferedFrames, minBufferedFrames));

    /// <summary>Gets the suppression windows left (diagnostics).</summary>
    public int SuppressionWindowsRemaining => Volatile.Read(ref _suppressionWindowsRemaining);

    /// <summary>Starts recovery after a seek; returns the window length in milliseconds.</summary>
    public int Begin(int bufferedFrames, long now, long frequency)
    {
        var milliseconds = RecoveryMillisecondsFor(bufferedFrames);
        var until = now + (long)(milliseconds / 1000.0 * frequency);
        _ = Interlocked.Exchange(ref _recoveryUntilTimestamp, until);
        _ = Interlocked.Exchange(ref _suppressionWindowsRemaining, AdaptiveSuppressionWindows);
        return milliseconds;
    }

    public void Reset()
    {
        _ = Interlocked.Exchange(ref _recoveryUntilTimestamp, 0);
        _ = Interlocked.Exchange(ref _suppressionWindowsRemaining, 0);
    }

    /// <summary>Whether the recovery window is open at <paramref name="now"/>.</summary>
    public bool IsActive(long now)
    {
        var until = Interlocked.Read(ref _recoveryUntilTimestamp);
        return until > 0 && now < until;
    }

    /// <summary>Late frames are kept (not dropped) during recovery and the suppression windows after it.</summary>
    public bool ShouldSuppressLateFrameDrop(long now) => IsActive(now) || SuppressionWindowsRemaining > 0;

    /// <summary>
    /// Decides a finished diagnostics window: <see cref="WindowAction.Recovering"/> during
    /// recovery, then <see cref="WindowAction.Suppressed"/> for the suppression windows (using
    /// one; <paramref name="remaining"/> says how many are left), then
    /// <see cref="WindowAction.Adapt"/>.
    /// </summary>
    public WindowAction NextWindow(long now, out int remaining)
    {
        remaining = 0;
        if (IsActive(now))
            return WindowAction.Recovering;
        if (SuppressionWindowsRemaining <= 0)
            return WindowAction.Adapt;

        remaining = Math.Max(0, Interlocked.Decrement(ref _suppressionWindowsRemaining));
        return WindowAction.Suppressed;
    }
}
