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
            SubtitleCanvasLeft = 100,
            SubtitleCanvasTop = 200,
            SubtitleCanvasWidth = 1920,
            SubtitleCanvasHeight = 1080,
            SubtitleRevision = 7
        };

        MediaCompositionLayer layer = Assert.Single(frame.EnumerateLayers());

        Assert.Equal(MediaCompositionLayerId.Subtitle, layer.Id);
        Assert.Equal(MediaCompositionLayerSourceKind.BgraPixels, layer.SourceKind);
        Assert.Equal(7, layer.Revision);
        Assert.Equal(MediaCompositionStretchMode.UniformFit, layer.StretchMode);
        Assert.Equal(100, layer.CanvasLeft);
        Assert.Equal(200, layer.CanvasTop);
        Assert.Equal(1920, layer.CanvasWidth);
        Assert.Equal(1080, layer.CanvasHeight);
    }
}
