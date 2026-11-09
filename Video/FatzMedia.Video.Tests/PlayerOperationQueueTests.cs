using FatzMedia.Video.Core.Threading;

namespace FatzMedia.Video.Tests;

public class PlayerOperationQueueTests
{
    [Fact]
    public async Task Enqueue_RunsOperationsInFifoOrder()
    {
        using var queue = new PlayerOperationQueue("test");
        var order = new List<string>();

        Task<bool> first = queue.Enqueue(() => { order.Add("first"); return true; });
        Task<bool> second = queue.Enqueue(() => { order.Add("second"); return true; });
        Task<bool> third = queue.Enqueue(() => { order.Add("third"); return true; });

        await Task.WhenAll(first, second, third);

        Assert.Equal(["first", "second", "third"], order);
    }

    [Fact]
    public async Task Execute_BlocksUntilQueuedOperationCompletes_AndReturnsResult()
    {
        using var queue = new PlayerOperationQueue("test");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<int> queued = queue.Enqueue(async () =>
        {
            started.SetResult();
            await release.Task.ConfigureAwait(false);
            return 1;
        });

        await started.Task.WaitAsync(TestEnvironment.EventuallyTimeout);

        var executeRan = new ManualResetEventSlim(false);
        Task<int> executeTask = Task.Run(() => queue.Execute(() => { executeRan.Set(); return 42; }));

        // The queued operation is still blocked, so Execute must not run yet.
        await Task.Delay(100);
        Assert.False(executeRan.IsSet);
        Assert.False(executeTask.IsCompleted);

        release.SetResult();

        Assert.Equal(42, await executeTask.WaitAsync(TestEnvironment.EventuallyTimeout));
        Assert.Equal(1, await queued.WaitAsync(TestEnvironment.EventuallyTimeout));
    }

    [Fact]
    public async Task Execute_WhenCalledFromWorkerContext_RunsDirectlyOnTheWorkerThread()
    {
        using var queue = new PlayerOperationQueue("test");
        int outerThreadId = -1;
        int innerThreadId = -2;

        Task<int> outer = queue.Enqueue(() =>
        {
            outerThreadId = Environment.CurrentManagedThreadId;
            innerThreadId = queue.Execute(() => Environment.CurrentManagedThreadId);
            return innerThreadId;
        });

        int result = await outer.WaitAsync(TestEnvironment.EventuallyTimeout);

        // Ran directly on the worker (no re-queueing, no deadlock).
        Assert.Equal(result, innerThreadId);
        Assert.Equal(outerThreadId, innerThreadId);
    }

    [Fact]
    public async Task Execute_ReentrantCallFromAQueuedOperation_ReturnsTheNestedResult()
    {
        using var queue = new PlayerOperationQueue("test");

        Task<int> outer = queue.Enqueue(() => queue.Execute(() => queue.Execute(() => 5)));

        Assert.Equal(5, await outer.WaitAsync(TestEnvironment.EventuallyTimeout));
    }

    [Fact]
    public async Task Execute_NestedFromCallerTurn_RunsInlineWithoutDeadlock()
    {
        using var queue = new PlayerOperationQueue("Test");

        Task<int> operation = Task.Run(() => queue.Execute(() => queue.Execute(() => 42)));

        Assert.Equal(42, await operation.WaitAsync(TestEnvironment.EventuallyTimeout));
    }

