using System.Diagnostics;
using System.Globalization;
using FatzMedia.Video.Core.Graphics.Cadence;
using FatzMedia.Video.Core.Graphics.Composition;

namespace FatzMedia.Video.Pipeline;

/// <summary>
/// Display-locked video timing (Stage 017A slices 4 and 5): with <see cref="VideoFrameTiming.DisplayLocked"/>
/// the presentation loop wakes a few milliseconds before each refresh of a display showing the
/// video and presents the frame for the moment that refresh reaches the screen, from the display's
/// vertical-blank timing and a jitter-filtered media clock (<see cref="VsyncMediaClock"/>).
/// </summary>
/// <remarks>
/// With surfaces on several monitors each display runs its own cadence: the loop serves whichever
/// display's refresh comes next and presents to that display's surfaces only. The decoded frames
/// are shared, and their order follows the one media clock: a display whose refresh comes later
/// shows the frame another display already moved on to once its own refresh reaches that frame's
/// time. Without current timing for the primary display, the source-cadence path is kept,
/// unchanged.
/// </remarks>
sealed partial class VideoPipeline : IVideoFrameTimingControl, IPresentationTimingSink
{
    const string FrameTimingDiagnosticCategory = "VideoFrameTiming";
    static readonly long DisplayLockedReportIntervalTicks = Stopwatch.Frequency * 60;
    static readonly long DisplayLockedStateSettleTicks = Stopwatch.Frequency / 2;
    // Timing gaps this short (a vblank grid restarting, a monitor change) keep each display's
    // clock and statistics, so they cause neither a cadence restart nor a burst of reports.
    static readonly long DisplayLockedGapToleranceTicks = Stopwatch.Frequency;

    // The targets of the frame being raised on this thread (null: every target).
    [ThreadStatic]
static MediaCompositionTargetFilter? t_frameReadyTargets;

    readonly List<PresentationTimingGroup> _timingGroups = [];
    readonly Dictionary<long, DisplayLockedTarget> _displayLockedTargets = [];
    // The displays one pass presents for, earliest refresh first (presentation thread only).
    readonly List<BatchMember> _displayLockedBatch = [];
    int _displayLockedDisplayCount;
    PresentationTimingSource? _presentationTimingSource;
    volatile VideoFrameTiming _frameTiming = VideoFrameTiming.SourceCadence;
    int _displayLockedResetRequested;
    string? _displayLockedReportedState;
    string? _displayLockedPendingState;
    long _displayLockedPendingSince;
    string? _displayLockedOnState;
    long[] _displayLockedOnStateKey = [];
    long _displayLockedUnavailableSince;

    /// <inheritdoc cref="IVideoFrameTimingControl.FrameTiming"/>
    public VideoFrameTiming FrameTiming
    {
        get => _frameTiming;
        set => _frameTiming = value;
    }

    void IPresentationTimingSink.SetPresentationTimingSource(PresentationTimingSource? source) =>
        Volatile.Write(ref _presentationTimingSource, source);

    /// <summary>Restarts the filtered clocks on the next refresh (open, close, seek).</summary>
    void RequestDisplayLockedReset() => Volatile.Write(ref _displayLockedResetRequested, 1);

