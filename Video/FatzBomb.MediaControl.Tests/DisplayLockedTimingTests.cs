using System.Diagnostics;
using FatzBomb.MediaControl.Core.Graphics.Cadence;
using FatzBomb.MediaControl.Core.Graphics.Composition;

namespace FatzBomb.MediaControl.Tests;

/// <summary>
/// Stage 017A slice 4: display-locked video timing. The filtered media clock keeps an even
/// cadence through clock jitter, follows real drift and tempo, and restarts on jumps; the slot
/// rules aim for the right refresh.
/// </summary>
public sealed class DisplayLockedTimingTests
{
    private static readonly long Frequency = Stopwatch.Frequency;

    [Fact]
    public void AnEvenClock_PassesThroughUnchanged()
    {
        var clock = new VsyncMediaClock(Frequency);
        var period = Ticks(1 / 60.0);
        for (var vsync = 0; vsync < 600; vsync++)
        {
            var media = 10 + (vsync / 60.0);
            Assert.Equal(media, clock.Update(Start + (vsync * period), media), 6);
        }

        Assert.Equal(0, clock.Restarts);
    }

    [Fact]
    public void ClockJitter_IsAveragedOut()
    {
        var clock = new VsyncMediaClock(Frequency);
        var random = new Random(3);
        var period = Ticks(1 / 59.94);
        double rawError = 0, filteredError = 0;
        var samples = 0;
        for (var vsync = 0; vsync < 3600; vsync++)
        {
            var media = vsync / 59.94;
            var noise = ((random.NextDouble() * 2) - 1) * 0.002; // +-2 ms read jitter
            var filtered = clock.Update(Start + (vsync * period), media + noise);
            if (vsync < 120)
                continue; // warm-up

            rawError += noise * noise;
            filteredError += (filtered - media) * (filtered - media);
            samples++;
        }

        var rawRms = Math.Sqrt(rawError / samples);
        var filteredRms = Math.Sqrt(filteredError / samples);
        Assert.True(filteredRms < rawRms * 0.4, $"filtered {filteredRms * 1000:F3} ms, raw {rawRms * 1000:F3} ms");
        Assert.Equal(0, clock.Restarts);
    }

    [Fact]
    public void ASeek_RestartsAtTheNewPosition()
    {
        var clock = new VsyncMediaClock(Frequency);
        var period = Ticks(1 / 60.0);
        for (var vsync = 0; vsync < 60; vsync++)
            _ = clock.Update(Start + (vsync * period), vsync / 60.0);

        Assert.Equal(42.0, clock.Update(Start + (60 * period), 42.0), 9);
        Assert.Equal(1, clock.Restarts);
    }

    [Fact]
    public void APause_RestartsTheFilter()
    {
        var clock = new VsyncMediaClock(Frequency);
        var period = Ticks(1 / 60.0);
        for (var vsync = 0; vsync < 60; vsync++)
            _ = clock.Update(Start + (vsync * period), vsync / 60.0);

        // Paused for two seconds at 1.0 s: the next refresh is far later but the position is not.
        Assert.Equal(1.0, clock.Update(Start + (60 * period) + Ticks(2), 1.0), 9);
        Assert.Equal(1, clock.Restarts);
    }

    [Fact]
    public void ATempoChange_IsFollowedWithinASecond()
    {
        var clock = new VsyncMediaClock(Frequency);
        var random = new Random(5);
        var period = Ticks(1 / 60.0);
        var media = 0.0;
        double filtered = 0;
        for (var vsync = 0; vsync < 240; vsync++)
        {
            var rate = vsync < 120 ? 1.0 : 0.5; // tempo halves at 2 s
            media += rate / 60.0;
            filtered = clock.Update(Start + (vsync * period), media + (((random.NextDouble() * 2) - 1) * 0.001));
        }

        Assert.Equal(0.5, clock.Rate, 2);
        Assert.True(Math.Abs(filtered - media) < 0.002, $"error {(filtered - media) * 1000:F2} ms");
    }

    [Fact]
    public void TheSameRefreshTwice_GivesTheSameAnswer()
    {
        var clock = new VsyncMediaClock(Frequency);
        var first = clock.Update(Start, 5.0);
        Assert.Equal(first, clock.Update(Start, 5.01));
    }

