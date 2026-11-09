namespace FatzMedia.Video.Core.Graphics.Composition;

/// <summary>
/// Vertical-blank timing of the display showing a presentation surface (Stage 017A slice 4):
/// the <see cref="System.Diagnostics.Stopwatch"/> timestamp of a recent vertical blank and the
/// refresh period in Stopwatch ticks. <see cref="DisplayKey"/> identifies the display, so surfaces
/// on the same monitor share one timing. <see cref="Resyncs"/> counts how often the source lost the
/// display's rhythm and measured it again (diagnostics).
/// </summary>
public readonly record struct PresentationVsyncTiming(long DisplayKey, long LastVsyncTimestamp, long PeriodTicks, long Resyncs = 0)
{
    /// <summary>Gets whether the timing holds a vertical blank and a positive period.</summary>
    public bool IsValid => LastVsyncTimestamp > 0 && PeriodTicks > 0;

    /// <summary>Gets the refresh period in seconds, or 0 when invalid.</summary>
    public double PeriodSeconds => IsValid ? (double)PeriodTicks / System.Diagnostics.Stopwatch.Frequency : 0;

    /// <summary>The first predicted vertical blank at or after <paramref name="timestamp"/>.</summary>
    public long NextVsyncAtOrAfter(long timestamp)
    {
        if (!IsValid)
            throw new InvalidOperationException("The vertical-blank timing is not valid.");

        if (timestamp <= LastVsyncTimestamp)
            return LastVsyncTimestamp;

        var periods = ((timestamp - LastVsyncTimestamp) + PeriodTicks - 1) / PeriodTicks;
        return LastVsyncTimestamp + (periods * PeriodTicks);
    }

    /// <summary>
    /// Gets whether the last vertical blank is recent enough at <paramref name="now"/> to predict
    /// from: no more than <paramref name="maxAgeTicks"/> old, and not in the future.
    /// </summary>
    public bool IsCurrent(long now, long maxAgeTicks) =>
        IsValid && now >= LastVsyncTimestamp - PeriodTicks && now - LastVsyncTimestamp <= maxAgeTicks;
}