    /// <summary>
    /// The next refresh to present for, across the displays showing the video, when display-locked
    /// timing applies: the mode is on, the video's frame rate is known, and the renderer reports
    /// current vertical-blank timing for the primary surface's display. Presentation thread only.
    /// </summary>
    bool TryGetDisplayLockedSlot(out DisplayLockedSlot slot, out DisplayLockedTarget? target)
    {
        slot = default;
        target = null;
        if (_frameTiming != VideoFrameTiming.DisplayLocked)
            return NoDisplayLockedSlot("off");

        if (!(FramesPerSecond > 0))
            return NoDisplayLockedSlot("unavailable: the video's frame rate is unknown");

        if (!QueryTimingGroups())
        {
            return NoDisplayLockedSlot(
                "unavailable: the renderer reports no vertical-blank timing for the video's display "
                + "(software output or a WPF-composited surface)");
        }

        var now = Stopwatch.GetTimestamp();
        var primaryCurrent = false;
        _displayLockedBatch.Clear();
        _displayLockedDisplayCount = 0;
        foreach (var group in _timingGroups)
        {
            if (!group.Timing.IsValid)
                continue;

            _displayLockedDisplayCount++;
            var display = GetDisplayLockedTarget(group);
            var budget = DisplayLockedTiming.GetBudgetTicks(display.WorkSeconds, group.Timing.PeriodTicks);
            if (!DisplayLockedTiming.TryGetNextSlot(group.Timing, now, budget, display.LastVsync, out var candidate))
                continue;

            primaryCurrent |= group.IncludesPrimary;
            _displayLockedBatch.Add(new BatchMember(candidate, display));
            if (target is null || candidate.WakeTimestamp < slot.WakeTimestamp)
            {
                slot = candidate;
                target = display;
            }
        }

        // One pass serves every display whose own wake-up falls before this pass would be done:
        // monitors refreshing at almost the same moment (two 60 Hz monitors drift in and out of
        // step) would otherwise take turns, each pass making the other miss its refresh.
        if (target is not null)
        {
            var passTicks = (long)((double.IsNaN(target.WorkSeconds) ? DisplayLockedTiming.InitialBudget.TotalSeconds : target.WorkSeconds) * Stopwatch.Frequency)
                + (Stopwatch.Frequency / 1000);
            var batchUntil = slot.WakeTimestamp + passTicks;
            RemoveBatchMembersWakingAfter(batchUntil);
            _displayLockedBatch.Sort(static (a, b) => a.Slot.VsyncTimestamp.CompareTo(b.Slot.VsyncTimestamp));
        }

        ForgetDisplaysNotShowingTheVideo(now);
        if (target is null || !primaryCurrent)
        {
            target = null;
            return NoDisplayLockedSlot("unavailable: the display's vertical-blank timing is not current");
        }

        _displayLockedUnavailableSince = 0;
        NoteDisplayLockedState(GetDisplayLockedOnState());
        return true;
    }

    // RemoveAll without its capturing lambda: this runs once per refresh (ENG-039 P5-6).
    void RemoveBatchMembersWakingAfter(long batchUntil)
    {
        var kept = 0;
        for (var index = 0; index < _displayLockedBatch.Count; index++)
        {
            var member = _displayLockedBatch[index];
            if (member.Slot.WakeTimestamp <= batchUntil)
                _displayLockedBatch[kept++] = member;
        }

        _displayLockedBatch.RemoveRange(kept, _displayLockedBatch.Count - kept);
    }

    bool QueryTimingGroups()
    {
        var source = Volatile.Read(ref _presentationTimingSource);
        _timingGroups.Clear();
        if (source is null)
            return false;

        try
        {
            source(_timingGroups);
        }
        catch (Exception ex)
        {
            // A renderer bug must not stop playback: the source-cadence path takes over.
            Debug.WriteLine($"[VideoPipeline] Presentation timing query failed: {ex.Message}");
            _timingGroups.Clear();
            return false;
        }

        foreach (var group in _timingGroups)
            if (group.IncludesPrimary && group.Timing.IsValid)
                return true;

        return false;
    }

    DisplayLockedTarget GetDisplayLockedTarget(PresentationTimingGroup group)
    {
        if (!_displayLockedTargets.TryGetValue(group.Timing.DisplayKey, out var display))
        {
            display = new DisplayLockedTarget();
            _displayLockedTargets.Add(group.Timing.DisplayKey, display);
        }

        display.Group = group;
        display.LastSeen = Stopwatch.GetTimestamp();
        return display;
    }

    void ForgetDisplaysNotShowingTheVideo(long now)
    {
        List<long>? gone = null;
        foreach ((var key, var display) in _displayLockedTargets)
        {
            if (now - display.LastSeen > DisplayLockedGapToleranceTicks)
            {
                ReportDisplayLockedStatisticsIfDue(display, force: true);
                (gone ??= []).Add(key);
            }
        }

        if (gone is not null)
            foreach (var key in gone)
                _ = _displayLockedTargets.Remove(key);
    }