    [Theory]
    [InlineData(0.0015)]
    [InlineData(-0.0015)]
    [InlineData(0.004)]
    public void TwentyThreeNineSevenSixOn5994_StaysExactThreeTwo_ThroughJitter(double phaseSeconds)
    {
        // Exact 3:2 (the ratio is 2.5). Half the frame boundaries sit phaseSeconds from a refresh;
        // with +-2 ms clock jitter the raw clock moves those frames between refreshes.
        (List<int> filteredHolds, List<int> rawHolds) = Simulate(FrameRate.Fps23_976, FrameRate.Hz59_94, phaseSeconds, seconds: 60);

        Assert.All(filteredHolds, hold => Assert.True(hold is 2 or 3, $"hold {hold}"));
        Assert.Equal(0, CountBrokenAlternations(filteredHolds));
        if (Math.Abs(phaseSeconds) < 0.002)
            Assert.True(CountBrokenAlternations(rawHolds) > 0, "the raw clock should show the flip-flops the filter removes");
    }

    [Fact]
    public void TwentyFourOnSixty_IsJudderFreeThreeTwo()
    {
        (List<int> holds, _) = Simulate(FrameRate.FromInteger(24), FrameRate.FromInteger(60), 0.002, seconds: 30);

        Assert.Equal(0, CountBrokenAlternations(holds));
    }

    [Fact]
    public void TwentyThreeNineSevenSixOnSixty_BreaksOnlyAsOftenAsTheDriftRequires()
    {
        // 0.1 % drift: the model breaks the 3:2 pattern about 4 times a minute. Jitter must not add
        // more (each crossing happens once, cleanly).
        (List<int> holds, List<int> rawHolds) = Simulate(FrameRate.Fps23_976, FrameRate.FromInteger(60), 0.003, seconds: 120);
        CadenceReport exact = DisplayCadence.Analyze(FrameRate.Fps23_976, FrameRate.FromInteger(60), 120 * 60);

        var modelBreaks = CountBrokenAlternations([.. exact.HoldPattern]);
        var filteredBreaks = CountBrokenAlternations(holds);
        Assert.InRange(filteredBreaks, Math.Max(0, modelBreaks - 2), modelBreaks + 2);
        Assert.True(CountBrokenAlternations(rawHolds) > filteredBreaks, $"raw {CountBrokenAlternations(rawHolds)}, filtered {filteredBreaks}");
    }

    [Fact]
    public void TheNextSlot_LeavesTheBudgetAndNeverRepeatsARefresh()
    {
        var period = Ticks(1 / 60.0);
        var timing = new PresentationVsyncTiming(1, Start, period);
        var budget = Ticks(0.004);

        Assert.True(DisplayLockedTiming.TryGetNextSlot(timing, Start + Ticks(0.001), budget, lastVsync: 0, out DisplayLockedSlot slot));
        Assert.Equal(Start + period, slot.VsyncTimestamp);
        Assert.Equal(slot.VsyncTimestamp - budget, slot.WakeTimestamp);

        // Too close to that refresh to make it: the one after.
        Assert.True(DisplayLockedTiming.TryGetNextSlot(timing, Start + period - Ticks(0.001), budget, lastVsync: 0, out slot));
        Assert.Equal(Start + (2 * period), slot.VsyncTimestamp);

        // Already handled: the next one.
        Assert.True(DisplayLockedTiming.TryGetNextSlot(timing, Start + Ticks(0.001), budget, lastVsync: Start + period, out slot));
        Assert.Equal(Start + (2 * period), slot.VsyncTimestamp);
    }

    [Fact]
    public void StaleOrMissingTiming_GivesNoSlot()
    {
        var period = Ticks(1 / 60.0);
        Assert.False(DisplayLockedTiming.TryGetNextSlot(default, Start, Ticks(0.004), 0, out _));
        Assert.False(DisplayLockedTiming.TryGetNextSlot(new PresentationVsyncTiming(1, Start, period), Start + Ticks(2), Ticks(0.004), 0, out _));
        // Half a second old (the source measuring the rhythm again) still predicts.
        Assert.True(DisplayLockedTiming.TryGetNextSlot(new PresentationVsyncTiming(1, Start, period), Start + Ticks(0.5), Ticks(0.004), 0, out _));
    }

    [Fact]
    public void TheBudget_FollowsTheMeasuredWork_WithinItsLimits()
    {
        var period = Ticks(1 / 60.0);
        Assert.Equal(Ticks(0.004), DisplayLockedTiming.GetBudgetTicks(double.NaN, period), Ticks(0.0001));
        Assert.Equal(Ticks(0.002), DisplayLockedTiming.GetBudgetTicks(0.0001, period), Ticks(0.0001));
        Assert.Equal(Ticks(0.0055), DisplayLockedTiming.GetBudgetTicks(0.003, period), Ticks(0.0001));
        Assert.Equal(Ticks(0.012), DisplayLockedTiming.GetBudgetTicks(0.05, Ticks(1 / 30.0)), Ticks(0.0001));
        // Never more than 60 % of a refresh: 10 ms at 60 Hz.
        Assert.Equal((long)(period * 0.6), DisplayLockedTiming.GetBudgetTicks(0.05, period));
        Assert.Equal((long)(Ticks(1 / 240.0) * 0.6), DisplayLockedTiming.GetBudgetTicks(0.05, Ticks(1 / 240.0)));
    }

