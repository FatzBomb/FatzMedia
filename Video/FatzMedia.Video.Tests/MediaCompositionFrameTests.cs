using FatzMedia.Video.Core.Graphics.Composition;

namespace FatzMedia.Video.Tests;

public class MediaCompositionFrameTests
{
    [Fact]
    public void BackgroundLayerCarriesOnlyBaseVisualTransform()
    {
        var transform = new BaseVisualTransform { Brightness = 0.25f };
        var frame = MediaCompositionFrame.Empty() with
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

        var layer = Assert.Single(frame.EnumerateLayers());

        Assert.Equal(MediaCompositionLayerId.Subtitle, layer.Id);
        Assert.Equal(MediaCompositionLayerSourceKind.BgraPixels, layer.SourceKind);
        Assert.Equal(7, layer.Revision);
        Assert.Equal(MediaCompositionStretchMode.UniformFit, layer.StretchMode);
        Assert.Equal(100, layer.CanvasLeft);
        Assert.Equal(200, layer.CanvasTop);
        Assert.Equal(1920, layer.CanvasWidth);
        Assert.Equal(1080, layer.CanvasHeight);
    }

    [Theory]
    [InlineData(MediaCompositionClearMode.Black, 0u, 0xFF000000u)]
    [InlineData(MediaCompositionClearMode.Black, 0xFF102030u, 0xFF102030u)]
    [InlineData(MediaCompositionClearMode.ConfiguredColor, 0u, 0u)]
    [InlineData(MediaCompositionClearMode.ConfiguredColor, 0x80FF0000u, 0x80FF0000u)]
    [InlineData(MediaCompositionClearMode.Transparent, 0xFFFFFFFFu, 0u)]
    [InlineData(MediaCompositionClearMode.PreservePrevious, 0xFFFFFFFFu, 0u)]
    public void ResolveClearArgb_AppliesClearModePolicy(MediaCompositionClearMode mode, uint clearArgb, uint expected) =>
        Assert.Equal(expected, MediaCompositionFrame.Empty(mode, clearArgb).ResolveClearArgb());

    [Theory]
    [InlineData(0xFF102030u, 0xFF102030u)]
    [InlineData(0x00FFFFFFu, 0x00000000u)]
    [InlineData(0x80FF0000u, 0x80800000u)]
    [InlineData(0x80FFFFFFu, 0x80808080u)]
    [InlineData(0x01FF7F00u, 0x01010000u)]
    public void PremultiplyBgra_RoundsEachColourChannelToNearest(uint argb, uint expected) =>
        Assert.Equal(expected, ColorMath.PremultiplyBgra(argb));

    [Fact]
    public void PremultiplyBgra_MatchesRoundedReferenceForEveryAlphaAndChannel()
    {
        for (var alpha = 0; alpha <= 255; alpha++)
        {
            for (var value = 0; value <= 255; value++)
            {
                var expected = (uint)Math.Round(value * alpha / 255.0, MidpointRounding.AwayFromZero);
                var argb = ((uint)alpha << 24) | ((uint)value << 16) | ((uint)value << 8) | (uint)value;
                var premultiplied = ColorMath.PremultiplyBgra(argb);
                Assert.Equal(((uint)alpha << 24) | (expected << 16) | (expected << 8) | expected, premultiplied);
            }
        }
    }
}