    // Built only when the displays or their periods change: this runs once per refresh, so the
    // check compares the periods in place instead of building a key (ENG-039 P5-6).
    string GetDisplayLockedOnState()
    {
        if (_displayLockedOnState is null || !DisplayLockedOnStateKeyMatches())
        {
            _displayLockedOnStateKey = [.. _displayLockedTargets.Values.Select(static display => display.Group.Timing.PeriodTicks / 1000)];
            var rates = _displayLockedTargets.Values
                .OrderByDescending(static display => display.IncludesPrimary)
                .Select(static display => (Stopwatch.Frequency / (double)display.Group.Timing.PeriodTicks).ToString("0.###", CultureInfo.InvariantCulture))
                .ToArray();
            _displayLockedOnState = rates.Length == 1
                ? $"on: frames follow the {rates[0]} Hz refreshes of the video's display"
                : $"on: {rates.Length} displays ({string.Join(" Hz, ", rates)} Hz), each following its own refreshes";
        }

        return _displayLockedOnState;
    }

    bool DisplayLockedOnStateKeyMatches()
    {
        if (_displayLockedOnStateKey.Length != _displayLockedTargets.Count)
            return false;

        var index = 0;
        foreach (var display in _displayLockedTargets.Values)
            if (_displayLockedOnStateKey[index++] != display.Group.Timing.PeriodTicks / 1000)
                return false;

        return true;
    }

    /// <summary>
    /// Presents for the displays of this pass (<see cref="_displayLockedBatch"/>, earliest refresh
    /// first; <paramref name="slot"/> and <paramref name="display"/> are its first) and returns the
    /// raw clock position for the loop's end and loop-wrap checks. Each display works out the
    /// media time of its own refresh; the frame for the earliest refresh is presented once to all
    /// of their surfaces. Runs under the presentation lock.
    /// </summary>
    double PresentFrameForDisplayLockedSlot(DisplayLockedSlot slot, DisplayLockedTarget display)
    {
        var workStart = Stopwatch.GetTimestamp();
        if (Interlocked.Exchange(ref _displayLockedResetRequested, 0) != 0)
        {
            foreach (var each in _displayLockedTargets.Values)
                each.Clock.Reset();
        }

        if (_displayLockedBatch.Count == 0)
            _displayLockedBatch.Add(new BatchMember(slot, display));

        var clockPosition = GetPresentationPosition();
        var now = Stopwatch.GetTimestamp();
        for (var index = 0; index < _displayLockedBatch.Count; index++)
        {
            var member = _displayLockedBatch[index];
            _displayLockedBatch[index] = member with { Selection = GetDisplayLockedSelection(member, clockPosition, now) };
        }

        var selection = _displayLockedBatch[0].Selection;
        var batchCoversEveryDisplay = _displayLockedBatch.Count >= _displayLockedDisplayCount;
        double? shownToAll = null;
        t_frameReadyTargets = MergeTargets(_displayLockedBatch, batchCoversEveryDisplay);
        try
        {
            var before = _currentFrame?.PresentationTime;
            PresentFrameForPosition(selection, selection, forceReissue: false, displayLocked: true);
            if (_currentFrame is { } current)
            {
                if (current.PresentationTime != before)
                {
                    shownToAll = current.PresentationTime;
                }
                else if (_displayLockedDisplayCount > 1)
                {
                    // Displays of this pass that have not shown the frame another display moved on
                    // to get it now, at their own refresh.
                    ReissueToLaggingDisplays(current.PresentationTime);
                }
            }
        }
        finally
        {
            t_frameReadyTargets = null;
        }

        var workEnd = Stopwatch.GetTimestamp();
        var workSeconds = Stopwatch.GetElapsedTime(workStart, workEnd).TotalSeconds;
        foreach (var member in _displayLockedBatch)
            FinishDisplayLockedRefresh(member, shownToAll ?? member.ReissuedTime, workEnd, workSeconds);

        _displayLockedBatch.Clear();
        return clockPosition;
    }

