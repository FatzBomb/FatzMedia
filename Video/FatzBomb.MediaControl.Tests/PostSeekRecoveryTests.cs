using FatzBomb.MediaControl.Pipeline;

namespace FatzBomb.MediaControl.Tests;

/// <summary>Stage 041A: the post-seek recovery state machine extracted from VideoPipeline.</summary>
public sealed class PostSeekRecoveryTests
{
    private const long Frequency = 1000; // 1 tick = 1 ms

    [Theory]
    [InlineData(0, 900)]
    [InlineData(-1, 900)]
    [InlineData(1, 700)]
    [InlineData(2, 700)]
    [InlineData(3, 450)]
    [InlineData(12, 450)]
    public void TheWindow_IsLongerForAnEmptyOrUnderfilledQueue(int buffered, int expected) =>
        Assert.Equal(expected, PostSeekRecovery.RecoveryMillisecondsFor(buffered));

    [Theory]
    [InlineData(12, 2, 3)]
    [InlineData(12, 6, 6)]
    [InlineData(2, 6, 2)]
    public void TheWarmupTarget_IsAtLeastTheRecoveryMinimumAndAtMostTheNormalTarget(int target, int minimum, int expected) =>
        Assert.Equal(expected, PostSeekRecovery.WarmupTargetFrames(target, minimum));

    [Fact]
    public void Recovery_RunsItsWindowThenSuppressesTwoAdaptiveWindows()
    {
        var recovery = new PostSeekRecovery();
        Assert.Equal(PostSeekRecovery.WindowAction.Adapt, recovery.NextWindow(0, out _));
        Assert.False(recovery.ShouldSuppressLateFrameDrop(0));

        Assert.Equal(450, recovery.Begin(bufferedFrames: 5, now: 1000, frequency: Frequency));
        Assert.True(recovery.IsActive(1449));
        Assert.True(recovery.ShouldSuppressLateFrameDrop(1449));
        Assert.Equal(PostSeekRecovery.WindowAction.Recovering, recovery.NextWindow(1449, out _));
        Assert.Equal(2, recovery.SuppressionWindowsRemaining);

        Assert.False(recovery.IsActive(1450));
        Assert.True(recovery.ShouldSuppressLateFrameDrop(1450));
        Assert.Equal(PostSeekRecovery.WindowAction.Suppressed, recovery.NextWindow(1500, out var remaining));
        Assert.Equal(1, remaining);
        Assert.Equal(PostSeekRecovery.WindowAction.Suppressed, recovery.NextWindow(1600, out remaining));
        Assert.Equal(0, remaining);
        Assert.False(recovery.ShouldSuppressLateFrameDrop(1700));
        Assert.Equal(PostSeekRecovery.WindowAction.Adapt, recovery.NextWindow(1700, out _));
    }

    [Fact]
    public void ANewSeek_RestartsTheWindowAndReset_ClearsIt()
    {
        var recovery = new PostSeekRecovery();
        recovery.Begin(0, now: 0, frequency: Frequency);
        recovery.Begin(5, now: 800, frequency: Frequency);
        Assert.True(recovery.IsActive(1249));
        Assert.False(recovery.IsActive(1250));

        recovery.Reset();
        Assert.False(recovery.IsActive(900));
        Assert.Equal(0, recovery.SuppressionWindowsRemaining);
        Assert.Equal(PostSeekRecovery.WindowAction.Adapt, recovery.NextWindow(900, out _));
    }
}
