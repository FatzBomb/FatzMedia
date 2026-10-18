using FatzBomb.MediaControl.Core.Graphics.Cadence;
using FatzBomb.MediaControl.Core.Graphics.Composition;
using System.Diagnostics;
using System.Globalization;

namespace FatzBomb.MediaControl.Pipeline;

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
internal sealed partial class VideoPipeline : IVideoFrameTimingControl, IPresentationTimingSink
{
    private const string FrameTimingDiagnosticCategory = "VideoFrameTiming";
    private static readonly long DisplayLockedReportIntervalTicks = Stopwatch.Frequency * 60;
    private static readonly long DisplayLockedStateSettleTicks = Stopwatch.Frequency / 2;

    // The targets of the frame being raised on this thread (null: every target).
    [ThreadStatic]
    private static MediaCompositionTargetFilter? t_frameReadyTargets;

    private readonly List<PresentationTimingGroup> _timingGroups = [];
    private readonly Dictionary<long, DisplayLockedTarget> _displayLockedTargets = [];
    private PresentationTimingSource? _presentationTimingSource;
    private volatile VideoFrameTiming _frameTiming = VideoFrameTiming.SourceCadence;
    private int _displayLockedResetRequested;
    private string? _displayLockedReportedState;
    private string? _displayLockedPendingState;
    private long _displayLockedPendingSince;
    private string? _displayLockedOnState;
    private string _displayLockedOnStateKey = string.Empty;

    /// <inheritdoc cref="IVideoFrameTimingControl.FrameTiming"/>
    public VideoFrameTiming FrameTiming
    {
        get => _frameTiming;
        set => _frameTiming = value;
    }

    void IPresentationTimingSink.SetPresentationTimingSource(PresentationTimingSource? source) =>
        Volatile.Write(ref _presentationTimingSource, source);

    /// <summary>Restarts the filtered clocks on the next refresh (open, close, seek).</summary>
    private void RequestDisplayLockedReset() => Volatile.Write(ref _displayLockedResetRequested, 1);

    /// <summary>
    /// The next refresh to present for, across the displays showing the video, when display-locked
    /// timing applies: the mode is on, the video's frame rate is known, and the renderer reports
    /// current vertical-blank timing for the primary surface's display. Presentation thread only.
    /// </summary>
    private bool TryGetDisplayLockedSlot(out DisplayLockedSlot slot, out DisplayLockedTarget? target)
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
        foreach (PresentationTimingGroup group in _timingGroups)
        {
            if (!group.Timing.IsValid)
                continue;

            DisplayLockedTarget display = GetDisplayLockedTarget(group);
            var budget = DisplayLockedTiming.GetBudgetTicks(display.WorkSeconds, group.Timing.PeriodTicks);
            if (!DisplayLockedTiming.TryGetNextSlot(group.Timing, now, budget, display.LastVsync, out DisplayLockedSlot candidate))
                continue;

            primaryCurrent |= group.IncludesPrimary;
            if (target is null || candidate.WakeTimestamp < slot.WakeTimestamp)
            {
                slot = candidate;
                target = display;
            }
        }

        ForgetDisplaysNotShowingTheVideo();
        if (target is null || !primaryCurrent)
        {
            target = null;
            return NoDisplayLockedSlot("unavailable: the display's vertical-blank timing is not current");
        }

