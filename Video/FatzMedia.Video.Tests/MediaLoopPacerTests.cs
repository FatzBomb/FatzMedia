using FatzMedia.Video.Core.Threading;

namespace FatzMedia.Video.Tests;

public sealed class MediaLoopPacerTests
{
    [Fact]
    public void WaitNext_CompensatesForWorkTimeAgainstAbsoluteDeadline()
    {
        var timeProvider = new ManualTimeProvider();
        var waiterFactory = new AdvancingWaiterFactory(timeProvider);
        using var cancellation = new ManualResetEvent(false);
        using var pacer = new MediaLoopPacer(waiterFactory, timeProvider);

        pacer.WaitNext(TimeSpan.FromMilliseconds(10), cancellation);
        timeProvider.Advance(TimeSpan.FromMilliseconds(3));
        pacer.WaitNext(TimeSpan.FromMilliseconds(10), cancellation);

        Assert.Equal(
            [TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(7)],
            waiterFactory.Waits);
    }

    [Fact]
    public void WaitNext_WhenLate_ResetsInsteadOfIssuingCatchUpTicks()
    {
        var timeProvider = new ManualTimeProvider();
        var waiterFactory = new AdvancingWaiterFactory(timeProvider);
        using var cancellation = new ManualResetEvent(false);
        using var pacer = new MediaLoopPacer(waiterFactory, timeProvider);

        pacer.WaitNext(TimeSpan.FromMilliseconds(10), cancellation);
        timeProvider.Advance(TimeSpan.FromMilliseconds(15));
        pacer.WaitNext(TimeSpan.FromMilliseconds(10), cancellation);
        pacer.WaitNext(TimeSpan.FromMilliseconds(10), cancellation);

        Assert.Equal(
            [TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(10)],
            waiterFactory.Waits);
    }

    [Fact]
    public void WaitNext_WhenLate_UsesConfiguredYield()
    {
        var timeProvider = new ManualTimeProvider();
        var waiterFactory = new AdvancingWaiterFactory(timeProvider);
        using var cancellation = new ManualResetEvent(false);
        using var pacer = new MediaLoopPacer(waiterFactory, timeProvider);
        timeProvider.Advance(TimeSpan.FromMilliseconds(15));

        pacer.WaitNext(
            TimeSpan.FromMilliseconds(10),
            cancellation,
            lateYield: TimeSpan.FromMilliseconds(1));

        Assert.Equal([TimeSpan.FromMilliseconds(1)], waiterFactory.Waits);
    }

    [Fact]
    public void WaitFor_ResetsFixedCadenceAccumulator()
    {
        var timeProvider = new ManualTimeProvider();
        var waiterFactory = new AdvancingWaiterFactory(timeProvider);
        using var cancellation = new ManualResetEvent(false);
        using var pacer = new MediaLoopPacer(waiterFactory, timeProvider);

        pacer.WaitNext(TimeSpan.FromMilliseconds(10), cancellation);
        timeProvider.Advance(TimeSpan.FromMilliseconds(2));
        pacer.WaitFor(TimeSpan.FromMilliseconds(5), cancellation);
        pacer.WaitNext(TimeSpan.FromMilliseconds(10), cancellation);

        Assert.Equal(
            [
                TimeSpan.FromMilliseconds(10),
                TimeSpan.FromMilliseconds(5),
                TimeSpan.FromMilliseconds(10),
            ],
            waiterFactory.Waits);
    }

    [Fact]
    public void IsHighResolution_ReflectsPlatformWaiter()
    {
        var timeProvider = new ManualTimeProvider();
        using var pacer = new MediaLoopPacer(
            new AdvancingWaiterFactory(timeProvider, isHighResolution: true),
            timeProvider);

        Assert.True(pacer.IsHighResolution);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void WaitNext_RejectsNonPositiveInterval(int milliseconds)
    {
        var timeProvider = new ManualTimeProvider();
        using var cancellation = new ManualResetEvent(false);
        using var pacer = new MediaLoopPacer(new AdvancingWaiterFactory(timeProvider), timeProvider);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            pacer.WaitNext(TimeSpan.FromMilliseconds(milliseconds), cancellation));
    }

    [Fact]
    public void WaitNext_RejectsNegativeLateYield()
    {
        var timeProvider = new ManualTimeProvider();
        using var cancellation = new ManualResetEvent(false);
        using var pacer = new MediaLoopPacer(new AdvancingWaiterFactory(timeProvider), timeProvider);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            pacer.WaitNext(
                TimeSpan.FromMilliseconds(10),
                cancellation,
                lateYield: TimeSpan.FromMilliseconds(-1)));
    }

    [Fact]
    public void Reset_AfterDispose_ThrowsObjectDisposedException()
    {
        var timeProvider = new ManualTimeProvider();
        var pacer = new MediaLoopPacer(new AdvancingWaiterFactory(timeProvider), timeProvider);
        pacer.Dispose();

        Assert.Throws<ObjectDisposedException>(pacer.Reset);
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp;

        public override long TimestampFrequency => 1_000;
        public override long GetTimestamp() => _timestamp;

        public void Advance(TimeSpan duration) =>
            _timestamp += (long)Math.Round(duration.TotalSeconds * TimestampFrequency);
    }

    private sealed class AdvancingWaiterFactory(
        ManualTimeProvider timeProvider,
        bool isHighResolution = false) : IPrecisionWaiterFactory
    {
        public List<TimeSpan> Waits { get; } = [];

        public IPrecisionWaiter Create() => new AdvancingWaiter(this, timeProvider, isHighResolution);

        private sealed class AdvancingWaiter(
            AdvancingWaiterFactory owner,
            ManualTimeProvider timeProvider,
            bool isHighResolution) : IPrecisionWaiter
        {
            public bool IsHighResolution => isHighResolution;

            public void Wait(TimeSpan duration, WaitHandle cancellationHandle)
            {
                owner.Waits.Add(duration);
                timeProvider.Advance(duration);
            }

            public void Dispose()
            {
            }
        }
    }
}
