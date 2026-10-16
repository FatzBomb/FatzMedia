using FatzBomb.MediaControl.Core.Graphics.Composition;
using FatzBomb.MediaControl.Rendering.Software;

namespace FatzBomb.MediaControl.Rendering.Vulkan.Tests;

public sealed class VulkanCompositionPlannerTests
{
    [Fact]
    public void PlanBackground_UniformFit_Letterboxes()
    {
        VulkanBlit? blit = VulkanCompositionPlanner.PlanBackground(4, 2, 8, 8, MediaCompositionStretchMode.UniformFit);

        Assert.Equal(new PixelRect(0, 0, 4, 2), blit!.Value.Source);
        Assert.Equal(new PixelRect(0, 2, 8, 6), blit.Value.Destination);
    }

    [Fact]
    public void PlanBackground_Fill_CoversOutput()
    {
        VulkanBlit? blit = VulkanCompositionPlanner.PlanBackground(3, 5, 7, 11, MediaCompositionStretchMode.Fill);

        Assert.Equal(new PixelRect(0, 0, 3, 5), blit!.Value.Source);
        Assert.Equal(new PixelRect(0, 0, 7, 11), blit.Value.Destination);
    }

    [Fact]
    public void PlanBackground_UniformToFill_CropsSourceProportionally()
    {
        VulkanBlit? blit = VulkanCompositionPlanner.PlanBackground(8, 4, 4, 4, MediaCompositionStretchMode.UniformToFill);

        Assert.Equal(new PixelRect(0, 0, 4, 4), blit!.Value.Destination);
        Assert.Equal(new PixelRect(2, 0, 6, 4), blit.Value.Source);
    }

    [Theory]
    [InlineData(0, 2, 8, 8)]
    [InlineData(2, 2, 0, 8)]
    public void PlanBackground_EmptyInputs_ReturnNull(int sw, int sh, int ow, int oh) =>
        Assert.Null(VulkanCompositionPlanner.PlanBackground(sw, sh, ow, oh, MediaCompositionStretchMode.UniformFit));

    [Fact]
    public void ResolveClear_MatchesSoftwarePolicyAndPremultiplies()
    {
        Assert.Equal(((byte)0, (byte)0, (byte)0, (byte)255), VulkanCompositionPlanner.ResolveClear(MediaCompositionFrame.Empty()));
        Assert.Equal(((byte)0, (byte)0, (byte)0, (byte)0), VulkanCompositionPlanner.ResolveClear(MediaCompositionFrame.Empty(MediaCompositionClearMode.Transparent, 0xFFFFFFFF)));
        Assert.Equal(((byte)0, (byte)0, (byte)128, (byte)128), VulkanCompositionPlanner.ResolveClear(MediaCompositionFrame.Empty(MediaCompositionClearMode.ConfiguredColor, 0x80FF0000)));
    }

    [Fact]
    public void Classify_RejectsOverlaysAndTransformsInThisSlice()
    {
        Assert.Equal(VulkanFrameSupport.Supported, VulkanCompositionPlanner.Classify(MediaCompositionFrame.Empty()));
        Assert.Equal(VulkanFrameSupport.Overlays, VulkanCompositionPlanner.Classify(MediaCompositionFrame.Empty() with { WashArgb = 0x80000000 }));
        Assert.Equal(
            VulkanFrameSupport.BaseVisualTransform,
            VulkanCompositionPlanner.Classify(MediaCompositionFrame.Empty() with { BaseVisualTransform = BaseVisualTransform.Identity with { Brightness = 0.5f } }));
    }
}

/// <summary>
/// GPU conformance against the deterministic software renderer. Runs wherever a Vulkan device
/// exists (lavapipe in the Linux CI job); returns early when none is available.
/// </summary>
public sealed class VulkanConformanceTests
{
    [Fact]
    public void ClearOnly_MatchesSoftware()
    {
        MediaCompositionFrame frame = MediaCompositionFrame.Empty(MediaCompositionClearMode.ConfiguredColor, 0xFF336699);
        AssertMatchesSoftware(frame, 9, 5, minimumExactRatio: 1.0);
    }

    [Theory]
    [InlineData(MediaCompositionStretchMode.UniformFit, 4, 2, 8, 8)]
    [InlineData(MediaCompositionStretchMode.Fill, 3, 5, 7, 11)]
    [InlineData(MediaCompositionStretchMode.Original, 4, 4, 8, 8)]
    [InlineData(MediaCompositionStretchMode.UniformFit, 16, 9, 64, 36)]
    public void IntegralBackgroundPlacement_MatchesSoftwareExactly(MediaCompositionStretchMode stretch, int sourceWidth, int sourceHeight, int width, int height) =>
        AssertMatchesSoftware(CreateBackgroundFrame(sourceWidth, sourceHeight, stretch), width, height, minimumExactRatio: 1.0);

    [Theory]
    [InlineData(MediaCompositionStretchMode.UniformToFill, 17, 9, 20, 20)]
    [InlineData(MediaCompositionStretchMode.UniformFit, 17, 9, 20, 20)]
    public void FractionalPlacement_MatchesSoftwareWithinEdgeTolerance(MediaCompositionStretchMode stretch, int sourceWidth, int sourceHeight, int width, int height) =>
        // vkCmdBlitImage takes integer destination edges while the software renderer samples from
        // the fractional destination rectangle, so some rows/columns map one source pixel apart.
        // Shader sampling (next slice) removes this; the bound pins today's behavior.
        AssertMatchesSoftware(CreateBackgroundFrame(sourceWidth, sourceHeight, stretch), width, height, minimumExactRatio: 0.75);

