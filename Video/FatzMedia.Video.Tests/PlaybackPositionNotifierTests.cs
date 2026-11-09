using FatzMedia.Video.Core.Threading;

namespace FatzMedia.Video.Tests;

public sealed class PlaybackPositionNotifierTests
{
    [Fact]
    public void PublishNow_RaisesCurrentPositionAndDuration()
    {
        using var notifier = new PlaybackPositionNotifier(() => 12.5, () => 90);
        MediaPositionChangedEventArgs? received = null;
        notifier.PositionChanged += (_, e) => received = e;

        notifier.PublishNow();

        Assert.Equal(TimeSpan.FromSeconds(12.5), received!.Position);
        Assert.Equal(TimeSpan.FromSeconds(90), received.Duration);
    }

    [Fact]
    public async Task StopFromPositionHandler_PreventsFurtherPeriodicEvents()
    {
        using var notifier = new PlaybackPositionNotifier(
            () => 1,
            () => 10,
            interval: TimeSpan.FromMilliseconds(20));
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        notifier.PositionChanged += (_, _) =>
        {
            Interlocked.Increment(ref count);
            notifier.Stop();
            first.TrySetResult();
        };

        notifier.Start();
        await first.Task.WaitAsync(TestEnvironment.EventuallyTimeout);
        await Task.Delay(100);

        Assert.Equal(1, Volatile.Read(ref count));
    }

    [Fact]
    public void StartStopStorm_FaultsNoRun()
    {
        // A Stop() right after Start() runs before the pump's task starts; the task must not read
        // the cleared cancellation source (it faulted with a NullReferenceException).
        var faults = 0;
        void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            if (e.Exception.InnerExceptions.Any(static ex => ex.StackTrace?.Contains(nameof(PlaybackPositionNotifier), StringComparison.Ordinal) == true))
                Interlocked.Increment(ref faults);
        }

        TaskScheduler.UnobservedTaskException += OnUnobserved;
        try
        {
            using (var notifier = new PlaybackPositionNotifier(() => 1, () => 10, interval: TimeSpan.FromMilliseconds(5)))
            {
                for (var i = 0; i < 500; i++)
                {
                    notifier.Start();
                    notifier.Stop();
                }
            }

            Thread.Sleep(200);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= OnUnobserved;
        }

        Assert.Equal(0, Volatile.Read(ref faults));
    }

    [Fact]
    public async Task DisposeFromPositionHandler_DoesNotBlockPump()
    {
        var notifier = new PlaybackPositionNotifier(
            () => 1,
            () => 10,
            interval: TimeSpan.FromMilliseconds(20));
        var disposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        notifier.PositionChanged += (_, _) =>
        {
            notifier.Dispose();
            disposed.TrySetResult();
        };

        notifier.Start();

        await disposed.Task.WaitAsync(TestEnvironment.EventuallyTimeout);
    }

    [Fact]
    public async Task RestartAfterStop_UsesNewPumpGeneration()
    {
        using var notifier = new PlaybackPositionNotifier(
            () => 1,
            () => 10,
            interval: TimeSpan.FromMilliseconds(20));
        var secondGeneration = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        notifier.PositionChanged += (_, _) =>
        {
            if (Interlocked.Increment(ref count) == 1)
            {
                notifier.Stop();
                notifier.Start();
                return;
            }

            notifier.Stop();
            secondGeneration.TrySetResult();
        };

        notifier.Start();

        await secondGeneration.Task.WaitAsync(TestEnvironment.EventuallyTimeout);
        Assert.Equal(2, Volatile.Read(ref count));
    }

    [Fact]
    public void PositionHandlerException_DoesNotBlockLaterHandlers()
    {
        using var notifier = new PlaybackPositionNotifier(() => 1, () => 10);
        var secondHandlerRan = false;
        notifier.PositionChanged += (_, _) => throw new InvalidOperationException("host failure");
        notifier.PositionChanged += (_, _) => secondHandlerRan = true;

        notifier.PublishNow();

        Assert.True(secondHandlerRan);
    }

    [Fact]
    public void DefaultInterval_Is250Milliseconds() =>
        Assert.Equal(TimeSpan.FromMilliseconds(250), PlaybackPositionNotifier.DefaultInterval);
}
