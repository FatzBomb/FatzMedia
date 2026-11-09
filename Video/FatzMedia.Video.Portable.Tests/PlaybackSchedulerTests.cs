using FatzMedia.Video.Pipeline;

namespace FatzMedia.Video.Portable.Tests;

/// <summary>
/// Stage 022A slice 3: <see cref="PlaybackScheduler"/> reuses one worker thread across
/// restarts (each seek restarts the decode and presentation loops) instead of creating one per
/// <see cref="PlaybackScheduler.Start"/>.
/// </summary>
public sealed class PlaybackSchedulerTests
{
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public void Restarts_RunOnTheSameThread()
    {
        using var scheduler = new PlaybackScheduler("Test");
        var threadIds = new List<int>();

        for (var run = 0; run < 20; run++)
        {
            using var started = new ManualResetEventSlim();
            scheduler.Start(token =>
            {
                lock (threadIds)
                    threadIds.Add(Environment.CurrentManagedThreadId);
                started.Set();
                _ = token.WaitHandle.WaitOne();
            });
            Assert.True(started.Wait(Timeout));
            scheduler.StopAndWait();
        }

        Assert.Equal(1, scheduler.WorkerThreadsCreated);
        Assert.Single(threadIds.Distinct());
    }

    [Fact]
    public void Start_CancelsTheCurrentLoop_AndLoopsNeverOverlap()
    {
        using var scheduler = new PlaybackScheduler("Test");
        var active = 0;
        var maxActive = 0;
        using var firstStarted = new ManualResetEventSlim();
        using var secondStarted = new ManualResetEventSlim();

        void Loop(CancellationToken token, ManualResetEventSlim started)
        {
            var now = Interlocked.Increment(ref active);
            InterlockedMax(ref maxActive, now);
            started.Set();
            _ = token.WaitHandle.WaitOne();
            Thread.Sleep(20); // still finishing after cancellation
            _ = Interlocked.Decrement(ref active);
        }

        scheduler.Start(token => Loop(token, firstStarted));
        Assert.True(firstStarted.Wait(Timeout));
        scheduler.Start(token => Loop(token, secondStarted));
        Assert.True(secondStarted.Wait(Timeout));
        scheduler.StopAndWait();

        Assert.Equal(1, maxActive);
        Assert.Equal(0, Volatile.Read(ref active));
    }

    [Fact]
    public void StopAndWait_ReturnsAfterTheLoopHasReturned()
    {
        using var scheduler = new PlaybackScheduler("Test");
        using var started = new ManualResetEventSlim();
        var returned = false;

        scheduler.Start(token =>
        {
            started.Set();
            _ = token.WaitHandle.WaitOne();
            Thread.Sleep(50);
            Volatile.Write(ref returned, true);
        });
        Assert.True(started.Wait(Timeout));
        Assert.True(scheduler.StopAndWait());

        Assert.True(Volatile.Read(ref returned));
    }

    [Fact]
    public void StopAndWait_WithNothingRunning_ReportsStopped()
    {
        using var scheduler = new PlaybackScheduler("Test");

        Assert.True(scheduler.StopAndWait());
    }

    [Fact]
    public void ALoopCanRestartItsOwnScheduler()
    {
        // VideoPipeline restarts its presentation loop from inside it (loop wrap, seek recovery).
        using var scheduler = new PlaybackScheduler("Test");
        using var restarted = new ManualResetEventSlim();

        scheduler.Start(_ =>
        {
            scheduler.StopAndWait(); // from the worker: cancels only, does not wait on itself
            scheduler.Start(_ => restarted.Set());
        });

        Assert.True(restarted.Wait(Timeout));
        Assert.Equal(1, scheduler.WorkerThreadsCreated);
    }

    [Fact]
    public void AStartReplacedBeforeItRuns_NeverRuns()
    {
        using var scheduler = new PlaybackScheduler("Test");
        using var blockerStarted = new ManualResetEventSlim();
        using var lastRan = new ManualResetEventSlim();
        var skippedRan = false;

        scheduler.Start(token =>
        {
            blockerStarted.Set();
            _ = token.WaitHandle.WaitOne();
            Thread.Sleep(20);
        });
        Assert.True(blockerStarted.Wait(Timeout));
        scheduler.Start(_ => Volatile.Write(ref skippedRan, true));
        scheduler.Start(_ => lastRan.Set());

        Assert.True(lastRan.Wait(Timeout));
        Assert.False(Volatile.Read(ref skippedRan));
    }

    [Fact]
    public void AFailingLoop_DoesNotStopLaterRuns()
    {
        using var scheduler = new PlaybackScheduler("Test");
        using var recovered = new ManualResetEventSlim();

        scheduler.Start(_ => throw new InvalidOperationException("boom"));
        scheduler.StopAndWait();
        scheduler.Start(_ => recovered.Set());

        Assert.True(recovered.Wait(Timeout));
    }

    [Fact]
    public void AnIdleWorkerExits_AndTheNextStartCreatesANewOne()
    {
        using var scheduler = new PlaybackScheduler("Test", idleTimeout: TimeSpan.FromMilliseconds(50));
        using var first = new ManualResetEventSlim();
        using var second = new ManualResetEventSlim();

        scheduler.Start(_ => first.Set());
        Assert.True(first.Wait(Timeout));
        Assert.True(SpinWait.SpinUntil(() => !scheduler.HasWorker, Timeout), "The idle worker did not exit.");
        scheduler.Start(_ => second.Set());

        Assert.True(second.Wait(Timeout));
        Assert.Equal(2, scheduler.WorkerThreadsCreated);
    }

    [Fact]
    public void ALoopThatIgnoresCancellation_DoesNotBlockLaterStarts()
    {
        // A native call that ignores the token: later starts get a new worker instead of
        // queueing behind it forever.
        using var scheduler = new PlaybackScheduler("Test");
        using var stuckStarted = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var nextRan = new ManualResetEventSlim();

        scheduler.Start(_ =>
        {
            stuckStarted.Set();
            release.Wait(CancellationToken.None); // ignores cancellation
        });
        Assert.True(stuckStarted.Wait(Timeout));
        // Not stopped: what the loop uses must not be freed (the background video decoder).
        Assert.False(scheduler.StopAndWait(TimeSpan.FromMilliseconds(100)));
        scheduler.Start(_ => nextRan.Set());

        Assert.True(nextRan.Wait(Timeout));
        Assert.Equal(2, scheduler.WorkerThreadsCreated);
        release.Set();
    }

    [Fact]
    public void Dispose_StopsTheLoop_AndRejectsNewStarts()
    {
        var scheduler = new PlaybackScheduler("Test");
        using var started = new ManualResetEventSlim();
        var returned = false;
        scheduler.Start(token =>
        {
            started.Set();
            _ = token.WaitHandle.WaitOne();
            Volatile.Write(ref returned, true);
        });
        Assert.True(started.Wait(Timeout));

        scheduler.Dispose();

        Assert.True(Volatile.Read(ref returned));
        _ = Assert.Throws<ObjectDisposedException>(() => scheduler.Start(_ => { }));
    }

    static void InterlockedMax(ref int target, int value)
    {
        var current = Volatile.Read(ref target);
        while (value > current)
        {
            var observed = Interlocked.CompareExchange(ref target, value, current);
            if (observed == current)
                return;
            current = observed;
        }
    }
}
