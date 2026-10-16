using FatzBomb.MediaControl.Core.Threading;

namespace FatzBomb.MediaControl.Tests;

public class MediaControlOperationQueueTests
{
    [Fact]
    public async Task Enqueue_RunsOperationsInFifoOrder()
    {
        using var queue = new MediaControlOperationQueue("test");
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
        using var queue = new MediaControlOperationQueue("test");
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
        using var queue = new MediaControlOperationQueue("test");
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
        using var queue = new MediaControlOperationQueue("test");

        Task<int> outer = queue.Enqueue(() => queue.Execute(() => queue.Execute(() => 5)));

        Assert.Equal(5, await outer.WaitAsync(TestEnvironment.EventuallyTimeout));
    }

    [Fact]
    public async Task Execute_NestedFromCallerTurn_RunsInlineWithoutDeadlock()
    {
        using var queue = new MediaControlOperationQueue("Test");

        Task<int> operation = Task.Run(() => queue.Execute(() => queue.Execute(() => 42)));

        Assert.Equal(42, await operation.WaitAsync(TestEnvironment.EventuallyTimeout));
    }

    [Fact]
    public async Task Execute_PropagatesTheOriginalException_NotAnAggregate()
    {
        using var queue = new MediaControlOperationQueue("test");
        var expected = new InvalidOperationException("boom");

        Task<int> executeTask = Task.Run(() => queue.Execute<int>(() => throw expected));

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() => executeTask.WaitAsync(TestEnvironment.EventuallyTimeout));
        Assert.Same(expected, actual);
    }

    [Fact]
    public async Task Execute_RunsAfterPreviouslyQueuedAsyncOperations()
    {
        using var queue = new MediaControlOperationQueue("test");
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
        using var queue = new MediaControlOperationQueue("test");
        int callingThreadId = Environment.CurrentManagedThreadId;

        int operationThreadId = queue.Execute(() => Environment.CurrentManagedThreadId);

        Assert.Equal(callingThreadId, operationThreadId);
    }

    [Fact]
    public void Execute_AfterDispose_ThrowsObjectDisposedException()
    {
        var queue = new MediaControlOperationQueue("test");
        queue.Dispose();

        Assert.Throws<ObjectDisposedException>(() => queue.Execute(() => 42));
    }
}