    [Fact]
    public void NextVsync_RoundsUpOntoTheGrid()
    {
        var timing = new PresentationVsyncTiming(1, 1_000, 100);
        Assert.Equal(1_000, timing.NextVsyncAtOrAfter(900));
        Assert.Equal(1_000, timing.NextVsyncAtOrAfter(1_000));
        Assert.Equal(1_100, timing.NextVsyncAtOrAfter(1_001));
        Assert.Equal(1_500, timing.NextVsyncAtOrAfter(1_500));
    }

    [Theory]
    [InlineData(24.0, 60.0, 2)]
    [InlineData(23.976, 59.94, 2)]
    [InlineData(30.0, 60.0, 1)]
    [InlineData(24.0, 120.0, 1)]
    [InlineData(60.0, 60.0, 1)]
    [InlineData(25.0, 60.0, 0)]
    public void ThePhaseLock_FindsTheCadenceFamilies(double fps, double hertz, int families)
    {
        var phaseLock = new CadencePhaseLock();
        phaseLock.Configure(1 / hertz, fps);
        Assert.Equal(families, phaseLock.Families);
    }

    [Fact]
    public void ThePhaseLock_StepsAwayFromARefresh_AndWrapsWithinHalfASpacing()
    {
        var period = 1 / 60.0;
        var phaseLock = new CadencePhaseLock();
        phaseLock.Configure(period, 24);
        var spacing = period / 2;

        phaseLock.OnFrameStarted(selectionTime: 1.0005, framePresentationTime: 1.0); // just due
        Assert.Equal(spacing / 4, phaseLock.Offset, 9);

        phaseLock.OnFrameStarted(selectionTime: 1.0 + (spacing * 0.5), framePresentationTime: 1.0); // comfortable
        Assert.Equal(spacing / 4, phaseLock.Offset, 9);

        phaseLock.OnFrameStarted(1.0001, 1.0);
        Assert.Equal(spacing / 2, phaseLock.Offset, 9);
        phaseLock.OnFrameStarted(1.0001, 1.0);
        Assert.Equal(-spacing / 4, phaseLock.Offset, 9); // wrapped by one spacing
        Assert.Equal(1, phaseLock.Wraps);
    }

    [Fact]
    public void ThePhaseLock_KeepsItsOffset_ThroughAMeasuredPeriodThatJitters()
    {
        // Review of slice 4: the measured period changes by a few ticks every refresh; that must
        // not clear the offset, or the hysteresis never acts on real displays.
        var phaseLock = new CadencePhaseLock();
        phaseLock.Configure(1 / 60.0, 24);
        phaseLock.OnFrameStarted(1.0005, 1.0);
        var offset = phaseLock.Offset;
        Assert.True(offset > 0);

        phaseLock.Configure((1 / 60.0) * 1.000002, 24);
        phaseLock.Configure((1 / 60.0) * 0.999997, 23.9999);
        Assert.Equal(offset, phaseLock.Offset);

        phaseLock.Configure(1 / 50.0, 24); // another refresh rate
        Assert.Equal(0, phaseLock.Offset);
    }

    [Fact]
    public void TheSameRefreshPredictedTwice_LeavesTheRateAlone()
    {
        // Review of slice 4: a grid prediction a fraction of a millisecond later is the same refresh;
        // dividing clock jitter by that gap pushed the rate to its clamp.
        var clock = new VsyncMediaClock(Frequency);
        var period = Ticks(1 / 60.0);
        _ = clock.Update(Start, 10.0);
        _ = clock.Update(Start + period, 10.0 + (1 / 60.0));
        var position = clock.Update(Start + period + Ticks(0.0001), 10.0 + (1 / 60.0) + 0.002);

        Assert.Equal(10.0 + (1 / 60.0), position, 6);
        Assert.Equal(1.0, clock.Rate, 3);
    }