    [Fact]
    public void Backend_PresentsThroughManagedFrameSinkAndResizes()
    {
        if (!VulkanPresentationCoordinatorFactory.IsSupported(out _))
            return;

        var sink = new RecordingFrameSink();
        using var handle = new MediaPresentationFrameSinkHandle(sink);
        using var backend = new VulkanRendererBackend(() => VulkanOffscreenCompositor.TryCreate(out _));
        var surface = new MediaPresentationSurfaceDescriptor(
            MediaPresentationSurfaceDescriptor.PrimarySurfaceId,
            MediaPresentationSurfaceRole.Primary,
            handle.CreateSurfaceDescriptor(),
            6,
            4);
        var presented = 0;
        backend.FramePresented += (_, _) => presented++;

        Assert.True(backend.AttachSurface(surface));
        Assert.True(backend.Initialize());
        Assert.True(backend.PresentComposition(CreateBackgroundFrame(3, 2, MediaCompositionStretchMode.Fill)));
        Assert.Equal((6, 4, 24), (sink.Width, sink.Height, sink.Stride));

        Assert.True(backend.ResizeSurface(surface.WithSize(10, 2)));
        Assert.True(backend.PresentComposition(MediaCompositionFrame.Empty()));
        Assert.Equal((10, 2), (sink.Width, sink.Height));
        Assert.Equal(2, presented);

        Assert.False(backend.PresentComposition(MediaCompositionFrame.Empty() with { WashArgb = 0x80FFFFFF }));
        Assert.True(backend.DetachSurface(surface));
        Assert.False(backend.PresentComposition(MediaCompositionFrame.Empty()));
    }

    [Fact]
    public void Backend_WithoutDevice_FailsInitialization()
    {
        using var backend = new VulkanRendererBackend(() => null);

        Assert.False(backend.Initialize());
    }

    [Fact]
    public void Backend_WithoutDevice_TriesOnceUntilCleanup()
    {
        var attempts = 0;
        using var backend = new VulkanRendererBackend(() =>
        {
            attempts++;
            return null;
        });

        Assert.False(backend.Initialize());
        Assert.False(backend.Initialize());
        Assert.False(backend.Initialize());
        Assert.Equal(1, attempts);

        backend.CleanupResources();
        Assert.False(backend.Initialize());
        Assert.Equal(2, attempts);
    }

    private static void AssertMatchesSoftware(MediaCompositionFrame frame, int width, int height, double minimumExactRatio)
    {
        using VulkanOffscreenCompositor? compositor = VulkanOffscreenCompositor.TryCreate(out _);
        if (compositor is null)
            return;

        SoftwareRenderOutput expected = new SoftwareCompositionRenderer().Render(frame, width, height);
        Assert.True(expected.IsRendered, expected.Message);
        VulkanBlit? blit = frame.HasBackground
            ? VulkanCompositionPlanner.PlanBackground(frame.BackgroundWidth, frame.BackgroundHeight, width, height, frame.EffectiveBackgroundStretchMode)
            : null;
        ReadOnlySpan<byte> actual = compositor.Render(
            width,
            height,
            VulkanCompositionPlanner.ResolveClear(frame),
            frame.BackgroundBgra.Span,
            frame.BackgroundWidth,
            frame.BackgroundHeight,
            frame.BackgroundStride,
            blit);

        ReadOnlySpan<byte> expectedPixels = expected.Pixels.Span;
        Assert.Equal(expectedPixels.Length, actual.Length);
        var exact = 0;
        for (var pixel = 0; pixel < width * height; pixel++)
        {
            if (expectedPixels.Slice(pixel * 4, 4).SequenceEqual(actual.Slice(pixel * 4, 4)))
                exact++;
        }

        var ratio = exact / (double)(width * height);
        Assert.True(ratio >= minimumExactRatio, $"{ratio:P1} of pixels match on {compositor.DeviceName} (required {minimumExactRatio:P0})");
    }

    private static MediaCompositionFrame CreateBackgroundFrame(int width, int height, MediaCompositionStretchMode stretch)
    {
        var stride = width * 4;
        var pixels = new byte[stride * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var offset = (y * stride) + (x * 4);
                pixels[offset] = (byte)(x * 37);
                pixels[offset + 1] = (byte)(y * 53);
                pixels[offset + 2] = (byte)((x + y) * 29);
                pixels[offset + 3] = 255;
            }
        }

        return MediaCompositionFrame.Empty() with
        {
            BackgroundBgra = pixels,
            BackgroundWidth = width,
            BackgroundHeight = height,
            BackgroundStride = stride,
            BackgroundStretchMode = stretch,
        };
    }

    private sealed class RecordingFrameSink : IMediaPresentationFrameSink
    {
        public int Width { get; private set; }
        public int Height { get; private set; }
        public int Stride { get; private set; }

        public void PresentFrame(ReadOnlySpan<byte> bgraPixels, int width, int height, int stride) =>
            (Width, Height, Stride) = (width, height, stride);
    }
}
