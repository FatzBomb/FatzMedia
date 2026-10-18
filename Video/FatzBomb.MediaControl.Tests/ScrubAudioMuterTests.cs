using FatzBomb.MediaControl.Controllers;

namespace FatzBomb.MediaControl.Tests;

/// <summary>
/// Silent scrubbing: audio mutes while scrub seeks keep arriving and comes back once the drag
/// rests for the quiet period, or when the scrub ends.
/// </summary>
public sealed class ScrubAudioMuterTests
{
    private static readonly TimeSpan Quiet = ScrubAudioMuter.DefaultQuietPeriod;
    private readonly FakeTimeProvider _time = new();
    private readonly List<(bool Muted, TimeSpan Ramp)> _applied = [];

    /// <summary>A scrub seek as the player makes it: tracked, then finished.</summary>
    private static void Seek(ScrubAudioMuter muter)
    {
        if (muter.OnScrubSeek())
            muter.OnScrubSeekFinished();
    }

    private ScrubAudioMuter Create(MediaScrubAudio mode = MediaScrubAudio.SilentWhileMoving) =>
        new((muted, ramp) => _applied.Add((muted, ramp)), _time) { Mode = mode };

    [Fact]
    public void Audible_LeavesTheAudioAlone()
    {
        using var muter = Create(MediaScrubAudio.Audible);
        Seek(muter);
        _time.Advance(Quiet * 2);
        muter.OnScrubEnded();

        Assert.Empty(_applied);
    }

    [Fact]
    public void AMovingDrag_StaysMuted_AndPlaysAgainOnceItRests()
    {
        using var muter = Create();
        Seek(muter);
        Assert.Equal([(true, TimeSpan.Zero)], _applied);

        // A drag that keeps moving: seeks closer together than the quiet period.
        for (var move = 0; move < 10; move++)
        {
            _time.Advance(TimeSpan.FromMilliseconds(100));
            Seek(muter);
        }

        Assert.True(muter.IsMuted);
        _time.Advance(Quiet - TimeSpan.FromMilliseconds(1));
        Assert.True(muter.IsMuted);
        Assert.Single(_applied);

        _time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.False(muter.IsMuted);
        Assert.Equal((false, ScrubAudioMuter.UnmuteRamp), _applied[^1]);
        Assert.Equal(2, _applied.Count);
    }

    [Fact]
    public void ASlowSeek_HoldsTheMute_UntilAQuietPeriodAfterItReturns()
    {
        // A network seek can outlast the quiet period; the drag moves on meanwhile, its requests
        // waiting on the control queue behind it.
        using var muter = Create();
        Assert.True(muter.OnScrubSeek());
        _time.Advance(Quiet * 3);
        Assert.True(muter.IsMuted);

        muter.OnScrubSeekFinished();
        _time.Advance(Quiet - TimeSpan.FromMilliseconds(1));
        Assert.True(muter.IsMuted);

        _time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.False(muter.IsMuted);
        Assert.Equal([(true, TimeSpan.Zero), (false, ScrubAudioMuter.UnmuteRamp)], _applied);
    }

    [Fact]
    public void AnUntrackedSeek_ReportsSo()
    {
        using var muter = Create(MediaScrubAudio.Audible);
        Assert.False(muter.OnScrubSeek());
    }

    [Fact]
    public void ADragThatMovesAgain_MutesAgain()
    {
        using var muter = Create();
        Seek(muter);
        _time.Advance(Quiet);
        Seek(muter);

        Assert.True(muter.IsMuted);
        Assert.Equal([(true, TimeSpan.Zero), (false, ScrubAudioMuter.UnmuteRamp), (true, TimeSpan.Zero)], _applied);
    }

    [Fact]
    public void EndingTheScrub_PlaysAtOnce_AndTheTimerAddsNothing()
    {
        using var muter = Create();
        Seek(muter);
        muter.OnScrubEnded();
        _time.Advance(Quiet * 2);
        muter.OnScrubEnded();

        Assert.False(muter.IsMuted);
        Assert.Equal([(true, TimeSpan.Zero), (false, ScrubAudioMuter.UnmuteRamp)], _applied);
    }

    [Fact]
    public void SwitchingToAudible_MidDrag_Unmutes()
    {
        using var muter = Create();
        Seek(muter);
        muter.Mode = MediaScrubAudio.Audible;
        Seek(muter);
        _time.Advance(Quiet * 2);

        Assert.False(muter.IsMuted);
        Assert.Equal([(true, TimeSpan.Zero), (false, ScrubAudioMuter.UnmuteRamp)], _applied);
    }

    [Fact]
    public void AfterDispose_NothingIsApplied()
    {
        var muter = Create();
        muter.Dispose();
        Seek(muter);
        _time.Advance(Quiet * 2);

        Assert.Empty(_applied);
    }

    [Fact]
    public void AnUnexpectedFailureWhileRestoring_IsReported_NotThrownOnTheTimer()
    {
        var fail = false;
        var diagnostics = new List<string>();
        void OnDiagnostic(MediaDiagnosticInfo info)
        {
            if (info.Category == nameof(ScrubAudioMuter))
                lock (diagnostics)
                    diagnostics.Add(info.Message);
        }

        DiagnosticRouter.DiagnosticOccurred += OnDiagnostic;
        try
        {
            using var muter = new ScrubAudioMuter((_, _) =>
            {
                if (fail)
                    throw new IOException("device lost");
            }, _time)
            { Mode = MediaScrubAudio.SilentWhileMoving };

            Seek(muter);
            fail = true;
            _time.Advance(Quiet);

            lock (diagnostics)
                Assert.Contains(diagnostics, message => message.Contains("device lost", StringComparison.Ordinal));
        }
        finally
        {
            DiagnosticRouter.DiagnosticOccurred -= OnDiagnostic;
        }
    }

    [Fact]
    public void AFailingEngine_DoesNotBreakTheMuter()
    {
        var calls = 0;
        using var muter = new ScrubAudioMuter((_, _) =>
        {
            calls++;
            throw new ObjectDisposedException("engine");
        }, _time)
        { Mode = MediaScrubAudio.SilentWhileMoving };

        Seek(muter);
        _time.Advance(Quiet);

        Assert.Equal(2, calls);
        Assert.False(muter.IsMuted);
    }

    /// <summary>Manual clock with one-shot timers that fire, in order, as time advances.</summary>
    private sealed class FakeTimeProvider : TimeProvider
    {
        private readonly List<FakeTimer> _timers = [];
        private long _now;

        public override long TimestampFrequency => 1_000;

        public override long GetTimestamp() => _now;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new FakeTimer(this, callback, state);
            _ = timer.Change(dueTime, period);
            _timers.Add(timer);
            return timer;
        }

        public void Advance(TimeSpan duration)
        {
            var target = _now + (long)duration.TotalMilliseconds;
            while (_timers.Where(timer => timer.DueAt <= target).MinBy(timer => timer.DueAt) is { } due)
            {
                _now = due.DueAt!.Value;
                due.Fire();
            }

            _now = target;
        }

        private sealed class FakeTimer(FakeTimeProvider owner, TimerCallback callback, object? state) : ITimer
        {
            public long? DueAt { get; private set; }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                Assert.Equal(Timeout.InfiniteTimeSpan, period);
                DueAt = dueTime == Timeout.InfiniteTimeSpan ? null : owner._now + (long)dueTime.TotalMilliseconds;
                return true;
            }

            public void Fire()
            {
                DueAt = null;
                callback(state);
            }

            public void Dispose() => DueAt = null;

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