    [Fact]
    public void RestartsInARow_StartTheRateOver()
    {
        var clock = new VsyncMediaClock(Frequency);
        var period = Ticks(1 / 60.0);
        // Two updates 3 ms apart with clock jitter: the warm-up takes a wild rate (clamped at 8).
        _ = clock.Update(Start, 0);
        _ = clock.Update(Start + Ticks(0.003), 0.033);
        Assert.True(clock.Rate > 5);

        // Seek: the first restart keeps that rate, the second (it predicted far off) starts over at 1.
        var t = Start + Ticks(0.003);
        _ = clock.Update(t + period, 30.0);
        _ = clock.Update(t + (2 * period), 30.0 + (1 / 60.0));
        Assert.Equal(2, clock.Restarts);
        Assert.Equal(1.0, clock.Rate, 3);

        for (var vsync = 3; vsync < 60; vsync++)
            Assert.Equal(30.0 + ((vsync - 1) / 60.0), clock.Update(t + (vsync * period), 30.0 + ((vsync - 1) / 60.0)), 4);

        Assert.Equal(2, clock.Restarts);
    }

    [Fact]
    public void AGridThatMovesLater_DoesNotGiveTheSameRefreshAgain()
    {
        var period = Ticks(1 / 60.0);
        var handled = Start + period;
        // The next blank moved the grid's prediction of that refresh 0.4 ms later.
        var moved = new PresentationVsyncTiming(1, handled + Ticks(0.0004), period);

        Assert.True(DisplayLockedTiming.TryGetNextSlot(moved, handled - Ticks(0.0035), Ticks(0.004), handled, out DisplayLockedSlot slot));
        Assert.Equal(handled + Ticks(0.0004) + period, slot.VsyncTimestamp);
    }

    [Fact]
    public void TwentyFiveOnSixty_KeepsItsModelCadence()
    {
        // No phase families: the lock stays out of the way and the 2/3 mix is the model's.
        (List<int> holds, _) = Simulate(FrameRate.FromInteger(25), FrameRate.FromInteger(60), 0.003, seconds: 30);

        Assert.All(holds, hold => Assert.True(hold is 2 or 3, $"hold {hold}"));
        Assert.InRange(holds.Average(), 2.39, 2.41);
    }

    [Fact]
    public void TwoDisplays_EachKeepTheirOwnCadence_FromSharedFrames()
    {
        // Stage 017A slice 5: 25 fps on a 60 Hz primary and a 50 Hz projector. The loop serves the
        // display whose refresh comes next; the shared frame moves forward once, and a display
        // whose refresh reaches it later shows it then. Each display keeps its own model cadence.
        (List<int> primary, List<int> projector) = SimulateTwoDisplays(25, 60, 50, seconds: 30);

        Assert.All(projector, hold => Assert.Equal(2, hold)); // 50 / 25: every frame twice
        Assert.All(primary, hold => Assert.True(hold is 2 or 3, $"hold {hold}"));
        Assert.InRange(primary.Average(), 2.39, 2.41); // 60 / 25
    }

    /// <summary>
    /// Mirrors the pipeline's multi-display loop: per display a filtered clock, phase lock and
    /// last frame shown; one shared current frame that only moves forward.
    /// </summary>
    private static (List<int> Primary, List<int> Secondary) SimulateTwoDisplays(double fps, double primaryHz, double secondaryHz, int seconds)
    {
        var random = new Random(17);
        var frameSeconds = 1 / fps;
        var displays = new[] { new SimulatedDisplay(primaryHz, phase: 0.0013), new SimulatedDisplay(secondaryHz, phase: 0.0071) };
        long current = -1;
        var budget = Ticks(0.004);
        var end = Start + Ticks(seconds);
        while (true)
        {
            // The display whose wake-up comes first.
            SimulatedDisplay? next = null;
            DisplayLockedSlot slot = default;
            foreach (SimulatedDisplay display in displays)
            {
                Assert.True(DisplayLockedTiming.TryGetNextSlot(display.TimingAt(display.LastVsync + 1), display.LastVsync + 1, budget, display.LastVsync, out DisplayLockedSlot candidate));
                if (next is null || candidate.WakeTimestamp < slot.WakeTimestamp)
                {
                    next = display;
                    slot = candidate;
                }
            }

            if (slot.VsyncTimestamp > end)
                break;

            var media = (slot.VsyncTimestamp - Start) / (double)Frequency;
            var observed = media + (((random.NextDouble() * 2) - 1) * 0.002);
            var filtered = next!.Clock.Update(slot.VsyncTimestamp, observed);
            next.PhaseLock.Configure(next.PeriodSeconds, fps);
            var selection = DisplayLockedTiming.GetSelectionTime(filtered, next.PeriodSeconds) + next.PhaseLock.Offset;
            var due = FrameAt(selection, frameSeconds);
            if (due > current)
                current = due; // the shared frame moves forward

            var shows = current * frameSeconds <= selection + 0.0005 ? current : next.Shown;
            if (shows != next.Shown && next.Shown >= 0)
                next.PhaseLock.OnFrameStarted(selection, shows * frameSeconds);

            next.Record(slot, shows);
            next.LastVsync = slot.VsyncTimestamp;
        }

        return (displays[0].Holds(skip: 30), displays[1].Holds(skip: 30));
    }