        NoteDisplayLockedState(GetDisplayLockedOnState());
        return true;
    }

    private bool QueryTimingGroups()
    {
        PresentationTimingSource? source = Volatile.Read(ref _presentationTimingSource);
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

        foreach (PresentationTimingGroup group in _timingGroups)
            if (group.IncludesPrimary && group.Timing.IsValid)
                return true;

        return false;
    }

    private DisplayLockedTarget GetDisplayLockedTarget(PresentationTimingGroup group)
    {
        if (!_displayLockedTargets.TryGetValue(group.Timing.DisplayKey, out DisplayLockedTarget? display))
        {
            display = new DisplayLockedTarget();
            _displayLockedTargets.Add(group.Timing.DisplayKey, display);
        }

        display.Group = group;
        display.Seen = true;
        return display;
    }

    private void ForgetDisplaysNotShowingTheVideo()
    {
        List<long>? gone = null;
        foreach ((var key, DisplayLockedTarget display) in _displayLockedTargets)
        {
            if (!display.Seen)
            {
                ReportDisplayLockedStatisticsIfDue(display, force: true);
                (gone ??= []).Add(key);
            }

            display.Seen = false;
        }

        if (gone is not null)
            foreach (var key in gone)
                _ = _displayLockedTargets.Remove(key);
    }

    // Built only when the displays or their periods change: this runs once per refresh.
    private string GetDisplayLockedOnState()
    {
        var key = string.Join(",", _displayLockedTargets.Values.Select(static display => display.Group.Timing.PeriodTicks / 1000));
        if (_displayLockedOnState is null || key != _displayLockedOnStateKey)
        {
            var rates = _displayLockedTargets.Values
                .OrderByDescending(static display => display.IncludesPrimary)
                .Select(static display => (Stopwatch.Frequency / (double)display.Group.Timing.PeriodTicks).ToString("0.###", CultureInfo.InvariantCulture))
                .ToArray();
            _displayLockedOnState = rates.Length == 1
                ? $"on: frames follow the {rates[0]} Hz refreshes of the video's display"
                : $"on: {rates.Length} displays ({string.Join(" Hz, ", rates)} Hz), each following its own refreshes";
            _displayLockedOnStateKey = key;
        }

        return _displayLockedOnState;
    }

    /// <summary>
    /// Presents the frame for <paramref name="slot"/>'s refresh to <paramref name="display"/>'s
    /// surfaces and returns the raw clock position for the loop's end and loop-wrap checks. Runs
    /// under the presentation lock.
    /// </summary>
    private double PresentFrameForDisplayLockedSlot(DisplayLockedSlot slot, DisplayLockedTarget display)
    {
        var workStart = Stopwatch.GetTimestamp();
        if (Interlocked.Exchange(ref _displayLockedResetRequested, 0) != 0)
        {
            foreach (DisplayLockedTarget each in _displayLockedTargets.Values)
                each.Clock.Reset();
        }

        VsyncMediaClock clock = display.Clock;
        var clockPosition = GetPresentationPosition();
        var secondsToVsync = (slot.VsyncTimestamp - Stopwatch.GetTimestamp()) / (double)Stopwatch.Frequency;
        var mediaAtVsync = clock.Update(slot.VsyncTimestamp, clockPosition + (secondsToVsync * clock.Rate));
        var periodSeconds = slot.PeriodTicks / (double)Stopwatch.Frequency;

        // A restarted clock (seek, loop, jump) starts the cadence afresh: the phase lock forgets
        // its offset. It also clears itself when the refresh period or frame rate changes.
        CadencePhaseLock phaseLock = display.PhaseLock;
        phaseLock.Configure(periodSeconds, FramesPerSecond);
        if (clock.Restarts != display.ClockRestarts)
        {
            display.ClockRestarts = clock.Restarts;
            phaseLock.Reset();
        }

        var selection = ApplyAudioVideoOffset(DisplayLockedTiming.GetSelectionTime(mediaAtVsync, periodSeconds) + phaseLock.Offset);
        MediaCompositionTargetFilter? targets = display.Group.Targets;
        double? shown = null;
        t_frameReadyTargets = targets;
        try
        {
            var before = _currentFrame?.PresentationTime;
            PresentFrameForPosition(selection, selection, forceReissue: false, displayLocked: true);
            if (_currentFrame is { } current)
            {
                if (current.PresentationTime != before)
                {
                    shown = current.PresentationTime;
                }
                else if (targets is not null
                    && current.PresentationTime != display.LastShown
                    && current.PresentationTime <= selection + DisplayLockedPtsToleranceSeconds)
                {
                    // Another display moved the frame on; this one shows it at its own refresh.
                    ReissueCurrentFrame();
                    shown = current.PresentationTime;
                }
            }
        }
        finally
        {
            t_frameReadyTargets = null;
        }

        var newFrame = shown is not null && shown != display.LastShown;
        if (shown is { } shownTime)
        {
            display.LastShown = shownTime;
            if (newFrame)
                phaseLock.OnFrameStarted(selection, shownTime);
        }

        var workEnd = Stopwatch.GetTimestamp();
        display.LastVsync = slot.VsyncTimestamp;
        var workSeconds = (workEnd - workStart) / (double)Stopwatch.Frequency;
        display.WorkSeconds = double.IsNaN(display.WorkSeconds)
            ? workSeconds
            : (display.WorkSeconds * 0.9) + (workSeconds * 0.1);

        display.Statistics.Record(slot, newFrame, late: workEnd > slot.VsyncTimestamp, clock.Restarts, phaseLock.Wraps);
        ReportDisplayLockedStatisticsIfDue(display, force: false);
        return clockPosition;
    }

    /// <summary>The loop is not presenting (paused, stopped): report what the last run did.</summary>
    private void OnDisplayLockedIdle()
    {
        foreach (DisplayLockedTarget display in _displayLockedTargets.Values)
        {
            ReportDisplayLockedStatisticsIfDue(display, force: true);
            display.LastShown = null;
        }
    }

    private bool NoDisplayLockedSlot(string state)
    {
        if (_displayLockedTargets.Count > 0)
        {
            foreach (DisplayLockedTarget display in _displayLockedTargets.Values)
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
    private void NoteDisplayLockedState(string state)
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

    private void ReportDisplayLockedStatisticsIfDue(DisplayLockedTarget display, bool force)
    {
        DisplayLockedStatistics statistics = display.Statistics;
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
    private sealed class DisplayLockedTarget
    {
        public VsyncMediaClock Clock { get; } = new(Stopwatch.Frequency);

        public CadencePhaseLock PhaseLock { get; } = new();

        public DisplayLockedStatistics Statistics { get; } = new();

        public PresentationTimingGroup Group { get; set; }

        public bool IncludesPrimary => Group.IncludesPrimary;

        public bool Seen { get; set; }

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
    private sealed class DisplayLockedStatistics
    {
        private readonly long[] _holds = new long[6];
        private long _firstVsync;
        private long _lastVsync;
        private long _frameStartVsync;
        private long _periodTicks;
        private long _refreshes;
        private long _frames;
        private long _late;
        private long _restartsAtStart;
        private long _restarts;
        private long _wrapsAtStart;
        private long _wraps;

        public bool HasData => _refreshes > 0;

        public long StartTimestamp { get; private set; }

        public void Record(DisplayLockedSlot slot, bool newFrame, bool late, long clockRestarts, long phaseWraps)
        {
            if (_refreshes == 0)
            {
                StartTimestamp = Stopwatch.GetTimestamp();
                _firstVsync = slot.VsyncTimestamp;
                _frameStartVsync = slot.VsyncTimestamp;
                _restartsAtStart = clockRestarts;
                _wrapsAtStart = phaseWraps;
            }

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
        }

        public string Describe(string label)
        {
            var seconds = (_lastVsync - _firstVsync) / (double)Stopwatch.Frequency;
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
                + $"{_wraps} cadence steps for clock drift; {_restarts} clock restarts (seeks, loops, jumps).");
        }
    }
}
