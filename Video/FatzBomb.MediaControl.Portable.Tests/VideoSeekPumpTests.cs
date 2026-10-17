using FatzBomb.MediaControl.Pipeline;

namespace FatzBomb.MediaControl.Portable.Tests;

/// <summary>
/// Stage 015A slice 2: the seek pump's ordering, replayed synchronously against a fake host.
/// Every seek bug found in the 035A smoke rounds lived in this logic.
/// </summary>
public sealed class VideoSeekPumpTests
{
    [Fact]
    public void LoneClick_PreviewsExactlyThenSettlesOnTheTarget()
    {
        var host = new FakeHost();
        host.Pump.Request(10);

        host.Pump.DrainPendingRequests();

        Assert.Equal(["preview 10 exact", "settle 10"], host.Calls);
    }

    [Fact]
    public void DragThenHold_SettlesOnTheFrameTheLastPreviewShowed()
    {
        var host = new FakeHost { ShownFrameOffset = -1.3 };
        host.Pump.Request(10);
        host.DuringNextWait(() => host.Pump.Request(20)); // the mouse moves on

        host.Pump.DrainPendingRequests();

        Assert.Equal(["preview 10 exact", "preview 20 nearby", "settle 18.7"], host.Calls);
    }

    [Fact]
    public void ReleaseAtTheDragPosition_DoesNotPreviewAgain()
    {
        var host = new FakeHost { ShownFrameOffset = -1.3 };
        host.Pump.Request(10);
        host.DuringNextWait(() => host.Pump.Request(20));
        host.DuringNextWait(() => host.Pump.Request(20.01)); // within the 25 ms change threshold

        host.Pump.DrainPendingRequests();

        Assert.Equal(["preview 10 exact", "preview 20 nearby", "settle 18.7"], host.Calls);
    }

    [Fact]
    public void FastDrag_OnlyTheLatestTargetPerIntervalIsPreviewed()
    {
        var host = new FakeHost();
        host.Pump.Request(10);
        host.DuringNextWait(() =>
        {
            host.Pump.Request(11);
            host.Pump.Request(12);
            host.Pump.Request(13);
        });

        host.Pump.DrainPendingRequests();

        Assert.Equal(["preview 10 exact", "preview 13 nearby", "settle 13"], host.Calls);
    }

    [Fact]
    public void PreviewWithoutAFrame_SettlesExactlyOnTheTarget()
    {
        // No frame to resume from, so the settle seeks exactly, without waiting for the interval.
        var host = new FakeHost { PreviewPresents = false, ShownFrameOffset = -1.3 };
        host.Pump.Request(10);

        host.Pump.DrainPendingRequests();

        Assert.Equal(["preview 10 exact", "settle 10"], host.Calls);
        Assert.Equal(0, host.Waits);
    }

    [Fact]
    public void PreviewWithoutAFrame_WithANewerRequest_WaitsThenPreviewsIt()
    {
        var host = new FakeHost { PreviewPresents = false };
        host.Pump.Request(10);
        host.DuringNextPreview(() => host.Pump.Request(20)); // the mouse moved during the preview

        host.Pump.DrainPendingRequests();

        Assert.Equal(["preview 10 exact", "preview 20 nearby", "settle 20"], host.Calls);
        Assert.Equal(1, host.Waits);
    }

    [Fact]
    public void Targets_AreNormalizedByTheHost()
    {
        var host = new FakeHost { Duration = 60 };
        host.Pump.Request(75);

        host.Pump.DrainPendingRequests();

        Assert.Equal(["preview 60 exact", "settle 60"], host.Calls);
    }

    [Fact]
    public void Sequence_IsActiveFromRequestUntilTheSettleFinishes()
    {
        var host = new FakeHost();
        Assert.False(host.Pump.IsSequenceActive);

        host.Pump.Request(10);
        Assert.True(host.Pump.IsSequenceActive);

        host.Pump.DrainPendingRequests();
        Assert.False(host.Pump.IsSequenceActive);
    }

    [Fact]
    public void RequestDuringTheSettle_KeepsTheSequenceActiveForTheNextBurst()
    {
        // Seek then Play: a Play that arrives while a newer seek is queued must still defer to
        // that seek's settle, so the sequence stays active.
        var host = new FakeHost();
        host.Pump.Request(10);
        host.DuringSettle(() => host.Pump.Request(30));

        host.Pump.DrainPendingRequests();

        Assert.True(host.Pump.IsSequenceActive);
        Assert.True(host.Pump.HasPendingRequest);

        host.Pump.DrainPendingRequests();
        Assert.Equal(["preview 10 exact", "settle 10", "preview 30 exact", "settle 30"], host.Calls);
        Assert.False(host.Pump.IsSequenceActive);
    }