    private sealed class SimulatedDisplay(double hertz, double phase)
    {
        private readonly List<int> _holds = [];
        private long _frameStart;

        public double PeriodSeconds { get; } = 1 / hertz;

        public VsyncMediaClock Clock { get; } = new(Frequency);

        public CadencePhaseLock PhaseLock { get; } = new();

        public long LastVsync { get; set; }

        public long Shown { get; private set; } = -1;

        public PresentationVsyncTiming TimingAt(long now) =>
            new(1, Start + Ticks(phase) + (long)(Math.Floor((now - Start) / (PeriodSeconds * Frequency)) * PeriodSeconds * Frequency), Ticks(PeriodSeconds));

        public void Record(DisplayLockedSlot slot, long shows)
        {
            if (shows != Shown)
            {
                if (Shown >= 0)
                    _holds.Add((int)Math.Round((slot.VsyncTimestamp - _frameStart) / (PeriodSeconds * Frequency)));

                _frameStart = slot.VsyncTimestamp;
                Shown = shows;
            }
        }

        public List<int> Holds(int skip) => _holds.Count > skip + 1 ? _holds[skip..^1] : _holds;
    }

    private static readonly long Start = Frequency * 100;

    private static long Ticks(double seconds) => (long)Math.Round(seconds * Frequency);

    /// <summary>
    /// Runs the display-locked selection on a refresh grid with a jittered audio clock: the
    /// frame each refresh shows, through the filter and phase lock, and straight from the raw clock. Returns the
    /// refreshes each frame stayed (the first and last frame left out).
    /// </summary>
    private static (List<int> Filtered, List<int> Raw) Simulate(FrameRate source, FrameRate display, double phaseSeconds, int seconds)
    {
        var random = new Random(11);
        var clock = new VsyncMediaClock(Frequency);
        var phaseLock = new CadencePhaseLock();
        var periodSeconds = display.Denominator / (double)display.Numerator;
        var periodTicks = periodSeconds * Frequency;
        var frameSeconds = source.Denominator / (double)source.Numerator;
        var refreshes = (long)(seconds / periodSeconds);
        phaseLock.Configure(periodSeconds, 1 / frameSeconds);
        var filteredFrames = new List<long>();
        var rawFrames = new List<long>();
        for (long vsync = 0; vsync < refreshes; vsync++)
        {
            // The media time at this refresh, offset so frame boundaries sit phaseSeconds from it.
            var media = (vsync * periodSeconds) - (periodSeconds / 2) + phaseSeconds;
            var observed = media + (((random.NextDouble() * 2) - 1) * 0.002);
            var filtered = clock.Update(Start + (long)Math.Round(vsync * periodTicks), observed);
            var selection = DisplayLockedTiming.GetSelectionTime(filtered, periodSeconds) + phaseLock.Offset;
            var frame = FrameAt(selection, frameSeconds);
            if (filteredFrames.Count > 0 && frame != filteredFrames[^1])
                phaseLock.OnFrameStarted(selection, frame * frameSeconds);

            filteredFrames.Add(frame);
            rawFrames.Add(FrameAt(DisplayLockedTiming.GetSelectionTime(observed, periodSeconds), frameSeconds));
        }

        return (Holds(filteredFrames, skip: 120), Holds(rawFrames, skip: 120));
    }

    private static long FrameAt(double selectionTime, double frameSeconds) => (long)Math.Floor((selectionTime / frameSeconds) + 1e-9);

    private static List<int> Holds(List<long> frames, int skip)
    {
        var holds = new List<int>();
        var run = 0;
        for (var index = skip; index < frames.Count; index++)
        {
            if (index > skip && frames[index] != frames[index - 1])
            {
                holds.Add(run);
                run = 0;
            }

            run++;
        }

        return holds.Count > 2 ? holds[1..^0] : holds;
    }

    /// <summary>Places where an alternating hold pattern (3,2,3,2) repeats or changes length.</summary>
    private static int CountBrokenAlternations(IReadOnlyList<int> holds)
    {
        var broken = 0;
        for (var index = 1; index < holds.Count; index++)
            if (holds[index] == holds[index - 1] || holds[index] is < 2 or > 3)
                broken++;

        return broken;
    }
}
