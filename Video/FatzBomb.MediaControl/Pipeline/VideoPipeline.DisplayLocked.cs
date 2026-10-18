using FatzBomb.MediaControl.Core.Graphics.Cadence;
using System.Diagnostics;
using System.Globalization;

namespace FatzBomb.MediaControl.Pipeline;

/// <summary>
/// Display-locked video timing (Stage 017A slice 4): with <see cref="VideoFrameTiming.DisplayLocked"/>
/// the presentation loop wakes a few milliseconds before each refresh of the video's display and
/// presents the frame for the moment that refresh reaches the screen, from the display's
/// vertical-blank timing and a jitter-filtered media clock (<see cref="VsyncMediaClock"/>).
/// Without that timing it keeps the source-cadence path, unchanged.
/// </summary>
internal sealed partial class VideoPipeline : IVideoFrameTimingControl, IPresentationTimingSink
{
    private const string FrameTimingDiagnosticCategory = "VideoFrameTiming";
    private static readonly long DisplayLockedReportIntervalTicks = Stopwatch.Frequency * 60;
    private static readonly long DisplayLockedStateSettleTicks = Stopwatch.Frequency / 2;

    private readonly List<PresentationTimingGroup> _timingGroups = [];
    private readonly DisplayLockedStatistics _displayLockedStatistics = new();
    private readonly CadencePhaseLock _phaseLock = new();
    private long _displayLockedClockRestarts;
    private PresentationTimingSource? _presentationTimingSource;
    private volatile VideoFrameTiming _frameTiming = VideoFrameTiming.SourceCadence;
    private VsyncMediaClock? _vsyncClock;
    private long _displayLockedLastVsync;
    private double _displayLockedWorkSeconds = double.NaN;
    private int _displayLockedResetRequested;
    private string? _displayLockedReportedState;
    private string? _displayLockedPendingState;
    private long _displayLockedPendingSince;
    private string? _displayLockedOnState;
    private long _displayLockedOnStatePeriod;

    /// <inheritdoc cref="IVideoFrameTimingControl.FrameTiming"/>
    public VideoFrameTiming FrameTiming
    {
        get => _frameTiming;
        set => _frameTiming = value;
    }

    void IPresentationTimingSink.SetPresentationTimingSource(PresentationTimingSource? source) =>
        Volatile.Write(ref _presentationTimingSource, source);

    /// <summary>Restarts the filtered clock on the next refresh (open, close, seek).</summary>
    private void RequestDisplayLockedReset() => Volatile.Write(ref _displayLockedResetRequested, 1);

    /// <summary>
    /// The refresh to present for next, when display-locked timing applies: the mode is on, the
    /// video's frame rate is known, and the renderer reports current vertical-blank timing for the
    /// display showing the primary surface. Presentation thread only.
    /// </summary>
    private bool TryGetDisplayLockedSlot(out DisplayLockedSlot slot)
    {
        slot = default;
        if (_frameTiming != VideoFrameTiming.DisplayLocked)
            return NoDisplayLockedSlot("off");

        if (!(FramesPerSecond > 0))
            return NoDisplayLockedSlot("unavailable: the video's frame rate is unknown");

        if (!TryGetPrimaryTimingGroup(out PresentationTimingGroup group))
        {
            return NoDisplayLockedSlot(
                "unavailable: the renderer reports no vertical-blank timing for the video's display "
                + "(software output or a WPF-composited surface)");
        }

        var budget = DisplayLockedTiming.GetBudgetTicks(_displayLockedWorkSeconds, group.Timing.PeriodTicks);
        if (!DisplayLockedTiming.TryGetNextSlot(group.Timing, Stopwatch.GetTimestamp(), budget, _displayLockedLastVsync, out slot))
            return NoDisplayLockedSlot("unavailable: the display's vertical-blank timing is not current");

        NoteDisplayLockedState(GetDisplayLockedOnState(group.Timing.PeriodTicks));
        return true;
    }

    // Built only when the refresh period changes: this runs once per refresh.
    private string GetDisplayLockedOnState(long periodTicks)
    {
        if (_displayLockedOnState is null || Math.Abs(periodTicks - _displayLockedOnStatePeriod) > periodTicks / 1000)
        {
            var hertz = Stopwatch.Frequency / (double)periodTicks;
            _displayLockedOnState = string.Create(CultureInfo.InvariantCulture, $"on: frames follow the {hertz:0.###} Hz refreshes of the video's display");
            _displayLockedOnStatePeriod = periodTicks;
        }

        return _displayLockedOnState;
    }