    double GetDisplayLockedSelection(BatchMember member, double clockPosition, long now)
    {
        var display = member.Display;
        var clock = display.Clock;
        var secondsToVsync = (member.Slot.VsyncTimestamp - now) / (double)Stopwatch.Frequency;
        var mediaAtVsync = clock.Update(member.Slot.VsyncTimestamp, clockPosition + (secondsToVsync * clock.Rate));
        var periodSeconds = member.Slot.PeriodTicks / (double)Stopwatch.Frequency;

        // A restarted clock (seek, loop, jump) starts the cadence afresh: the phase lock forgets
        // its offset. It also clears itself when the refresh period or frame rate changes.
        var phaseLock = display.PhaseLock;
        phaseLock.Configure(periodSeconds, FramesPerSecond);
        if (clock.Restarts != display.ClockRestarts)
        {
            display.ClockRestarts = clock.Restarts;
            phaseLock.Reset();
        }

        return ApplyAudioVideoOffset(DisplayLockedTiming.GetSelectionTime(mediaAtVsync, periodSeconds) + phaseLock.Offset);
    }

    void ReissueToLaggingDisplays(double currentTime)
    {
        var lagging = 0;
        for (var index = 0; index < _displayLockedBatch.Count; index++)
        {
            var member = _displayLockedBatch[index];
            if (currentTime != member.Display.LastShown && currentTime <= member.Selection + DisplayLockedPtsToleranceSeconds)
            {
                _displayLockedBatch[index] = member with { ReissuedTime = currentTime };
                lagging++;
            }
        }

        if (lagging == 0)
            return;

        t_frameReadyTargets = lagging == _displayLockedBatch.Count
            ? MergeTargets(_displayLockedBatch, _displayLockedBatch.Count >= _displayLockedDisplayCount)
            : MergeTargets([.. _displayLockedBatch.Where(static member => member.ReissuedTime is not null)], coversEveryDisplay: false);
        ReissueCurrentFrame();
    }

    // The targets of several displays as one filter: none when they are every display.
    static MediaCompositionTargetFilter? MergeTargets(List<BatchMember> members, bool coversEveryDisplay)
    {
        if (coversEveryDisplay || members.Count == 0)
            return null;

        if (members.Count == 1)
            return members[0].Display.Group.Targets;

        var includesPrimary = false;
        var secondaries = new List<string>();
        foreach (var member in members)
        {
            if (member.Display.Group.Targets is not { } targets)
                return null;

            includesPrimary |= targets.IncludesPrimary;
            secondaries.AddRange(targets.SecondarySurfaceIds);
        }

        return new MediaCompositionTargetFilter(includesPrimary, secondaries);
    }

    void FinishDisplayLockedRefresh(BatchMember member, double? shown, long workEnd, double workSeconds)
    {
        var display = member.Display;
        var newFrame = shown is not null && shown != display.LastShown;
        if (shown is { } shownTime)
        {
            display.LastShown = shownTime;
            if (newFrame)
                display.PhaseLock.OnFrameStarted(member.Selection, shownTime);
        }

        display.LastVsync = member.Slot.VsyncTimestamp;
        display.WorkSeconds = double.IsNaN(display.WorkSeconds)
            ? workSeconds
            : (display.WorkSeconds * 0.9) + (workSeconds * 0.1);

        // The frame on screen is more than a frame and a half behind what this refresh asked for:
        // the next frame was not decoded in time (or was dropped as too late).
        var starved = FramesPerSecond > 0
            && _currentFrame is { } onScreen
            && member.Selection - onScreen.PresentationTime > 1.5 / FramesPerSecond;
        display.Statistics.Record(
            member.Slot,
            newFrame,
            late: workEnd > member.Slot.VsyncTimestamp,
            workSeconds,
            starved,
            display.Clock.Restarts,
            display.PhaseLock.Wraps,
            display.Group.Timing.Resyncs);
        ReportDisplayLockedStatisticsIfDue(display, force: false);
    }

    /// <summary>One display served by a pass: its refresh, its selection, and a re-raised frame.</summary>
    readonly record struct BatchMember(DisplayLockedSlot Slot, DisplayLockedTarget Display, double Selection = 0, double? ReissuedTime = null);

    /// <summary>The loop is not presenting (paused, stopped): report what the last run did.</summary>
    void OnDisplayLockedIdle()
    {
        foreach (var display in _displayLockedTargets.Values)
        {
            ReportDisplayLockedStatisticsIfDue(display, force: true);
            display.LastShown = null;
        }
    }

