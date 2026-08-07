using FatzBomb.MediaControl.Core.Cdg;
using FatzBomb.MediaControl.Core.Graphics.Composition;
using FatzBomb.MediaControl.Rendering.Software;

namespace FatzBomb.MediaControl.Tests;

public sealed class SoftwareCompositionRendererTests
{
    private readonly SoftwareCompositionRenderer _renderer = new();

    [Fact]
    public void Render_BlackClear_ProducesOpaqueBlack()
    {
        SoftwareRenderOutput output = _renderer.Render(MediaCompositionFrame.Empty(), 2, 1);

        Assert.Equal(SoftwareRenderStatus.Rendered, output.Status);
        Assert.Equal(new byte[] { 0, 0, 0, 255, 0, 0, 0, 255 }, output.Pixels.ToArray());
    }

    [Fact]
    public void Render_ConfiguredColor_PremultipliesOutput()
    {
        var frame = MediaCompositionFrame.Empty(MediaCompositionClearMode.ConfiguredColor, 0x80804020);

        SoftwareRenderOutput output = _renderer.Render(frame, 1, 1);

        AssertPixel(output, 0, 0, blue: 16, green: 32, red: 64, alpha: 128);
    }

    [Theory]
    [InlineData(MediaCompositionClearMode.Transparent)]
    [InlineData(MediaCompositionClearMode.PreservePrevious)]
    public void Render_StatelessTransparentClearModes_ProduceTransparentPixels(MediaCompositionClearMode clearMode)
    {
        SoftwareRenderOutput output = _renderer.Render(MediaCompositionFrame.Empty(clearMode), 1, 1);

        AssertPixel(output, 0, 0, blue: 0, green: 0, red: 0, alpha: 0);
    }

    [Fact]
    public void Render_FillBackground_UsesNearestNeighborScaling()
    {
        var frame = CreateBackgroundFrame(
            [0, 0, 255, 255, 0, 255, 0, 255],
            width: 2,
            height: 1,
            MediaCompositionStretchMode.Fill);

        SoftwareRenderOutput output = _renderer.Render(frame, 4, 1);

        AssertPixel(output, 0, 0, 0, 0, 255, 255);
        AssertPixel(output, 1, 0, 0, 0, 255, 255);
        AssertPixel(output, 2, 0, 0, 255, 0, 255);
        AssertPixel(output, 3, 0, 0, 255, 0, 255);
    }

    [Fact]
    public void Render_UniformFitBackground_PreservesLetterboxClear()
    {
        var frame = CreateBackgroundFrame(
            [255, 0, 0, 255, 255, 0, 0, 255],
            width: 2,
            height: 1,
            MediaCompositionStretchMode.UniformFit);

        SoftwareRenderOutput output = _renderer.Render(frame, 2, 2);

        AssertPixel(output, 0, 0, 0, 0, 0, 255);
        AssertPixel(output, 0, 1, 255, 0, 0, 255);
    }

    [Fact]
    public void Render_UniformToFillBackground_CropsSourceCenter()
    {
        var frame = CreateBackgroundFrame(
            [0, 0, 255, 255, 0, 255, 0, 255, 255, 0, 0, 255, 255, 255, 255, 255,
             0, 0, 255, 255, 0, 255, 0, 255, 255, 0, 0, 255, 255, 255, 255, 255],
            width: 4,
            height: 2,
            MediaCompositionStretchMode.UniformToFill);

        SoftwareRenderOutput output = _renderer.Render(frame, 2, 2);

        AssertPixel(output, 0, 0, 0, 255, 0, 255);
        AssertPixel(output, 1, 0, 255, 0, 0, 255);
    }

    [Fact]
    public void Render_OriginalBackground_CentersAtNativeSize()
    {
        var frame = CreateBackgroundFrame(
            [0, 0, 255, 255, 0, 0, 255, 255],
            width: 2,
            height: 1,
            MediaCompositionStretchMode.Original);

        SoftwareRenderOutput output = _renderer.Render(frame, 4, 3);

        AssertPixel(output, 0, 1, 0, 0, 0, 255);
        AssertPixel(output, 1, 1, 0, 0, 255, 255);
        AssertPixel(output, 2, 1, 0, 0, 255, 255);
        AssertPixel(output, 3, 1, 0, 0, 0, 255);
    }

