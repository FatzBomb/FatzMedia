using FatzBombMedia.MediaControl.Core.Graphics.Composition;

namespace FatzBombMedia.MediaControl.Tests;

public class MediaCompositionFrameTests
{
    [Fact]
    public void EnumerateLayers_EmitsSeparateSubtitleBgraLayer()
    {
        var frame = new MediaCompositionFrame
        {
            SubtitleBgra = new byte[4 * 4 * 4],
            SubtitleWidth = 4,
            SubtitleHeight = 4,
            SubtitleStride = 16,
            SubtitleRevision = 7
        };

        var layer = Assert.Single(frame.EnumerateLayers());

        Assert.Equal(MediaCompositionLayerId.Subtitle, layer.Id);
        Assert.Equal(MediaCompositionLayerSourceKind.BgraPixels, layer.SourceKind);
        Assert.Equal(7, layer.Revision);
        Assert.Equal(MediaCompositionStretchMode.Fill, layer.StretchMode);
    }
}
