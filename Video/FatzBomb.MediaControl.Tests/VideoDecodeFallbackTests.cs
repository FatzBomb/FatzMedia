using FatzBomb.MediaControl.Core.Graphics;
using FatzBomb.MediaControl.Pipeline;

namespace FatzBomb.MediaControl.Tests;

/// <summary>
/// ENG-037: a renderer that cannot take a decoder's hardware frames asks it, through the frame's
/// fallback token, to continue in software; the pipeline then stays in software for that media.
/// </summary>
public sealed class VideoDecodeFallbackTests
{
    [Fact]
    public void FirstRequest_KeepsItsReason_AndNotifiesOnce()
    {
        var notified = new List<string>();
        var fallback = new VideoDecodeFallback(notified.Add);
        Assert.False(fallback.IsRequested);

        fallback.RequestSoftwareDecoding("first");
        fallback.RequestSoftwareDecoding("second");

        Assert.True(fallback.IsRequested);
        Assert.Equal("first", fallback.Reason);
        Assert.Equal(["first"], notified);
    }

    [Theory]
    [InlineData(true, true, false, true)]
    [InlineData(false, true, false, false)] // the decoder was replaced meanwhile
    [InlineData(true, false, false, false)] // closed
    [InlineData(true, true, true, false)]   // a lost device: the device-loss recovery handles it
    public void Pipeline_FallsBackOnlyForTheLiveDecoderOfOpenMedia_OnALiveDevice(bool isLiveDecoder, bool isOpen, bool deviceLost, bool expected) =>
        Assert.Equal(expected, VideoPipeline.ShouldFallBackForRenderer(isLiveDecoder, isOpen, deviceLost));

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)] // the renderer refused this media's hardware frames
    public void Pipeline_ReprobesHardware_OnlyForLocalMediaTheRendererAccepted(bool isNetwork, bool refusedByRenderer, bool expected) =>
        Assert.Equal(expected, VideoPipeline.ShouldReprobeHardware(isNetwork, refusedByRenderer));
}