    [Fact]
    public void Render_Wash_UsesStraightAlphaOverBackground()
    {
        MediaCompositionFrame frame = MediaCompositionFrame.Empty() with
        {
            WashArgb = 0x80FFFFFF
        };

        SoftwareRenderOutput output = _renderer.Render(frame, 1, 1);

        AssertPixel(output, 0, 0, 128, 128, 128, 255);
    }

    [Fact]
    public void Render_CdgOverlay_HidesOnlyBackgroundColorIndex()
    {
        var indices = new byte[300 * 216];
        indices[0] = 1;
        var snapshot = new CdgRenderSnapshot(
            1,
            1,
            indices,
            new int[] { unchecked((int)0xFF000000), unchecked((int)0xFFFF0000) },
            0,
            0,
            []);
        MediaCompositionFrame frame = MediaCompositionFrame.Empty(
            MediaCompositionClearMode.ConfiguredColor,
            0xFF0000FF) with
        {
            CdgOverlay = snapshot
        };

        SoftwareRenderOutput output = _renderer.Render(frame, 300, 216);

        AssertPixel(output, 0, 0, 0, 0, 255, 255);
        AssertPixel(output, 1, 0, 255, 0, 0, 255);
    }

    [Fact]
    public void Render_CdgOverlay_SkipsPaletteIndexOutsidePalette()
    {
        var indices = new byte[300 * 216];
        indices[0] = 99;
        var snapshot = new CdgRenderSnapshot(
            1,
            1,
            indices,
            new int[] { unchecked((int)0xFF000000), unchecked((int)0xFFFF0000) },
            0,
            0,
            []);
        MediaCompositionFrame frame = MediaCompositionFrame.Empty(
            MediaCompositionClearMode.ConfiguredColor,
            0xFF0000FF) with
        {
            CdgOverlay = snapshot
        };

        SoftwareRenderOutput output = _renderer.Render(frame, 300, 216);

        AssertPixel(output, 0, 0, 255, 0, 0, 255);
    }

    [Fact]
    public void Render_CdgOverlay_SuppressionLeavesClearColor()
    {
        var indices = new byte[300 * 216];
        indices[0] = 1;
        var snapshot = new CdgRenderSnapshot(
            1,
            1,
            indices,
            new int[] { unchecked((int)0xFF000000), unchecked((int)0xFFFF0000) },
            0,
            0,
            []);
        MediaCompositionFrame frame = MediaCompositionFrame.Empty(
            MediaCompositionClearMode.ConfiguredColor,
            0xFF0000FF) with
        {
            CdgOverlay = snapshot,
            SuppressCdgOverlayDraw = true
        };

        SoftwareRenderOutput output = _renderer.Render(frame, 300, 216);

        AssertPixel(output, 0, 0, 255, 0, 0, 255);
    }

    [Fact]
    public void Render_Subtitle_UsesCanvasPlacementAndPremultipliedBlend()
    {
        MediaCompositionFrame frame = MediaCompositionFrame.Empty() with
        {
            SubtitleBgra = new byte[] { 0, 0, 128, 128 },
            SubtitleWidth = 1,
            SubtitleHeight = 1,
            SubtitleStride = 4,
            SubtitleCanvasLeft = 1,
            SubtitleCanvasTop = 1,
            SubtitleCanvasWidth = 4,
            SubtitleCanvasHeight = 4
        };

        SoftwareRenderOutput output = _renderer.Render(frame, 4, 4);

        AssertPixel(output, 1, 1, 0, 0, 128, 255);
        AssertPixel(output, 0, 0, 0, 0, 0, 255);
    }

