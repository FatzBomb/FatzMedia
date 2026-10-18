using FatzBomb.MediaControl.Core.Graphics.Composition;

namespace FatzBomb.MediaControl.Core.Graphics.Cadence;

/// <summary>One display refresh the presentation loop presents for: when to wake, and the refresh.</summary>
internal readonly record struct DisplayLockedSlot(long VsyncTimestamp, long WakeTimestamp, long PeriodTicks);

/// <summary>
/// Scheduling rules of display-locked video timing (Stage 017A slices 4 and 5): which refresh
/// the loop aims for next, when to wake for it, and which media time a refresh shows.
/// </summary>
internal static class DisplayLockedTiming
{
    /// <summary>
    /// A vertical-blank timestamp older than this is not used to predict refreshes. Predictions
    /// from a measured period hold for far longer; a second rides out the timing source measuring
    /// the rhythm again, so playback does not switch timing paths for a few refreshes.
    /// </summary>
    public static readonly TimeSpan MaxVsyncAge = TimeSpan.FromSeconds(1);

    /// <summary>Lead the loop starts with before a refresh, before measuring its own work.</summary>
    public static readonly TimeSpan InitialBudget = TimeSpan.FromMilliseconds(4);

    private static readonly TimeSpan MinBudget = TimeSpan.FromMilliseconds(2);
    private static readonly TimeSpan MaxBudget = TimeSpan.FromMilliseconds(12);

    /// <summary>
    /// The next refresh to present for: the first one at least <paramref name="budgetTicks"/>
    /// after <paramref name="now"/> (time to select, compose and present) and after
    /// <paramref name="lastVsync"/>, the one already handled. False when the timing is missing or
    /// too old to predict from.
    /// </summary>
    public static bool TryGetNextSlot(PresentationVsyncTiming timing, long now, long budgetTicks, long lastVsync, out DisplayLockedSlot slot)
    {
        slot = default;
        var maxAge = (long)(MaxVsyncAge.TotalSeconds * System.Diagnostics.Stopwatch.Frequency);
        if (!timing.IsCurrent(now, maxAge))
            return false;

        // Half a period past the refresh already handled: the grid's prediction of that same
        // refresh moves a little with each blank observed and must not count as the next one.
        var budget = Math.Clamp(budgetTicks, 0, timing.PeriodTicks - 1);
        var earliest = Math.Max(now + budget, lastVsync + (timing.PeriodTicks / 2));
        var vsync = timing.NextVsyncAtOrAfter(earliest);
        slot = new DisplayLockedSlot(vsync, vsync - budget, timing.PeriodTicks);
        return true;
    }

    /// <summary>
    /// The media time a refresh shows frames up to: the media time at the refresh plus half a
    /// refresh, so each frame starts on the refresh nearest its presentation time (on average
    /// neither early nor late). This is the cadence model's phase term; it moves which refresh
    /// shows a frame, never how many refreshes frames are held.
    /// </summary>
    public static double GetSelectionTime(double mediaTimeAtVsync, double periodSeconds) =>
        mediaTimeAtVsync + (periodSeconds / 2);

    /// <summary>
    /// The lead to wake with before a refresh: one and a half times the measured selection and
    /// present work plus a millisecond, between 2 and 12 ms and under 60 % of a refresh.
    /// </summary>
    public static long GetBudgetTicks(double averageWorkSeconds, long periodTicks)
    {
        var frequency = System.Diagnostics.Stopwatch.Frequency;
        var seconds = double.IsFinite(averageWorkSeconds) && averageWorkSeconds > 0
            ? (averageWorkSeconds * 1.5) + 0.001
            : InitialBudget.TotalSeconds;
        var clamped = Math.Clamp(seconds, MinBudget.TotalSeconds, MaxBudget.TotalSeconds);
        var ticks = (long)(clamped * frequency);
        return periodTicks > 0 ? Math.Min(ticks, (long)(periodTicks * 0.6)) : ticks;
    }
}