    bool NoDisplayLockedSlot(string state)
    {
        // A pass on the source-cadence path while display-locked timing is on: counted on the
        // primary display's report (switching paths shows as uneven motion).
        if (_frameTiming == VideoFrameTiming.DisplayLocked)
        {
            foreach (var display in _displayLockedTargets.Values)
                if (display.IncludesPrimary)
                    display.Statistics.RecordFallbackPass();
        }

        var now = Stopwatch.GetTimestamp();
        if (_displayLockedUnavailableSince == 0)
            _displayLockedUnavailableSince = now;

        if (_displayLockedTargets.Count > 0
            && (_frameTiming != VideoFrameTiming.DisplayLocked || now - _displayLockedUnavailableSince > DisplayLockedGapToleranceTicks))
        {
            foreach (var display in _displayLockedTargets.Values)
                ReportDisplayLockedStatisticsIfDue(display, force: true);

            _displayLockedTargets.Clear();
        }

        NoteDisplayLockedState(state);
        return false;
    }

    /// <summary>
    /// Reports a change of display-locked state once it has held for half a second, so the first
    /// refresh after starting playback (timing not measured yet) does not log a false alarm.
    /// "Off" is only reported after display-locked timing was on.
    /// </summary>
    void NoteDisplayLockedState(string state)
    {
        if (state == _displayLockedReportedState)
        {
            _displayLockedPendingState = null;
            return;
        }

        var now = Stopwatch.GetTimestamp();
        if (state != _displayLockedPendingState)
        {
            _displayLockedPendingState = state;
            _displayLockedPendingSince = now;
            return;
        }

        if (now - _displayLockedPendingSince < DisplayLockedStateSettleTicks)
            return;

        var previous = _displayLockedReportedState;
        _displayLockedReportedState = state;
        _displayLockedPendingState = null;
        if (state == "off" && previous is null)
            return;

        DiagnosticRouter.Raise(MediaDiagnosticLevel.Info, FrameTimingDiagnosticCategory, $"Display-locked video timing {state}.", _currentVideoPath);
    }

    void ReportDisplayLockedStatisticsIfDue(DisplayLockedTarget display, bool force)
    {
        var statistics = display.Statistics;
        if (!statistics.HasData
            || (!force && Stopwatch.GetTimestamp() - statistics.StartTimestamp < DisplayLockedReportIntervalTicks))
        {
            return;
        }

        var label = _displayLockedTargets.Count > 1
            ? display.IncludesPrimary ? " (primary display)" : " (another display)"
            : string.Empty;
        DiagnosticRouter.Raise(MediaDiagnosticLevel.Info, FrameTimingDiagnosticCategory, statistics.Describe(label), _currentVideoPath);
        statistics.Clear();
    }

    /// <summary>The display-locked state of one display showing the video.</summary>
    sealed class DisplayLockedTarget
    {
        public VsyncMediaClock Clock { get; } = new(Stopwatch.Frequency);

        public CadencePhaseLock PhaseLock { get; } = new();

        public DisplayLockedStatistics Statistics { get; } = new();

        public PresentationTimingGroup Group { get; set; }

        public bool IncludesPrimary => Group.IncludesPrimary;

        public long LastSeen { get; set; }

        public long LastVsync { get; set; }

        public long ClockRestarts { get; set; }

        public double WorkSeconds { get; set; } = double.NaN;

        /// <summary>The timestamp of the frame this display shows (null after a pause).</summary>
        public double? LastShown { get; set; }
    }

    /// <summary>
    /// What display-locked timing did over a run on one display: refreshes handled, frames shown
    /// and how many refreshes each stayed, presents that finished after their refresh, drift steps
    /// and clock restarts.
    /// </summary>
    sealed class DisplayLockedStatistics
    {
        readonly long[] _holds = new long[6];
        long _firstVsync;
        long _lastVsync;
        long _frameStartVsync;
        long _periodTicks;
        long _refreshes;
        long _frames;
        long _late;
        long _restartsAtStart;
        long _restarts;
        long _wrapsAtStart;
        long _wraps;
        long _resyncsAtStart;
        long _resyncs;
        long _missedRefreshes;
        long _gaps;
        double _longestGapMs;
        long _slowPasses;
        double _longestWorkMs;
        long _starvedRefreshes;
        long _fallbackPasses;

        public bool HasData => _refreshes > 0;

        public long StartTimestamp { get; private set; }

        public void RecordFallbackPass() => _fallbackPasses++;