    [Fact]
    public void Render_SubtitleRemainsTopmostLayer()
    {
        MediaCompositionFrame frame = CreateBackgroundFrame(
            [255, 0, 0, 255],
            width: 1,
            height: 1,
            MediaCompositionStretchMode.Fill) with
        {
            WashArgb = 0x80FF0000,
            SubtitleBgra = new byte[] { 0, 255, 0, 255 },
            SubtitleWidth = 1,
            SubtitleHeight = 1,
            SubtitleStride = 4
        };

        SoftwareRenderOutput output = _renderer.Render(frame, 1, 1);

        AssertPixel(output, 0, 0, 0, 255, 0, 255);
    }

    [Fact]
    public void Render_RepeatedInput_IsByteIdentical()
    {
        MediaCompositionFrame frame = CreateBackgroundFrame(
            [10, 20, 30, 255],
            width: 1,
            height: 1,
            MediaCompositionStretchMode.Fill) with
        {
            WashArgb = 0x40302010
        };

        SoftwareRenderOutput first = _renderer.Render(frame, 3, 3);
        SoftwareRenderOutput second = _renderer.Render(frame, 3, 3);

        Assert.Equal(first.Pixels.ToArray(), second.Pixels.ToArray());
    }

    [Fact]
    public void Render_RetainedBackground_ReturnsUnsupported()
    {
        MediaCompositionFrame frame = MediaCompositionFrame.Empty() with
        {
            UseRetainedBackground = true,
            BackgroundWidth = 1,
            BackgroundHeight = 1
        };

        SoftwareRenderOutput output = _renderer.Render(frame, 1, 1);

        Assert.Equal(SoftwareRenderStatus.UnsupportedFrame, output.Status);
        Assert.Empty(output.Pixels.ToArray());
    }

    [Fact]
    public void Render_InvalidBackgroundStride_ReturnsInvalid()
    {
        MediaCompositionFrame frame = MediaCompositionFrame.Empty() with
        {
            BackgroundBgra = new byte[4],
            BackgroundWidth = 2,
            BackgroundHeight = 1,
            BackgroundStride = 4
        };

        SoftwareRenderOutput output = _renderer.Render(frame, 1, 1);

        Assert.Equal(SoftwareRenderStatus.InvalidFrame, output.Status);
    }

    [Fact]
    public void Render_InvalidSubtitleStride_ReturnsInvalid()
    {
        MediaCompositionFrame frame = MediaCompositionFrame.Empty() with
        {
            SubtitleBgra = new byte[4],
            SubtitleWidth = 2,
            SubtitleHeight = 1,
            SubtitleStride = 4
        };

        SoftwareRenderOutput output = _renderer.Render(frame, 1, 1);

        Assert.Equal(SoftwareRenderStatus.InvalidFrame, output.Status);
    }

    [Fact]
    public void Render_ZeroOutputSize_ReturnsInvalid()
    {
        SoftwareRenderOutput output = _renderer.Render(MediaCompositionFrame.Empty(), 0, 1);

        Assert.Equal(SoftwareRenderStatus.InvalidFrame, output.Status);
    }

    private static MediaCompositionFrame CreateBackgroundFrame(
        byte[] pixels,
        int width,
        int height,
        MediaCompositionStretchMode stretchMode) => MediaCompositionFrame.Empty() with
        {
            BackgroundBgra = pixels,
            BackgroundWidth = width,
            BackgroundHeight = height,
            BackgroundStride = width * 4,
            BackgroundStretchMode = stretchMode,
            BackgroundRevision = 1
        };

    private static void AssertPixel(
        SoftwareRenderOutput output,
        int x,
        int y,
        byte blue,
        byte green,
        byte red,
        byte alpha)
    {
        ReadOnlySpan<byte> pixels = output.Pixels.Span;
        var offset = (y * output.Stride) + (x * 4);
        Assert.Equal(blue, pixels[offset]);
        Assert.Equal(green, pixels[offset + 1]);
        Assert.Equal(red, pixels[offset + 2]);
        Assert.Equal(alpha, pixels[offset + 3]);
    }
}
