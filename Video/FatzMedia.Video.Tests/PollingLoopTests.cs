using FatzMedia.Video.Core.Audio;

namespace FatzMedia.Video.Tests;

public class PollingLoopTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public void Start_RunsTickRepeatedlyUntilStopped()
    {
        using var ticks = new SemaphoreSlim(0);
        using var loop = new PollingLoop("test", TimeSpan.FromMilliseconds(5), () => ticks.Release());

        loop.Start();

        Assert.True(loop.IsRunning);
        Assert.True(ticks.Wait(Timeout));
        Assert.True(ticks.Wait(Timeout));
        loop.Stop();
        Assert.False(loop.IsRunning);
    }

    [Fact]
    public void Stop_EndsAThreadWaitingOutALongInterval()
    {
        using var first = new ManualResetEventSlim();
        var loop = new PollingLoop("test", TimeSpan.FromMinutes(5), first.Set);
        loop.Start();
        Assert.True(first.Wait(Timeout));

        var started = DateTime.UtcNow;
        loop.Stop();

        Assert.False(loop.IsRunning);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Start_WhileRunning_DoesNotRunBeforeStartAgain()
    {
        var before = 0;
        using var loop = new PollingLoop("test", TimeSpan.FromMilliseconds(5), () => { });

        loop.Start(() => before++);
        loop.Start(() => before++);

        Assert.Equal(1, before);
    }

    [Fact]
    public void BeforeStart_RunsBeforeTheFirstTick()
    {
        var order = new List<string>();
        using var done = new ManualResetEventSlim();
        using var loop = new PollingLoop("test", TimeSpan.FromMilliseconds(5), () =>
        {
            lock (order)
                order.Add("tick");
            done.Set();
        });

        loop.Start(() =>
        {
            lock (order)
                order.Add("before");
        });

        Assert.True(done.Wait(Timeout));
        loop.Stop();
        lock (order)
            Assert.Equal("before", order[0]);
    }

    [Fact]
    public void ThrowingTick_ReportsTheErrorAndKeepsPolling()
    {
        var calls = 0;
        var errors = new List<Exception>();
        using var twice = new ManualResetEventSlim();
        using var loop = new PollingLoop(
            "test",
            TimeSpan.FromMilliseconds(5),
            () =>
            {
                if (Interlocked.Increment(ref calls) >= 2)
                    twice.Set();
                throw new InvalidOperationException("boom");
            },
            exception =>
            {
                lock (errors)
                    errors.Add(exception);
            });

        loop.Start();

        Assert.True(twice.Wait(Timeout));
        loop.Stop();
        lock (errors)
        {
            Assert.NotEmpty(errors);
            Assert.All(errors, e => Assert.Equal("boom", e.Message));
        }
    }

    [Fact]
    public void ThrowingTick_WithoutHandlerIsSwallowed()
    {
        using var ticks = new SemaphoreSlim(0);
        using var loop = new PollingLoop("test", TimeSpan.FromMilliseconds(5), () =>
        {
            _ = ticks.Release();
            throw new InvalidOperationException("boom");
        });

        loop.Start();

        Assert.True(ticks.Wait(Timeout));
        Assert.True(ticks.Wait(Timeout));
    }

    [Fact]
    public void CanRestartAfterStop()
    {
        using var ticks = new SemaphoreSlim(0);
        using var loop = new PollingLoop("test", TimeSpan.FromMilliseconds(5), () => ticks.Release());

        loop.Start();
        Assert.True(ticks.Wait(Timeout));
        loop.Stop();
        while (ticks.Wait(0))
        {
        }

        loop.Start();

        Assert.True(ticks.Wait(Timeout));
        Assert.True(loop.IsRunning);
    }

    [Fact]
    public void Stop_FromTheCallback_ReturnsWithoutWaitingOutTheJoin()
    {
        using var stopped = new ManualResetEventSlim();
        var elapsed = TimeSpan.MaxValue;
        PollingLoop? loop = null;
        loop = new PollingLoop("test", TimeSpan.FromSeconds(5), () =>
        {
            var started = DateTime.UtcNow;
            loop!.Stop();
            elapsed = DateTime.UtcNow - started;
            stopped.Set();
        });

        loop.Start();

        Assert.True(stopped.Wait(Timeout));
        Assert.True(elapsed < TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void Stop_WithoutStart_IsANoOp()
    {
        var loop = new PollingLoop("test", TimeSpan.FromMilliseconds(5), () => { });

        loop.Stop();
        loop.Dispose();

        Assert.False(loop.IsRunning);
    }
}
