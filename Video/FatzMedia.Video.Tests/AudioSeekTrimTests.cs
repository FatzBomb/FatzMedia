using FatzMedia.Video.Core.Audio;

namespace FatzMedia.Video.Tests;

/// <summary>After a seek, the audio frame spanning the target is trimmed to start at the target.</summary>
public sealed class AudioSeekTrimTests
{
    [Theory]
    [InlineData(44100)]
    [InlineData(48000)]
    public void AFrameSpanningTheTarget_SkipsTheSamplesBeforeIt(int sampleRate)
    {
        // A 1024-sample frame starting 10 ms before the target.
        var skip = AudioSeekTrim.SamplesToSkip(framePositionSeconds: 4.99, targetSeconds: 5, sampleRate, frameSamples: 1024);

        Assert.Equal((int)Math.Round(0.01 * sampleRate), skip);
    }

    [Theory]
    [InlineData(5.0)]
    [InlineData(5.5)]
    public void AFrameAtOrAfterTheTarget_SkipsNothing(double framePosition) =>
        Assert.Equal(0, AudioSeekTrim.SamplesToSkip(framePosition, targetSeconds: 5, sampleRate: 48000, frameSamples: 1024));

    [Fact]
    public void AFrameWhollyBeforeTheTarget_SkipsAllOfIt() =>
        Assert.Equal(1024, AudioSeekTrim.SamplesToSkip(framePositionSeconds: 4, targetSeconds: 5, sampleRate: 48000, frameSamples: 1024));

    [Theory]
    [InlineData(double.NaN, 5, 48000, 1024)]
    [InlineData(4.99, double.NaN, 48000, 1024)]
    [InlineData(4.99, 5, 0, 1024)]
    [InlineData(4.99, 5, 48000, 0)]
    public void UnusableInput_SkipsNothing(double framePosition, double target, int sampleRate, int frameSamples) =>
        Assert.Equal(0, AudioSeekTrim.SamplesToSkip(framePosition, target, sampleRate, frameSamples));
}
