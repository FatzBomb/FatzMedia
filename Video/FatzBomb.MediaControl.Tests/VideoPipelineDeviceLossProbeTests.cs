using FatzBomb.MediaControl.Pipeline;

namespace FatzBomb.MediaControl.Tests;

/// <summary>
/// ENG-039: after a GPU reset a hardware decoder keeps returning frames, so the decode loop also
/// asks whether its device was lost once a second, not only after repeated decode failures.
/// </summary>
public sealed class VideoPipelineDeviceLossProbeTests
{
    [Fact]
    public void RepeatedFailures_ProbeAtOnce() =>
        Assert.True(VideoPipeline.ShouldProbeDeviceLoss(repeatedlyFailing: true, nowTimestamp: 10, nextProbeTimestamp: 100));

    [Fact]
    public void WithoutFailures_ProbeOnlyWhenTheIntervalHasPassed()
    {
        Assert.False(VideoPipeline.ShouldProbeDeviceLoss(repeatedlyFailing: false, nowTimestamp: 99, nextProbeTimestamp: 100));
        Assert.True(VideoPipeline.ShouldProbeDeviceLoss(repeatedlyFailing: false, nowTimestamp: 100, nextProbeTimestamp: 100));
    }
}
