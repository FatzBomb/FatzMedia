using System.Collections.Concurrent;
using System.Diagnostics;
using FatzMedia.Video.Core.Configuration;
using FatzMedia.Video.Core.Threading;
using static FatzMedia.Video.Tests.PlayerModeSwitchTests;

namespace FatzMedia.Video.Tests;

/// <summary>ENG-024: the player's threading contract (admission, cancellation, bounded lifecycle waits).</summary>
public sealed class PlayerThreadingContractTests
{
    /// <summary>How long a call that must not block may take; far below the blocked operation.</summary>
    static readonly TimeSpan PromptReturn = TimeSpan.FromSeconds(2);

    [Fact]
    public async Task AsyncCalls_DuringASlowSynchronousLoad_ReturnWithoutWaiting()
    {
        using var releaseOpen = new ManualResetEventSlim();
        var openEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new SwitchTestPlatformFactory
        {
            ConfigureEngine = (_, engine) => engine.BeforeOpen = () =>
            {
                if (openEntered.TrySetResult())
                    releaseOpen.Wait();
            }
        };
        using var player = new PlayerCore(factory, MediaPlayerConfiguration.Auto, []);
        var syncLoad = Task.Run(() => player.Load("slow.mp3"));
        await openEntered.Task.WaitAsync(TestEnvironment.EventuallyTimeout);

        // Each call only queues: it returns while the synchronous load still holds the queue.
        var stopwatch = Stopwatch.StartNew();
        (var play, var seek, var load) = await Task.Run(() =>
            (player.PlayAsync(), player.SeekAsync(5), player.LoadAsync("next.mp3"))).WaitAsync(PromptReturn);
        Assert.True(stopwatch.Elapsed < PromptReturn);
        Assert.False(play.IsCompleted || seek.IsCompleted || load.IsCompleted);

        releaseOpen.Set();
        Assert.True(await syncLoad.WaitAsync(TestEnvironment.EventuallyTimeout));
        Assert.True(await play.WaitAsync(TestEnvironment.EventuallyTimeout));
        Assert.True(await seek.WaitAsync(TestEnvironment.EventuallyTimeout));
        Assert.True(await load.WaitAsync(TestEnvironment.EventuallyTimeout));
    }

    [Fact]
    public async Task AsyncCalls_DuringASlowReset_FailAtOnceInsteadOfWaiting()
    {
        using var releaseRebuild = new ManualResetEventSlim();
        var rebuildEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new SwitchTestPlatformFactory
        {
            BeforeCreateServiceProvider = count =>
            {
                if (count == 2)
                {
                    rebuildEntered.SetResult();
                    releaseRebuild.Wait();
                }
            }
        };
        using var player = new PlayerCore(factory, MediaPlayerConfiguration.Auto, []);
        var reset = Task.Run(() => player.Reset(MediaPlayerConfiguration.Auto, []));
        await rebuildEntered.Task.WaitAsync(TestEnvironment.EventuallyTimeout);

        var refused = await Task.Run(() => Record.Exception(() => { _ = player.PlayAsync(); })).WaitAsync(PromptReturn);
        Assert.IsType<InvalidOperationException>(refused);

        releaseRebuild.Set();
        Assert.True(await reset.WaitAsync(TestEnvironment.EventuallyTimeout));
        Assert.True(await player.LoadAsync("song.mp3").WaitAsync(TestEnvironment.EventuallyTimeout));
    }