        public void Record(
            DisplayLockedSlot slot,
            bool newFrame,
            bool late,
            double workSeconds,
            bool starved,
            long clockRestarts,
            long phaseWraps,
            long timingResyncs)
        {
            if (_refreshes == 0)
            {
                StartTimestamp = Stopwatch.GetTimestamp();
                _firstVsync = slot.VsyncTimestamp;
                _frameStartVsync = slot.VsyncTimestamp;
                _restartsAtStart = clockRestarts;
                _wrapsAtStart = phaseWraps;
                _resyncsAtStart = timingResyncs;
            }
            else
            {
                // Refreshes between the last one handled and this one were missed: the loop woke
                // or finished too late, or passes ran on the source-cadence path.
                var missed = (long)Math.Round((slot.VsyncTimestamp - _lastVsync) / (double)slot.PeriodTicks) - 1;
                if (missed > 0)
                {
                    _missedRefreshes += missed;
                    _gaps++;
                    _longestGapMs = Math.Max(_longestGapMs, Stopwatch.GetElapsedTime(_lastVsync, slot.VsyncTimestamp).TotalMilliseconds);
                }
            }

            var workMs = workSeconds * 1000;
            if (workMs > slot.PeriodTicks * 500.0 / Stopwatch.Frequency)
                _slowPasses++;

            _longestWorkMs = Math.Max(_longestWorkMs, workMs);
            if (starved)
                _starvedRefreshes++;

            _resyncs = timingResyncs - _resyncsAtStart;
            _refreshes++;
            _lastVsync = slot.VsyncTimestamp;
            _periodTicks = slot.PeriodTicks;
            _restarts = clockRestarts - _restartsAtStart;
            _wraps = phaseWraps - _wrapsAtStart;
            if (late)
                _late++;

            if (!newFrame)
                return;

            // The previous frame stayed from its first refresh up to this one. Missed refreshes
            // (a late loop) still count: they are refreshes the frame stayed on screen for.
            if (_frames > 0)
            {
                var hold = (int)Math.Round((slot.VsyncTimestamp - _frameStartVsync) / (double)slot.PeriodTicks);
                _holds[Math.Clamp(hold, 0, _holds.Length - 1)]++;
            }

            _frames++;
            _frameStartVsync = slot.VsyncTimestamp;
        }

        public void Clear()
        {
            Array.Clear(_holds);
            _refreshes = 0;
            _frames = 0;
            _late = 0;
            _restarts = 0;
            _wraps = 0;
            _resyncs = 0;
            _missedRefreshes = 0;
            _gaps = 0;
            _longestGapMs = 0;
            _slowPasses = 0;
            _longestWorkMs = 0;
            _starvedRefreshes = 0;
            _fallbackPasses = 0;
        }

        public string Describe(string label)
        {
            var seconds = Stopwatch.GetElapsedTime(_firstVsync, _lastVsync).TotalSeconds;
            var hertz = _periodTicks > 0 ? Stopwatch.Frequency / (double)_periodTicks : 0;
            var holds = string.Join(
                ", ",
                Enumerable.Range(1, _holds.Length - 1)
                    .Where(hold => _holds[hold] > 0)
                    .Select(hold => string.Create(CultureInfo.InvariantCulture, $"{hold}{(hold == _holds.Length - 1 ? "+" : string.Empty)}: {_holds[hold]}")));
            return string.Create(
                CultureInfo.InvariantCulture,
                $"Display-locked timing over {seconds:0.0} s at {hertz:0.###} Hz{label}: {_frames} frames on {_refreshes} refreshes; "
                + $"refreshes per frame {(holds.Length > 0 ? holds : "n/a")}; {_late} presents finished after their refresh; "
                + $"{_wraps} cadence steps for clock drift; {_restarts} clock restarts (seeks, loops, jumps). "
                + $"Missed refreshes {_missedRefreshes} in {_gaps} gaps (longest {_longestGapMs:0} ms); "
                + $"passes over half a refresh {_slowPasses} (longest {_longestWorkMs:0.0} ms); "
                + $"refreshes waiting for an undecoded frame {_starvedRefreshes}; "
                + $"source-cadence passes {_fallbackPasses}; vertical-blank re-measurements {_resyncs}.");
        }
    }
}