    [Fact]
    public void SupersedingRequest_DependsOnTheActiveTarget()
    {
        var pump = new FakeHost().Pump;
        Assert.False(pump.HasSupersedingRequest);

        pump.Request(10);
        Assert.True(pump.HasSupersedingRequest); // nothing in progress yet

        pump.SetActiveTarget(10.01);
        Assert.False(pump.HasSupersedingRequest); // same target within the threshold

        pump.SetActiveTarget(5);
        Assert.True(pump.HasSupersedingRequest);
    }

    [Fact]
    public void ClearActiveTarget_OnlyClearsItsOwnTarget()
    {
        var pump = new FakeHost().Pump;
        pump.Request(10);
        pump.SetActiveTarget(10);

        pump.ClearActiveTarget(20);
        Assert.False(pump.HasSupersedingRequest);

        pump.ClearActiveTarget(10);
        Assert.True(pump.HasSupersedingRequest);
    }

    [Fact]
    public void CancelAndReset_ForgetTheRequest()
    {
        var pump = new FakeHost().Pump;
        pump.Request(10);
        pump.SetMinimumCommittedTarget(10);

        pump.CancelPendingRequest();

        Assert.False(pump.HasPendingRequest);
        Assert.False(pump.IsSequenceActive);
        Assert.True(double.IsNaN(pump.MinimumCommittedTarget));

        pump.Request(12);
        pump.SetActiveTarget(3);
        pump.Reset();
        Assert.False(pump.HasPendingRequest);
        Assert.False(pump.HasSupersedingRequest);
    }

    [Fact]
    public async Task WorkerThread_SettlesARequestAndStopsCleanly()
    {
        var host = new FakeHost { RealWait = true };
        var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.OnSettle = settled.SetResult;

        host.Pump.Start();
        host.Pump.Request(5);
        await settled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        host.Pump.Stop();

        Assert.Contains("settle 5", host.Calls);
        host.Pump.Dispose();
    }

    private sealed class FakeHost : IVideoSeekPumpHost
    {
        private readonly Queue<Action> _waitActions = new();
        private readonly Queue<Action> _settleActions = new();
        private readonly Queue<Action> _previewActions = new();
        private double _currentFrameTime = double.NaN;

        public FakeHost() => Pump = new VideoSeekPump(this);

        public VideoSeekPump Pump { get; }
        public List<string> Calls { get; } = [];
        public double Duration { get; init; } = 3600;
        public bool PreviewPresents { get; init; } = true;
        /// <summary>Where a nearby-frame preview lands relative to its target.</summary>
        public double ShownFrameOffset { get; init; }
        public bool RealWait { get; init; }
        public Action? OnSettle { get; set; }

        public double CurrentFrameTime => _currentFrameTime;

        public void DuringNextWait(Action action) => _waitActions.Enqueue(action);

        public void DuringSettle(Action action) => _settleActions.Enqueue(action);

        public void DuringNextPreview(Action action) => _previewActions.Enqueue(action);

        public int Waits { get; private set; }

        public double NormalizeSeekTarget(double seconds) => VideoSeekPolicy.NormalizeTarget(seconds, Duration);

        public bool RunSeek(double target, bool asPreview, bool allowPreviewFallback)
        {
            // Mirrors VideoPipeline.SeekCore's use of the pump's active target and sequence.
            Pump.SetActiveTarget(target);
            try
            {
                if (asPreview)
                {
                    Calls.Add($"preview {Format(target)} {(allowPreviewFallback ? "nearby" : "exact")}");
                    if (_previewActions.TryDequeue(out Action? previewAction))
                        previewAction();
                    if (PreviewPresents)
                        _currentFrameTime = allowPreviewFallback ? target + ShownFrameOffset : target;
                    return PreviewPresents;
                }

                Calls.Add($"settle {Format(target)}");
                _currentFrameTime = target;
                if (_settleActions.TryDequeue(out Action? action))
                    action();
                OnSettle?.Invoke();
                return true;
            }
            finally
            {
                Pump.ClearActiveTarget(target);
                if (!asPreview)
                    Pump.EndSequenceIfSettled();
            }
        }

        public void WaitForPumpInterval()
        {
            Waits++;
            if (RealWait)
                Pump.WaitInterval(1);
            else if (_waitActions.TryDequeue(out Action? action))
                action();
        }

        private static string Format(double value) => value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
    }
}