    [Fact]
    public async Task Dispose_DuringASlowLoad_CancelsTheLoadAndReturnsWithinTheBound()
    {
        var openEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new SwitchTestPlatformFactory
        {
            // Like a network open waiting between retries: it ends when the load is cancelled.
            ConfigureEngine = (_, engine) => engine.BeforeOpen = () =>
            {
                openEntered.TrySetResult();
                MediaOpenCancellation.Current.WaitHandle.WaitOne(TestEnvironment.EventuallyTimeout);
                if (MediaOpenCancellation.Current.IsCancellationRequested)
                    engine.OpenSucceeds = false;
            }
        };
        var player = new PlayerCore(factory, MediaPlayerConfiguration.Auto, []);
        var load = player.LoadAsync("stream.mp3");
        await openEntered.Task.WaitAsync(TestEnvironment.EventuallyTimeout);

        var stopwatch = Stopwatch.StartNew();
        await Task.Run(player.Dispose).WaitAsync(TestEnvironment.EventuallyTimeout);

        Assert.True(stopwatch.Elapsed < player.DisposeWaitTimeout, $"Dispose took {stopwatch.Elapsed}.");
        Assert.False(await load.WaitAsync(TestEnvironment.EventuallyTimeout));
        var engine = Assert.Single(factory.Engines);
        Assert.Equal(1, engine.DisposeCount);
        Assert.False(engine.IsOpen);
        Assert.Throws<ObjectDisposedException>(() => player.Play());
    }

    [Fact]
    public async Task Dispose_DuringAnUncancellableOpen_ReturnsAfterTheBound_AndTearsDownWhenTheOpenEnds()
    {
        using var releaseOpen = new ManualResetEventSlim();
        var openEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new SwitchTestPlatformFactory
        {
            // Like a native connect attempt in progress: it does not see the cancellation.
            ConfigureEngine = (_, engine) => engine.BeforeOpen = () =>
            {
                openEntered.TrySetResult();
                releaseOpen.Wait();
            }
        };
        var player = new PlayerCore(factory, MediaPlayerConfiguration.Auto, [])
        {
            DisposeWaitTimeout = TimeSpan.FromMilliseconds(200)
        };
        Assert.True(player.AttachPresentationSurface(Surface("primary", 640, 360)));
        var coordinator = Assert.Single(factory.Coordinators);
        var load = player.LoadAsync("stream.mp3");
        await openEntered.Task.WaitAsync(TestEnvironment.EventuallyTimeout);

        await Task.Run(player.Dispose).WaitAsync(TestEnvironment.EventuallyTimeout);

        // Returned without the teardown: calls are refused and the host's surfaces are released.
        var engine = Assert.Single(factory.Engines);
        Assert.Equal(0, engine.DisposeCount);
        Assert.Throws<ObjectDisposedException>(() => player.Play());
        Assert.Throws<ObjectDisposedException>(() => { _ = player.PlayAsync(); });
        Assert.Empty(coordinator.Surfaces);
        Assert.False(player.AttachPresentationSurface(Surface("late", 320, 240)));

        releaseOpen.Set();
        Assert.False(await load.WaitAsync(TestEnvironment.EventuallyTimeout));
        await WaitUntilAsync(() => engine.DisposeCount == 1);
        Assert.True(coordinator.Disposed);
        Assert.False(engine.IsOpen);
        player.Dispose();
        Assert.Equal(1, engine.DisposeCount);
    }