    [Fact]
    public async Task Execute_PropagatesTheOriginalException_NotAnAggregate()
    {
        using var queue = new PlayerOperationQueue("test");
        var expected = new InvalidOperationException("boom");

        Task<int> executeTask = Task.Run(() => queue.Execute<int>(() => throw expected));

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() => executeTask.WaitAsync(TestEnvironment.EventuallyTimeout));
        Assert.Same(expected, actual);
    }

    [Fact]
    public async Task Execute_RunsAfterPreviouslyQueuedAsyncOperations()
    {
        using var queue = new PlayerOperationQueue("test");
        var order = new List<string>();

        Task<bool> asyncOp = queue.Enqueue(async () =>
        {
            order.Add("async-start");
            await Task.Delay(50).ConfigureAwait(false);
            order.Add("async-end");
            return true;
        });

        // Do not await asyncOp first: this is exactly the interleaving guard under test.
        var syncResult = queue.Execute(() =>
        {
            order.Add("sync");
            return "sync-result";
        });

        Assert.Equal("sync-result", syncResult);
        Assert.True(await asyncOp.WaitAsync(TestEnvironment.EventuallyTimeout));
        Assert.Equal(["async-start", "async-end", "sync"], order);
    }

    [Fact]
    public void Execute_RunsOperationOnCallingThread()
    {
        using var queue = new PlayerOperationQueue("test");
        int callingThreadId = Environment.CurrentManagedThreadId;

        int operationThreadId = queue.Execute(() => Environment.CurrentManagedThreadId);

        Assert.Equal(callingThreadId, operationThreadId);
    }

    [Fact]
    public void Execute_AfterDispose_ThrowsObjectDisposedException()
    {
        var queue = new PlayerOperationQueue("test");
        queue.Dispose();

        Assert.Throws<ObjectDisposedException>(() => queue.Execute(() => 42));
    }

    // ENG-024: the "inside an operation" flag no longer flows into work that outlives the
    // operation or runs on another thread.

    [Fact]
    public async Task IsExecutingOperation_IsFalseInATaskStartedByAnOperation_OnceTheOperationEnded()
    {
        using var queue = new PlayerOperationQueue("test");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<bool>? child = null;

        await queue.Enqueue(() =>
        {
            child = Task.Run(async () =>
            {
                await release.Task.ConfigureAwait(false);
                return queue.IsExecutingOperation;
            });
            return true;
        }).WaitAsync(TestEnvironment.EventuallyTimeout);
        release.SetResult();

        Assert.False(await child!.WaitAsync(TestEnvironment.EventuallyTimeout));
    }

    [Fact]
    public async Task IsExecutingOperation_IsFalseOnAnotherThread_WhileASynchronousOperationRuns()
    {
        using var queue = new PlayerOperationQueue("test");

        (bool onWorker, bool onOtherThread) result = await queue.Enqueue(() =>
        {
            // A thread (not a task, which the wait could run inline) inherits the context.
            var onOtherThread = true;
            var thread = new Thread(() => onOtherThread = queue.IsExecutingOperation);
            thread.Start();
            thread.Join();
            return (queue.IsExecutingOperation, onOtherThread);
        }).WaitAsync(TestEnvironment.EventuallyTimeout);

        Assert.True(result.onWorker);
        Assert.False(result.onOtherThread);
    }

    [Fact]
    public async Task HandlerScope_CountsAsInsideOnlyOnItsThreadAndUntilItReturns()
    {
        using var queue = new PlayerOperationQueue("test");
        var handlerReturned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var operationMayEnd = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var insideHandler = false;
        Task<bool>? afterHandler = null;
        EventHandler handler = (_, _) =>
        {
            insideHandler = queue.IsExecutingOperation;
            // Like a dispatcher callback: captures the context, runs after the handler returned
            // while the (asynchronous) operation is still running.
            afterHandler = Task.Run(async () =>
            {
                await handlerReturned.Task.ConfigureAwait(false);
                var inside = queue.IsExecutingOperation;
                operationMayEnd.SetResult();
                return inside;
            });
        };

        Task<bool> operation = queue.Enqueue(async () =>
        {
            HostEventDispatcher.Raise(this, handler, "Test");
            handlerReturned.SetResult();
            await operationMayEnd.Task.ConfigureAwait(false);
            return queue.IsExecutingOperation;
        });

        Assert.True(await operation.WaitAsync(TestEnvironment.EventuallyTimeout));
        Assert.True(insideHandler);
        Assert.False(await afterHandler!.WaitAsync(TestEnvironment.EventuallyTimeout));
    }

    [Fact]
    public async Task ExclusiveTurn_AbandonedBeforeItIsGranted_IsSkipped()
    {
        using var queue = new PlayerOperationQueue("test");
        var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<bool> blocking = queue.Enqueue(() =>
        {
            entered.SetResult();
            release.Wait();
            return true;
        });
        await entered.Task.WaitAsync(TestEnvironment.EventuallyTimeout);

        PlayerOperationQueue.ExclusiveTurn turn = queue.BeginExclusiveTurn();
        Assert.False(turn.TryWait(TimeSpan.FromMilliseconds(50)));
        release.Set();

        Assert.True(await blocking.WaitAsync(TestEnvironment.EventuallyTimeout));
        // The worker skipped the abandoned turn instead of waiting for it.
        Assert.Equal(7, await queue.Enqueue(() => 7).WaitAsync(TestEnvironment.EventuallyTimeout));
        release.Dispose();
    }

    [Fact]
    public async Task Enqueue_OperationCanceledException_CancelsTheTask()
    {
        using var queue = new PlayerOperationQueue("test");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Task<int> task = queue.Enqueue(new Func<int>(() => throw new OperationCanceledException(cancellation.Token)));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task.WaitAsync(TestEnvironment.EventuallyTimeout));
        Assert.True(task.IsCanceled);
    }
}
