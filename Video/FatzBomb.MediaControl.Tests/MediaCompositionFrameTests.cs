using FatzBomb.MediaControl.Core.Graphics.Composition;

namespace FatzBomb.MediaControl.Tests;

public class MediaCompositionFrameTests
{
    [Fact]
    public void BackgroundLayerCarriesOnlyBaseVisualTransform()
    {
        var transform = new BaseVisualTransform { Brightness = 0.25f };
        MediaCompositionFrame frame = MediaCompositionFrame.Empty() with
        {
            BackgroundBgra = new byte[4],
            BackgroundWidth = 1,
            BackgroundHeight = 1,
            BackgroundStride = 4,
            BaseVisualTransform = transform,
            WashArgb = 0x80FFFFFF,
            SubtitleBgra = new byte[4],
            SubtitleWidth = 1,
            SubtitleHeight = 1,
            SubtitleStride = 4
        };

        MediaCompositionLayer[] layers = [.. frame.EnumerateLayers()];

        Assert.Equal(transform, layers.Single(static layer => layer.Id == MediaCompositionLayerId.Background).BaseVisualTransform);
        Assert.True(layers.Where(static layer => layer.Id != MediaCompositionLayerId.Background)
            .All(static layer => layer.BaseVisualTransform.IsIdentity));
    }

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