    private bool TryGetPrimaryTimingGroup(out PresentationTimingGroup group)
    {
        group = default;
        PresentationTimingSource? source = Volatile.Read(ref _presentationTimingSource);
        if (source is null)
            return false;

        _timingGroups.Clear();
        try
        {
            source(_timingGroups);
        }
        catch (Exception ex)
        {
            // A renderer bug must not stop playback: the source-cadence path takes over.
            Debug.WriteLine($"[VideoPipeline] Presentation timing query failed: {ex.Message}");
            return false;
        }

        foreach (PresentationTimingGroup candidate in _timingGroups)
        {
            if (candidate.IncludesPrimary && candidate.Timing.IsValid)
            {
                group = candidate;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Presents the frame for <paramref name="slot"/>'s refresh and returns the raw clock position
    /// for the loop's end and loop-wrap checks. Runs under the presentation lock.
    /// </summary>
    private double PresentFrameForDisplayLockedSlot(DisplayLockedSlot slot)
    {
        var workStart = Stopwatch.GetTimestamp();
        VsyncMediaClock clock = _vsyncClock ??= new VsyncMediaClock(Stopwatch.Frequency);
        if (Interlocked.Exchange(ref _displayLockedResetRequested, 0) != 0)
            clock.Reset();

        var clockPosition = GetPresentationPosition();
        var secondsToVsync = (slot.VsyncTimestamp - Stopwatch.GetTimestamp()) / (double)Stopwatch.Frequency;
        var mediaAtVsync = clock.Update(slot.VsyncTimestamp, clockPosition + (secondsToVsync * clock.Rate));
        var periodSeconds = slot.PeriodTicks / (double)Stopwatch.Frequency;

        // A restarted clock (seek, loop, jump) starts the cadence afresh: the phase lock forgets
        // its offset. It also clears itself when the refresh period or frame rate changes.
        _phaseLock.Configure(periodSeconds, FramesPerSecond);
        if (clock.Restarts != _displayLockedClockRestarts)
        {
            _displayLockedClockRestarts = clock.Restarts;
            _phaseLock.Reset();
        }

        var selection = ApplyAudioVideoOffset(DisplayLockedTiming.GetSelectionTime(mediaAtVsync, periodSeconds) + _phaseLock.Offset);

        var shownBefore = _currentFrame?.PresentationTime;
        PresentFrameForPosition(selection, selection, forceReissue: false, displayLocked: true);
        var shownAfter = _currentFrame?.PresentationTime;
        var newFrame = shownAfter is { } after && after != shownBefore;
        if (newFrame)
            _phaseLock.OnFrameStarted(selection, shownAfter!.Value);

        var workEnd = Stopwatch.GetTimestamp();
        _displayLockedLastVsync = slot.VsyncTimestamp;
        var workSeconds = (workEnd - workStart) / (double)Stopwatch.Frequency;
        _displayLockedWorkSeconds = double.IsNaN(_displayLockedWorkSeconds)
            ? workSeconds
            : (_displayLockedWorkSeconds * 0.9) + (workSeconds * 0.1);

        _displayLockedStatistics.Record(
            slot,
            newFrame,
            late: workEnd > slot.VsyncTimestamp,
            clock.Restarts,
            _phaseLock.Wraps);
        ReportDisplayLockedStatisticsIfDue(force: false);
        return clockPosition;
    }

    /// <summary>The loop is not presenting (paused, stopped): report what the last run did.</summary>
    private void OnDisplayLockedIdle()
    {
        ReportDisplayLockedStatisticsIfDue(force: true);
        _displayLockedStatistics.Clear();
    }

    private bool NoDisplayLockedSlot(string state)
    {
        if (_displayLockedStatistics.HasData)
        {
            ReportDisplayLockedStatisticsIfDue(force: true);
            _displayLockedStatistics.Clear();
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

    private void ReportDisplayLockedStatisticsIfDue(bool force)
    {
        if (!_displayLockedStatistics.HasData
            || (!force && Stopwatch.GetTimestamp() - _displayLockedStatistics.StartTimestamp < DisplayLockedReportIntervalTicks))
        {
            return;
        }

        DiagnosticRouter.Raise(MediaDiagnosticLevel.Info, FrameTimingDiagnosticCategory, _displayLockedStatistics.Describe(), _currentVideoPath);
        _displayLockedStatistics.Clear();
    }

    /// <summary>
    /// What display-locked timing did over a run: refreshes handled, frames shown and how many
    /// refreshes each stayed, presents that finished after their refresh, and clock restarts.
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
        private long _restartsAtStart = -1;
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
            _restartsAtStart = -1;
            _wraps = 0;
        }

        public string Describe()
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
                $"Display-locked timing over {seconds:0.0} s at {hertz:0.###} Hz: {_frames} frames on {_refreshes} refreshes; "
                + $"refreshes per frame {(holds.Length > 0 ? holds : "n/a")}; {_late} presents finished after their refresh; "
                + $"{_wraps} cadence steps for clock drift; {_restarts} clock restarts (seeks, loops, jumps).");
        }
    }
}
