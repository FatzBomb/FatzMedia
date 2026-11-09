using System.Collections.Concurrent;
using FatzMedia.Video.Core.Threading;

namespace FatzMedia.Video.Tests;

/// <summary>ENG-024 follow-up: the player's diagnostic delivery worker.</summary>
public sealed class SerialEventDispatcherTests
{
    [Fact]
    public async Task Deliveries_RunInPostedOrder_AndAThrowingOneDoesNotStopTheRest()
    {
        using var dispatcher = new SerialEventDispatcher("test", TimeSpan.FromSeconds(1));
        var delivered = new ConcurrentQueue<int>();
        for (var index = 0; index < 200; index++)
        {
            var value = index;
            Assert.True(dispatcher.Post(() =>
            {
                delivered.Enqueue(value);
                if (value == 50)
                    throw new InvalidOperationException("handler bug");
            }));
        }

        await dispatcher.FlushAsync().WaitAsync(TestEnvironment.EventuallyTimeout);

        Assert.Equal(Enumerable.Range(0, 200), delivered);
    }

    [Fact]
    public async Task ABlockedDelivery_DoesNotBlockThePoster()
    {
        using var release = new ManualResetEventSlim();
        using var dispatcher = new SerialEventDispatcher("test", TimeSpan.FromSeconds(1));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(dispatcher.Post(() =>
        {
            entered.SetResult();
            release.Wait();
        }));
        await entered.Task.WaitAsync(TestEnvironment.EventuallyTimeout);

        Assert.True(dispatcher.Post(static () => { }));
        var flushed = dispatcher.FlushAsync();
        Assert.False(flushed.IsCompleted);

        release.Set();
        await flushed.WaitAsync(TestEnvironment.EventuallyTimeout);
    }

    [Fact]
    public async Task Dispose_DeliversWhatWasQueued_ThenNothing()
    {
        var dispatcher = new SerialEventDispatcher("test", TimeSpan.FromSeconds(5));
        var delivered = new ConcurrentQueue<int>();
        for (var index = 0; index < 20; index++)
        {
            var value = index;
            Assert.True(dispatcher.Post(() =>
            {
                Thread.Sleep(2);
                delivered.Enqueue(value);
            }));
        }

        dispatcher.Dispose();
        Assert.Equal(Enumerable.Range(0, 20), delivered);

        Assert.False(dispatcher.Post(() => delivered.Enqueue(-1)));
        await dispatcher.FlushAsync().WaitAsync(TestEnvironment.EventuallyTimeout);
        await Task.Delay(50);
        Assert.Equal(20, delivered.Count);
    }

    [Fact]
    public async Task Dispose_PastItsBound_DropsTheRest()
    {
        using var release = new ManualResetEventSlim();
        var dispatcher = new SerialEventDispatcher("test", TimeSpan.FromMilliseconds(100));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var later = 0;
        Assert.True(dispatcher.Post(() =>
        {
            entered.SetResult();
            release.Wait();
        }));
        Assert.True(dispatcher.Post(() => Interlocked.Increment(ref later)));
        await entered.Task.WaitAsync(TestEnvironment.EventuallyTimeout);

        dispatcher.Dispose();
        release.Set();
        await Task.Delay(100);

        Assert.Equal(0, Volatile.Read(ref later));
    }

    [Fact]
    public async Task Dispose_FromADelivery_DoesNotWaitForItself()
    {
        var dispatcher = new SerialEventDispatcher("test", TimeSpan.FromSeconds(30));
        var disposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var later = 0;
        Assert.True(dispatcher.Post(() =>
        {
            dispatcher.Dispose();
            disposed.SetResult();
        }));
        _ = dispatcher.Post(() => Interlocked.Increment(ref later));

        await disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(50);
        Assert.Equal(0, Volatile.Read(ref later));
    }
}
