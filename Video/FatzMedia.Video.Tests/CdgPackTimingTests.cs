using FatzMedia.Video.Core.Cdg;

namespace FatzMedia.Video.Tests;

/// <summary>
/// ENG-029: the pack count for a playback position is computed in integer nanoseconds, so exact
/// multiples of 1/300 s count the same pack everywhere (a double product truncated some of them
/// one pack low).
/// </summary>
public sealed class CdgPackTimingTests
{
    [Theory]
    [InlineData(0.0, 0)]
    [InlineData(-1.0, 0)]
    [InlineData(double.NaN, 0)]
    [InlineData(1.0, 300)]
    [InlineData(59.99, 17_997)]
    [InlineData(0.29, 87)]
    [InlineData(0.58, 174)]
    [InlineData(4.35, 1_305)]
    [InlineData(10.0, 3_000)]
    public void ExactBoundaries_CountExactly(double seconds, long packs) =>
        Assert.Equal(packs, CdgEngine.PacketCountAt(seconds));

    [Fact]
    public void EveryMultipleOfOneThreeHundredth_CountsItsOwnIndex()
    {
        // Positions given as the nearest double to n / 300 s: a pack boundary, whatever the
        // rounding of the division.
        for (var n = 0; n <= 300 * 600; n++)
            Assert.Equal(n, CdgEngine.PacketCountAt(n / 300.0));
    }

    [Fact]
    public void JustBeforeABoundary_StaysOnThePreviousPack()
    {
        for (var n = 1; n <= 300 * 60; n += 7)
            Assert.Equal(n - 1, CdgEngine.PacketCountAt((n / 300.0) - 1e-6));
    }

    [Fact]
    public void InBetween_RoundsDown()
    {
        Assert.Equal(1, CdgEngine.PacketCountAt(0.0049));
        Assert.Equal(0, CdgEngine.PacketCountAt(0.0033));
        Assert.Equal(1, CdgEngine.PacketCountAt(0.0034));
    }

    [Fact]
    public void HugeAndInfinitePositions_DoNotOverflow()
    {
        Assert.True(CdgEngine.PacketCountAt(double.PositiveInfinity) > 1_000_000_000L);
        Assert.True(CdgEngine.PacketCountAt(1e300) > 1_000_000_000L);
        Assert.Equal(0, CdgEngine.PacketCountAt(double.NegativeInfinity));
    }
}
