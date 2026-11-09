using FatzMedia.Video.Core.Graphics;

namespace FatzMedia.Video.Tests;

public class CdgPresentationRequestQueueTests
{
    [Fact]
    public void TryDequeue_WhenEmpty_ReturnsFalseWithDefaults()
    {
        var queue = new CdgPresentationRequestQueue();

        var dequeued = queue.TryDequeue(out var consumeDirtyRegions, out var retainedRefresh, out var reason);

        Assert.False(dequeued);
        Assert.False(consumeDirtyRegions);
        Assert.False(retainedRefresh);
        Assert.Equal("cdg pending", reason);
    }

    [Fact]
    public void Enqueue_ThenTryDequeue_ReturnsRequestAndEmpties()
    {
        var queue = new CdgPresentationRequestQueue();
        queue.Enqueue(consumeDirtyRegions: true, retainedRefresh: true, reason: "first");

        Assert.True(queue.TryDequeue(out var consume, out var retained, out var reason));
        Assert.True(consume);
        Assert.True(retained);
        Assert.Equal("first", reason);

        // Single-slot queue is emptied by a successful dequeue.
        Assert.False(queue.TryDequeue(out _, out _, out _));
    }

    [Fact]
    public void Enqueue_CoalescesConsumeDirtyRegionsWithOr()
    {
        var queue = new CdgPresentationRequestQueue();
        queue.Enqueue(consumeDirtyRegions: false, retainedRefresh: true, reason: "a");
        queue.Enqueue(consumeDirtyRegions: true, retainedRefresh: true, reason: "b");

        Assert.True(queue.TryDequeue(out var consume, out _, out var reason));
        Assert.True(consume);          // OR-merged: once any request consumes, the coalesced one does
        Assert.Equal("b", reason);     // latest reason wins
    }

    [Fact]
    public void Enqueue_CoalescesRetainedRefreshWithAnd()
    {
        var queue = new CdgPresentationRequestQueue();
        queue.Enqueue(consumeDirtyRegions: false, retainedRefresh: true, reason: "a");
        queue.Enqueue(consumeDirtyRegions: false, retainedRefresh: false, reason: "b");

        Assert.True(queue.TryDequeue(out _, out var retained, out _));
        Assert.False(retained);        // AND-merged: a non-retained request downgrades the coalesced one
    }

    [Fact]
    public void Enqueue_FirstRequestTakesRetainedValueDirectly()
    {
        var queue = new CdgPresentationRequestQueue();

        // Fresh (not pending) request keeps its retained flag rather than AND-ing with a stale default.
        queue.Enqueue(consumeDirtyRegions: false, retainedRefresh: true, reason: "fresh");

        Assert.True(queue.TryDequeue(out _, out var retained, out _));
        Assert.True(retained);
    }

    [Fact]
    public void Clear_DiscardsPendingRequest()
    {
        var queue = new CdgPresentationRequestQueue();
        queue.Enqueue(consumeDirtyRegions: true, retainedRefresh: false, reason: "pending");

        queue.Clear();

        Assert.False(queue.TryDequeue(out _, out _, out _));
    }
}