    [Fact]
    public async Task LoadAsync_CancelledWhileQueued_IsSkippedAndChangesNothing()
    {
        using var releaseOpen = new ManualResetEventSlim();
        var openEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var opens = new ConcurrentQueue<string>();
        var factory = new SwitchTestPlatformFactory
        {
            ConfigureEngine = (_, engine) => engine.BeforeOpen = () =>
            {
                opens.Enqueue("open");
                if (openEntered.TrySetResult())
                    releaseOpen.Wait();
            }
        };
        using var player = new PlayerCore(factory, MediaPlayerConfiguration.Auto, []);
        var first = player.LoadAsync("first.mp3");
        await openEntered.Task.WaitAsync(TestEnvironment.EventuallyTimeout);
        using var cancellation = new CancellationTokenSource();
        var second = player.LoadAsync("second.mp3", cancellation.Token);

        cancellation.Cancel();
        releaseOpen.Set();

        Assert.True(await first.WaitAsync(TestEnvironment.EventuallyTimeout));
        var cancelled = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => second.WaitAsync(TestEnvironment.EventuallyTimeout));
        Assert.Equal(cancellation.Token, cancelled.CancellationToken);
        Assert.True(second.IsCanceled);
        Assert.Single(opens);
        Assert.True(player.IsOpen);
    }

    [Fact]
    public async Task LoadAsync_CancelledDuringTheOpen_EndsClosedAndStaysUsable()
    {
        var openEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waitForCancellation = false;
        var factory = new SwitchTestPlatformFactory
        {
            ConfigureEngine = (_, engine) => engine.BeforeOpen = () =>
            {
                if (!Volatile.Read(ref waitForCancellation))
                    return;

                openEntered.TrySetResult();
                // A connect that finishes but whose load was cancelled meanwhile.
                MediaOpenCancellation.Current.WaitHandle.WaitOne(TestEnvironment.EventuallyTimeout);
            }
        };
        using var player = new PlayerCore(factory, MediaPlayerConfiguration.Auto, []);
        var loaded = new ConcurrentQueue<string?>();
        var closed = new ConcurrentQueue<string?>();
        player.MediaLoaded += (_, e) => loaded.Enqueue(e.Source.FilePath);
        player.MediaClosed += (_, e) => closed.Enqueue(e.Source.FilePath);
        Assert.True(await player.LoadAsync("first.mp3").WaitAsync(TestEnvironment.EventuallyTimeout));
        Volatile.Write(ref waitForCancellation, true);
        using var cancellation = new CancellationTokenSource();

        var second = player.LoadAsync(MediaInputSource.FromFile("second.mp3"), null, ignoreSidecar: false, cancellation.Token);
        await openEntered.Task.WaitAsync(TestEnvironment.EventuallyTimeout);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second.WaitAsync(TestEnvironment.EventuallyTimeout));
        Assert.False(player.IsOpen);
        Assert.Equal(["first.mp3"], loaded);
        Assert.Equal(["first.mp3"], closed);

        Volatile.Write(ref waitForCancellation, false);
        Assert.True(await player.LoadAsync("third.mp3", CancellationToken.None).WaitAsync(TestEnvironment.EventuallyTimeout));
        Assert.True(player.IsOpen);
    }

    [Fact]
    public async Task LoadAsync_WithACancelledToken_IsCancelledWithoutQueueing()
    {
        var factory = new SwitchTestPlatformFactory();
        using var player = new PlayerCore(factory, MediaPlayerConfiguration.Auto, []);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var load = player.LoadAsync("song.mp3", cancellation.Token);

        Assert.True(load.IsCanceled);
        await player.FlushControlOperationsAsync();
        Assert.False(player.IsOpen);
    }

    [Fact]
    public async Task Reset_DuringAnUncancellableOpen_GivesUpAfterTheBound_AndLeavesThePlayerUsable()
    {
        using var releaseOpen = new ManualResetEventSlim();
        var openEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var block = true;
        var factory = new SwitchTestPlatformFactory
        {
            ConfigureEngine = (_, engine) => engine.BeforeOpen = () =>
            {
                if (!Volatile.Read(ref block))
                    return;

                openEntered.TrySetResult();
                releaseOpen.Wait();
            }
        };
        using var player = new PlayerCore(factory, MediaPlayerConfiguration.Auto, [])
        {
            ResetWaitTimeout = TimeSpan.FromMilliseconds(200)
        };
        var load = player.LoadAsync("stream.mp3");
        await openEntered.Task.WaitAsync(TestEnvironment.EventuallyTimeout);

        Assert.False(await Task.Run(() => player.Reset(MediaPlayerConfiguration.Auto, [])).WaitAsync(TestEnvironment.EventuallyTimeout));

        Volatile.Write(ref block, false);
        releaseOpen.Set();
        // The in-flight load was cancelled; nothing was rebuilt and later calls work.
        Assert.False(await load.WaitAsync(TestEnvironment.EventuallyTimeout));
        Assert.Single(factory.Engines);
        Assert.True(await player.LoadAsync("song.mp3").WaitAsync(TestEnvironment.EventuallyTimeout));
        Assert.True(player.Play());
        Assert.True(player.Reset(MediaPlayerConfiguration.Auto, []));
        Assert.Equal(2, factory.Engines.Count);
    }

    [Fact]
    public async Task FailedReset_IsClearlyFaulted_ThenRebuildableAndDisposable()
    {
        var factory = new SwitchTestPlatformFactory
        {
            BeforeCreateServiceProvider = count =>
            {
                if (count == 2)
                    throw new InvalidOperationException("Audio device unavailable.");
            }
        };
        var player = new PlayerCore(factory, MediaPlayerConfiguration.Auto, []);
        Assert.True(player.AttachPresentationSurface(Surface("primary", 640, 360)));

        _ = Assert.Throws<InvalidOperationException>(() => player.Reset(MediaPlayerConfiguration.Auto, []));

        // Faulted: control calls are refused, surface calls keep the surfaces for the next graph.
        Assert.Throws<ObjectDisposedException>(() => player.Play());
        _ = await Assert.ThrowsAsync<ObjectDisposedException>(() => player.PlayAsync());
        Assert.True(player.ResizePresentationSurface("primary", 1280, 720));

        Assert.True(player.Reset(MediaPlayerConfiguration.Auto, []));
        Assert.True(player.Load("song.mp3"));
        var rebuilt = factory.Coordinators[^1];
        Assert.Equal(1280, rebuilt.Surfaces["primary"].PixelWidth);

        player.Dispose();
        Assert.Equal(1, factory.Engines[^1].DisposeCount);
    }

    [Fact]
    public async Task FailedReset_CanBeDisposedWithoutARetry()
    {
        var factory = new SwitchTestPlatformFactory
        {
            BeforeCreateServiceProvider = count =>
            {
                if (count == 2)
                    throw new InvalidOperationException("Audio device unavailable.");
            }
        };
        var player = new PlayerCore(factory, MediaPlayerConfiguration.Auto, []);
        _ = Assert.Throws<InvalidOperationException>(() => player.Reset(MediaPlayerConfiguration.Auto, []));

        await Task.Run(player.Dispose).WaitAsync(TestEnvironment.EventuallyTimeout);

        Assert.Equal(1, factory.Engines[0].DisposeCount);
        Assert.Throws<ObjectDisposedException>(() => player.Play());
    }

    [Fact]
    public async Task Reset_RaisesChangeEventsForTheSettingsItReturnedToDefaults()
    {
        var factory = new SwitchTestPlatformFactory();
        using var player = new PlayerCore(factory, MediaPlayerConfiguration.Auto, []);
        player.Effects.Volume = 0.5;
        player.Effects.Pitch = 2;
        player.LoopMedia = true;
        var volume = new TaskCompletionSource<MediaValueChangedEventArgs<double>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pitch = new TaskCompletionSource<MediaValueChangedEventArgs<int>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var loop = new TaskCompletionSource<MediaValueChangedEventArgs<bool>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var tempoChanges = 0;
        player.VolumeChanged += (_, e) => volume.TrySetResult(e);
        player.PitchChanged += (_, e) => pitch.TrySetResult(e);
        player.LoopMediaChanged += (_, e) => loop.TrySetResult(e);
        player.TempoChanged += (_, _) => Interlocked.Increment(ref tempoChanges);

        Assert.True(player.Reset(MediaPlayerConfiguration.Auto, []));

        var volumeChange = await volume.Task.WaitAsync(TestEnvironment.EventuallyTimeout);
        Assert.Equal((0.5, player.Effects.Volume, MediaChangeReason.PlayerReset), (volumeChange.PreviousValue, volumeChange.CurrentValue, volumeChange.Reason));
        var pitchChange = await pitch.Task.WaitAsync(TestEnvironment.EventuallyTimeout);
        Assert.Equal((2, 0), (pitchChange.PreviousValue, pitchChange.CurrentValue));
        var loopChange = await loop.Task.WaitAsync(TestEnvironment.EventuallyTimeout);
        Assert.Equal((true, false), (loopChange.PreviousValue, loopChange.CurrentValue));
        await player.FlushControlOperationsAsync();
        Assert.Equal(0, tempoChanges);
    }

    [Fact]
    public async Task SurfaceCalls_RacingResetsAndModeSwitches_EndAttachedToTheLastGraph()
    {
        var factory = new SwitchTestPlatformFactory();
        using var player = new PlayerCore(factory, MediaPlayerConfiguration.Auto, []);
        Assert.True(player.Load("song.mp3"));
        using var stop = new CancellationTokenSource();
        string[] ids = ["s0", "s1", "s2", "s3"];

        var hammer = Task.Run(() =>
        {
            var random = new Random(24);
            while (!stop.IsCancellationRequested)
            {
                var id = ids[random.Next(ids.Length)];
                _ = random.Next(4) switch
                {
                    0 => player.AttachPresentationSurface(Surface(id, 100 + random.Next(100), 100)),
                    1 => player.ResizePresentationSurface(id, 200 + random.Next(100), 200),
                    2 => player.SetPresentationSurfaceLiveResize(id, random.Next(2) == 0),
                    _ => player.DetachPresentationSurface(id),
                };
            }
        });

        for (var i = 0; i < 4; i++)
        {
            Assert.True(await player.SetModeAsync(MediaPlayerMode.Audio).WaitAsync(TestEnvironment.EventuallyTimeout));
            Assert.True(await player.SetModeAsync(MediaPlayerMode.Full).WaitAsync(TestEnvironment.EventuallyTimeout));
            Assert.True(await Task.Run(() => player.Reset(MediaPlayerConfiguration.Auto, [])).WaitAsync(TestEnvironment.EventuallyTimeout));
        }

        stop.Cancel();
        await hammer.WaitAsync(TestEnvironment.EventuallyTimeout);
        for (var i = 0; i < ids.Length; i++)
        {
            Assert.True(player.AttachPresentationSurface(Surface(ids[i], 640, 360)));
            Assert.True(player.ResizePresentationSurface(ids[i], 800 + i, 600));
        }

        // Every earlier graph was freed and the last one has exactly the host's surfaces.
        var last = factory.Coordinators[^1];
        Assert.All(factory.Coordinators.SkipLast(1), coordinator => Assert.True(coordinator.Disposed));
        Assert.Equal(ids, last.Surfaces.Keys.Order(StringComparer.Ordinal));
        for (var i = 0; i < ids.Length; i++)
            Assert.Equal(800 + i, last.Surfaces[ids[i]].PixelWidth);
    }

    [Fact]
    public async Task Dispose_FromATaskAHandlerStarted_RunsOutsideTheOperation()
    {
        var factory = new SwitchTestPlatformFactory();
        var player = new PlayerCore(factory, MediaPlayerConfiguration.Auto, []);
        var disposeResult = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handlerReturned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        player.MediaLoaded += (_, _) =>
        {
            // Like a host marshalling the close to its dispatcher: the callback captured this
            // handler's context and runs once the handler returned.
            _ = Task.Run(async () =>
            {
                await handlerReturned.Task.ConfigureAwait(false);
                disposeResult.SetResult(Record.Exception(player.Dispose));
            });
        };

        player.MediaLoaded += (_, _) => handlerReturned.TrySetResult();
        var load = player.LoadAsync("song.mp3");

        Assert.True(await load.WaitAsync(TestEnvironment.EventuallyTimeout));
        Assert.Null(await disposeResult.Task.WaitAsync(TestEnvironment.EventuallyTimeout));
        Assert.Equal(1, Assert.Single(factory.Engines).DisposeCount);
    }

    [Fact]
    public async Task MediaDiagnostic_RaisedDuringALongOperation_ReachesTheHostBeforeItEnds_InOrder()
    {
        const string Category = "ThreadingTest.Retry";
        PlayerCore? owner = null;
        var received = new ConcurrentQueue<string>();
        using var allReceived = new CountdownEvent(3);
        var deliveredDuringOpen = false;
        var factory = new SwitchTestPlatformFactory
        {
            // Like the network open's retries: progress diagnostics while the load still runs.
            ConfigureEngine = (_, engine) => engine.BeforeOpen = () =>
            {
                using (DiagnosticRouter.EnterOwner(owner!))
                {
                    for (var attempt = 1; attempt <= 3; attempt++)
                        DiagnosticRouter.Raise(MediaDiagnosticLevel.Warning, Category, $"retry {attempt}/3");
                }

                deliveredDuringOpen = allReceived.Wait(TestEnvironment.EventuallyTimeout);
            }
        };
        using var player = new PlayerCore(factory, MediaPlayerConfiguration.Auto, []);
        owner = player;
        player.MediaDiagnostic += (_, e) =>
        {
            if (e.Category != Category)
                return;
            received.Enqueue(e.Message);
            allReceived.Signal();
        };

        Assert.True(await player.LoadAsync("stream.mp3").WaitAsync(TestEnvironment.EventuallyTimeout));

        Assert.True(deliveredDuringOpen, "the diagnostics waited for the load to end");
        Assert.Equal(["retry 1/3", "retry 2/3", "retry 3/3"], received);
    }

    [Fact]
    public async Task MediaDiagnostic_IsNotDeliveredAfterDispose()
    {
        const string Category = "ThreadingTest.AfterDispose";
        var player = new PlayerCore(new SwitchTestPlatformFactory(), MediaPlayerConfiguration.Auto, []);
        var received = new ConcurrentQueue<string>();
        player.MediaDiagnostic += (_, e) =>
        {
            if (e.Category == Category)
                received.Enqueue(e.Message);
        };
        using (DiagnosticRouter.EnterOwner(player))
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Info, Category, "before");

        player.Dispose();
        // Queued before Dispose: delivered by the time it returned.
        Assert.Equal(["before"], received);

        using (DiagnosticRouter.EnterOwner(player))
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Info, Category, "after");
        await Task.Delay(100);
        Assert.Equal(["before"], received);
    }

    [Fact]
    public async Task HandlerExceptions_DoNotStopOtherHandlers_AndAreReported()
    {
        var factory = new SwitchTestPlatformFactory();
        using var player = new PlayerCore(factory, MediaPlayerConfiguration.Auto, []);
        var secondRan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reported = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        // Only this test's report: a handler failure raised without an owner (another test class,
        // in parallel) is process-wide and reaches every player.
        player.MediaDiagnostic += (_, e) =>
        {
            if (e.Category == "HostEvent" && e.Message.StartsWith("MediaStopped", StringComparison.Ordinal))
                reported.TrySetResult(e.Message);
        };
        player.MediaStopped += (_, _) => throw new InvalidOperationException("host bug");
        player.MediaStopped += (_, _) => secondRan.TrySetResult();
        Assert.True(player.Load("song.mp3"));
        Assert.True(player.Play());

        Assert.True(await player.StopAsync().WaitAsync(TestEnvironment.EventuallyTimeout));

        await secondRan.Task.WaitAsync(TestEnvironment.EventuallyTimeout);
        Assert.Contains("MediaStopped handler failed: host bug", await reported.Task.WaitAsync(TestEnvironment.EventuallyTimeout));
    }

    static async Task WaitUntilAsync(Func<bool> condition)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(stopwatch.Elapsed < TestEnvironment.EventuallyTimeout, "Condition not met in time.");
            await Task.Delay(10);
        }
    }
}
