using FatzMedia.Video.Core.Threading;

namespace FatzMedia.Video.Tests;

public sealed class HostEventDispatcherTests
{
    [Fact]
    public void Raise_ThrowingHandler_DoesNotStopTheOthers()
    {
        var calls = new List<int>();
        EventHandler? handlers = null;
        handlers += (_, _) => calls.Add(1);
        handlers += (_, _) => throw new InvalidOperationException("host bug");
        handlers += (_, _) => calls.Add(3);

        HostEventDispatcher.Raise(this, handlers, "Test", reportFailures: false);

        Assert.Equal([1, 3], calls);
    }

    [Fact]
    public void RaiseGeneric_ThrowingHandler_DoesNotStopTheOthers()
    {
        var calls = new List<int>();
        EventHandler<TestEventArgs>? handlers = null;
        handlers += (_, e) => calls.Add(e.Value);
        handlers += (_, _) => throw new InvalidOperationException("host bug");
        handlers += (_, e) => calls.Add(e.Value + 1);

        HostEventDispatcher.Raise(this, handlers, new TestEventArgs(7), "Test", reportFailures: false);

        Assert.Equal([7, 8], calls);
    }

    [Fact]
    public void Raise_MulticastHandlers_DoesNotAllocate()
    {
        var count = 0;
        EventHandler? handlers = null;
        handlers += (_, _) => count++;
        handlers += (_, _) => count++;
        handlers += (_, _) => count++;
        EventHandler<TestEventArgs>? genericHandlers = null;
        genericHandlers += (_, _) => count++;
        genericHandlers += (_, _) => count++;
        var args = new TestEventArgs(0);

        // Warm up (JIT, tiering) before measuring.
        for (var i = 0; i < 100; i++)
        {
            HostEventDispatcher.Raise(this, handlers, "Test");
            HostEventDispatcher.Raise(this, genericHandlers, args, "Test");
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++)
        {
            HostEventDispatcher.Raise(this, handlers, "Test");
            HostEventDispatcher.Raise(this, genericHandlers, args, "Test");
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(5 * 1100, count);
        Assert.Equal(0, allocated);
    }

    private sealed class TestEventArgs(int value) : EventArgs
    {
        public int Value { get; } = value;
    }
}
