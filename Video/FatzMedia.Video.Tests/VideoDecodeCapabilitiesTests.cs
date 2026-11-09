namespace FatzMedia.Video.Tests;

public sealed class VideoDecodeCapabilitiesTests
{
    [Fact]
    public void Create_NormalizesLookupKeys()
    {
        var codec = new CodecCapability("H264", "H.264", true, "h264", true, "d3d11va");
        var container = new ContainerCapability(".MP4", "mov,mp4", "MPEG-4", [codec], true);

        var capabilities = VideoDecodeCapabilities.Create([codec], [container]);

        Assert.Same(codec, capabilities.ForCodec("h264"));
        Assert.Same(container, capabilities.ForExtension("mp4"));
    }

    [Fact]
    public void Create_EmptyCollectionsProducesEmptySnapshot()
    {
        var capabilities = VideoDecodeCapabilities.Create([], []);

        Assert.Empty(capabilities.Codecs);
        Assert.Empty(capabilities.Containers);
    }
}
