using FatzBomb.MediaControl.Pipeline;

namespace FatzBomb.MediaControl.Tests;

public class SeekHelpersTests
{
    [Theory]
    [InlineData(10.0, 100.0, 10.0)]
    [InlineData(150.0, 100.0, 100.0)] // clamped to duration
    [InlineData(0.0, 100.0, 0.0)]     // zero returns zero
    [InlineData(-5.0, 100.0, 0.0)]    // negative returns zero
    [InlineData(42.0, 0.0, 42.0)]     // zero duration passes through
    public void NormalizeSeekTarget_ClampsAndCleans(double seconds, double durationSeconds, double expected) =>
        Assert.Equal(expected, SeekHelpers.NormalizeSeekTarget(seconds, durationSeconds));

    [Theory]
    [InlineData(double.NaN, 100.0)]
    [InlineData(double.PositiveInfinity, 100.0)]
    [InlineData(double.NegativeInfinity, 100.0)]
    public void NormalizeSeekTarget_ReturnsZeroForNonFinite(double seconds, double durationSeconds) =>
        Assert.Equal(0, SeekHelpers.NormalizeSeekTarget(seconds, durationSeconds));

    [Fact]
    public void NormalizeSeekTarget_NegativeZeroReturnsZero() =>
        Assert.Equal(0, SeekHelpers.NormalizeSeekTarget(-0.0, 50.0));

    [Fact]
    public void NormalizeSeekTarget_StrictNegativeReturnsZero() =>
        Assert.Equal(0, SeekHelpers.NormalizeSeekTarget(-1.0, 100.0));
}
