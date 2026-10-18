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
    public void Render_PublicOverload_ReturnsIndependentOutputs()
    {
        SoftwareRenderOutput first = _renderer.Render(MediaCompositionFrame.Empty(), 2, 1);
        SoftwareRenderOutput second = _renderer.Render(MediaCompositionFrame.Empty(MediaCompositionClearMode.Transparent), 2, 1);

        Assert.Equal(new byte[] { 0, 0, 0, 255, 0, 0, 0, 255 }, first.Pixels.ToArray());
        Assert.Equal(new byte[8], second.Pixels.ToArray());
    }

    [Fact]
    public void Render_ReusableBuffer_IsReusedAtTheSameSizeAndFullyRedrawn()
    {
        // Stage 022A slice 2: the backend reuses one buffer instead of allocating per frame.
        SoftwareCompositionBuffer? buffer = null;
        SoftwareRenderOutput black = _renderer.Render(MediaCompositionFrame.Empty(), 2, 1, ref buffer);
        SoftwareCompositionBuffer firstBuffer = Assert.IsType<SoftwareCompositionBuffer>(buffer);
        Assert.Equal(new byte[] { 0, 0, 0, 255, 0, 0, 0, 255 }, black.Pixels.ToArray());

        SoftwareRenderOutput transparent = _renderer.Render(MediaCompositionFrame.Empty(MediaCompositionClearMode.Transparent), 2, 1, ref buffer);

        Assert.Same(firstBuffer, buffer);
        Assert.Equal(new byte[8], transparent.Pixels.ToArray());

        _ = _renderer.Render(MediaCompositionFrame.Empty(), 3, 1, ref buffer);
        Assert.NotSame(firstBuffer, buffer);
        Assert.Equal(3, buffer!.Width);
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
    public void Render_IdentityTransform_IsByteIdentical()
    {
        MediaCompositionFrame frame = CreateBackgroundFrame(
            [10, 20, 30, 128],
            width: 1,
            height: 1,
            MediaCompositionStretchMode.Fill);

        SoftwareRenderOutput original = _renderer.Render(frame, 1, 1);
        SoftwareRenderOutput identity = _renderer.Render(frame with { BaseVisualTransform = BaseVisualTransform.Identity }, 1, 1);

        Assert.Equal(original.Pixels.ToArray(), identity.Pixels.ToArray());
    }

    [Fact]
    public void Render_RgbGainsTransformOnlyBackgroundAndPreserveAlpha()
    {
        MediaCompositionFrame frame = CreateBackgroundFrame(
            [255, 255, 255, 128],
            width: 1,
            height: 1,
            MediaCompositionStretchMode.Fill) with
        {
            ClearMode = MediaCompositionClearMode.Transparent,
            BaseVisualTransform = new BaseVisualTransform
            {
                GreenGain = 0f,
                BlueGain = 0f
            }
        };

        SoftwareRenderOutput output = _renderer.Render(frame, 1, 1);

        AssertPixel(output, 0, 0, blue: 0, green: 0, red: 128, alpha: 128);
    }

    [Fact]
    public void Render_TransformDoesNotReachSubtitle()
    {
        MediaCompositionFrame frame = CreateBackgroundFrame(
            [255, 255, 255, 255],
            width: 1,
            height: 1,
            MediaCompositionStretchMode.Fill) with
        {
            BaseVisualTransform = new BaseVisualTransform
            {
                GreenGain = 0f,
                BlueGain = 0f
            },
            SubtitleBgra = new byte[] { 0, 255, 0, 255 },
            SubtitleWidth = 1,
            SubtitleHeight = 1,
            SubtitleStride = 4
        };

        SoftwareRenderOutput output = _renderer.Render(frame, 1, 1);

        AssertPixel(output, 0, 0, blue: 0, green: 255, red: 0, alpha: 255);
    }

    [Fact]
    public void Render_HueUsesPinnedYiqRotation()
    {
        MediaCompositionFrame frame = CreateTransformedPixelFrame(
            blue: 0,
            green: 0,
            red: 255,
            new BaseVisualTransform { HueDegrees = 90f });

        AssertPixel(_renderer.Render(frame, 1, 1), 0, 0, blue: 255, green: 0, red: 119, alpha: 255);
    }

    [Fact]
    public void Render_SaturationUsesPinnedRec709Luma()
    {
        MediaCompositionFrame frame = CreateTransformedPixelFrame(
            blue: 0,
            green: 0,
            red: 255,
            new BaseVisualTransform { Saturation = 0f });

        AssertPixel(_renderer.Render(frame, 1, 1), 0, 0, blue: 54, green: 54, red: 54, alpha: 255);
    }

    [Fact]
    public void Render_RgbGainsIndependentlyRemoveChannels()
    {
        MediaCompositionFrame frame = CreateTransformedPixelFrame(
            blue: 255,
            green: 128,
            red: 64,
            new BaseVisualTransform
            {
                RedGain = 0f
            });

        AssertPixel(_renderer.Render(frame, 1, 1), 0, 0, blue: 255, green: 128, red: 0, alpha: 255);
    }

    [Fact]
    public void Render_IncludeOverlaysTransformsCompletedComposition()
    {
        MediaCompositionFrame frame = CreateBackgroundFrame(
            [255, 255, 255, 255],
            width: 1,
            height: 1,
            MediaCompositionStretchMode.Fill) with
        {
            BaseVisualTransform = new BaseVisualTransform
            {
                GreenGain = 0f,
                BlueGain = 0f,
                IncludeOverlays = true
            },
            SubtitleBgra = new byte[] { 0, 255, 0, 255 },
            SubtitleWidth = 1,
            SubtitleHeight = 1,
            SubtitleStride = 4
        };

        AssertPixel(_renderer.Render(frame, 1, 1), 0, 0, blue: 0, green: 0, red: 0, alpha: 255);
    }

    [Fact]
    public void Render_IncludeOverlaysDoesNotDoubleApplyBackgroundTransform()
    {
        MediaCompositionFrame frame = CreateTransformedPixelFrame(
            blue: 0,
            green: 0,
            red: 128,
            new BaseVisualTransform { RedGain = 0.5f, IncludeOverlays = true });

        AssertPixel(_renderer.Render(frame, 1, 1), 0, 0, blue: 0, green: 0, red: 64, alpha: 255);
    }

    [Fact]
    public void Render_IncludeOverlaysWithNeutralMathIsByteIdentical()
    {
        MediaCompositionFrame frame = CreateBackgroundFrame(
            [10, 20, 30, 128],
            width: 1,
            height: 1,
            MediaCompositionStretchMode.Fill);

        SoftwareRenderOutput original = _renderer.Render(frame, 1, 1);
        SoftwareRenderOutput scopedIdentity = _renderer.Render(
            frame with { BaseVisualTransform = new BaseVisualTransform { IncludeOverlays = true } },
            1,
            1);

        Assert.Equal(original.Pixels.ToArray(), scopedIdentity.Pixels.ToArray());
    }

    [Fact]
    public void Render_CombinedTransformUsesFixedOrder()
    {
        MediaCompositionFrame frame = CreateTransformedPixelFrame(
            blue: 153,
            green: 102,
            red: 51,
            new BaseVisualTransform
            {
                Brightness = 0.1f,
                Contrast = 1.5f,
                RedGain = 0.5f,
                GreenGain = 1.25f,
                BlueGain = 0f
            });

        AssertPixel(_renderer.Render(frame, 1, 1), 0, 0, blue: 0, green: 223, red: 51, alpha: 255);
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

    private static MediaCompositionFrame CreateTransformedPixelFrame(
        byte blue,
        byte green,
        byte red,
        BaseVisualTransform transform) => CreateBackgroundFrame(
            [blue, green, red, 255],
            width: 1,
            height: 1,
            MediaCompositionStretchMode.Fill) with
        {
            BaseVisualTransform = transform
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
