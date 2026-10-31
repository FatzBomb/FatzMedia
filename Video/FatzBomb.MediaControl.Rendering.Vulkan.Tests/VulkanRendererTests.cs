using FatzBomb.MediaControl.Core.Cdg;
using FatzBomb.MediaControl.Core.Graphics.Composition;
using FatzBomb.MediaControl.Core.Subtitle;
using FatzBomb.MediaControl.Rendering.Software;
using FatzBomb.MediaControl.Rendering.Text;

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
    public void Classify_AcceptsWashCdgAndSubtitles_RejectsTransformsAndInvalidLayers()
    {
        Assert.Equal(VulkanFrameSupport.Supported, VulkanCompositionPlanner.Classify(MediaCompositionFrame.Empty()));
        Assert.Equal(VulkanFrameSupport.Supported, VulkanCompositionPlanner.Classify(MediaCompositionFrame.Empty() with { WashArgb = 0x80000000 }));
        Assert.Equal(VulkanFrameSupport.Supported, VulkanCompositionPlanner.Classify(MediaCompositionFrame.Empty() with { CdgOverlay = CdgFixtures.Snapshot() }));
        Assert.Equal(VulkanFrameSupport.Supported, VulkanCompositionPlanner.Classify(SubtitleFrame()));
        Assert.Equal(VulkanFrameSupport.InvalidSubtitle, VulkanCompositionPlanner.Classify(SubtitleFrame() with { SubtitleBgra = new byte[12] }));
        Assert.Equal(
            VulkanFrameSupport.BaseVisualTransform,
            VulkanCompositionPlanner.Classify(MediaCompositionFrame.Empty() with { BaseVisualTransform = BaseVisualTransform.Identity with { Brightness = 0.5f } }));
        Assert.Equal(
            VulkanFrameSupport.InvalidCdgSnapshot,
            VulkanCompositionPlanner.Classify(MediaCompositionFrame.Empty() with { CdgOverlay = new CdgRenderSnapshot(1, 1, new byte[10], new int[16], 0, 0, []) }));
    }

    [Fact]
    public void Plan_CollectsWashAndCdgInDrawOrder()
    {
        CdgRenderSnapshot snapshot = CdgFixtures.Snapshot();
        VulkanFramePlan plan = VulkanCompositionPlanner.Plan(MediaCompositionFrame.Empty() with { WashArgb = 0x40FF0000, CdgOverlay = snapshot }, 300, 216);

        Assert.True(plan.HasDraws);
        Assert.Equal(0x40FF0000u, plan.WashArgb);
        Assert.Same(snapshot, plan.Cdg!.Value.Snapshot);
        Assert.Equal(new PixelRect(0, 0, 300, 216), plan.Cdg.Value.Quad.Destination);
        Assert.Null(plan.Background);
    }

    [Fact]
    public void Plan_SkipsTransparentWashSuppressedCdgAndNegativeBackgroundIndex()
    {
        Assert.False(VulkanCompositionPlanner.Plan(MediaCompositionFrame.Empty() with { WashArgb = 0x00FFFFFF }, 8, 8).HasDraws);
        Assert.False(VulkanCompositionPlanner.Plan(MediaCompositionFrame.Empty() with { CdgOverlay = CdgFixtures.Snapshot(), SuppressCdgOverlayDraw = true }, 8, 8).HasDraws);
        Assert.False(VulkanCompositionPlanner.Plan(MediaCompositionFrame.Empty() with { CdgOverlay = CdgFixtures.Snapshot(backgroundIndex: -1) }, 8, 8).HasDraws);
    }

    [Fact]
    public void PlanOverlay_ExactFit_UsesWholeTexture()
    {
        VulkanOverlayQuad quad = VulkanCompositionPlanner.PlanOverlay(300, 216, 600, 432, MediaCompositionStretchMode.UniformFit)!.Value;

        Assert.Equal(new PixelRect(0, 0, 600, 432), quad.Destination);
        Assert.Equal((0f, 0f, 1f, 1f), quad.TexCoords);
        Assert.Equal((0, 0), quad.GetSourcePixel(0, 1));
        Assert.Equal((299, 215), quad.GetSourcePixel(599, 431));
    }

    [Fact]
    public void PlanOverlay_FractionalFit_RoundsEdgesAwayFromZeroAndOffsetsTexCoords()
    {
        // 300x216 into 640x480: 640x461 at top 9.5, so rows 10..470 are covered.
        VulkanOverlayQuad quad = VulkanCompositionPlanner.PlanOverlay(300, 216, 640, 480, MediaCompositionStretchMode.UniformFit)!.Value;

        Assert.Equal(new PixelRect(0, 10, 640, 471), quad.Destination);
        Assert.Equal(9.5, quad.Placement.Destination.Top);
        Assert.Equal(461, quad.Placement.Destination.Height);
        Assert.Equal(0.5f / 461f, quad.TexCoords.V0, 6);
        Assert.Equal(461.5f / 461f, quad.TexCoords.V1, 6);
        Assert.Equal((0, 0), quad.GetSourcePixel(0, 10));
        Assert.Equal((299, 215), quad.GetSourcePixel(639, 470));
    }

    [Theory]
    [InlineData(300, 216)]
    [InlineData(600, 432)]
    [InlineData(640, 480)]
    [InlineData(1280, 720)]
    [InlineData(1920, 1080)]
    [InlineData(333, 250)]
    public void PlanOverlay_TexCoordsInterpolateToSoftwareSourcePixels(int width, int height)
    {
        VulkanOverlayQuad quad = VulkanCompositionPlanner.PlanOverlay(300, 216, width, height, MediaCompositionStretchMode.UniformFit)!.Value;
        (float u0, float v0, float u1, float v1) = quad.TexCoords;
        PixelRect destination = quad.Destination;

        // What a rasterizer interpolates at pixel centres across a viewport equal to Destination,
        // followed by nearest texel selection.
        for (var x = destination.Left; x < destination.Right; x++)
        {
            var u = u0 + ((u1 - u0) * ((x + 0.5f - destination.Left) / destination.Width));
            Assert.Equal(quad.GetSourcePixel(x, destination.Top).X, Math.Clamp((int)MathF.Floor(u * 300), 0, 299));
        }

        for (var y = destination.Top; y < destination.Bottom; y++)
        {
            var v = v0 + ((v1 - v0) * ((y + 0.5f - destination.Top) / destination.Height));
            Assert.Equal(quad.GetSourcePixel(destination.Left, y).Y, Math.Clamp((int)MathF.Floor(v * 216), 0, 215));
        }
    }

    [Fact]
    public void PlanOverlay_EmptyOutput_ReturnsNull() =>
        Assert.Null(VulkanCompositionPlanner.PlanOverlay(300, 216, 0, 10, MediaCompositionStretchMode.UniformFit));

    [Fact]
    public void ExpandCdg_ForcesOpaquePaletteAndClearsBackgroundAndOutOfRangeIndices()
    {
        var indices = new byte[CdgConstants.TotalPixels];
        indices[0] = 2;
        indices[1] = 3;
        indices[2] = 200;
        int[] palette = [0x00000000, 0x11111111, 0x20A0B0C0, unchecked((int)0xFF102030)];
        var snapshot = new CdgRenderSnapshot(1, 1, indices, palette, 0, backgroundColorIndex: 0, []);
        var bgra = new byte[CdgConstants.TotalPixels * 4];
        bgra.AsSpan().Fill(0x55);

        VulkanCompositionPlanner.ExpandCdg(snapshot, bgra);

        Assert.Equal(new byte[] { 0xC0, 0xB0, 0xA0, 0xFF }, bgra[0..4]);
        Assert.Equal(new byte[] { 0x30, 0x20, 0x10, 0xFF }, bgra[4..8]);
        Assert.Equal(new byte[4], bgra[8..12]);
        Assert.Equal(new byte[4], bgra[12..16]);
    }

    private static MediaCompositionFrame SubtitleFrame() => MediaCompositionFrame.Empty() with
    {
        SubtitleBgra = new byte[16],
        SubtitleWidth = 2,
        SubtitleHeight = 2,
        SubtitleStride = 8,
    };
}

/// <summary>
/// GPU conformance against the deterministic software renderer. Runs wherever a Vulkan device
/// exists (lavapipe in the Linux CI job); returns early when none is available.
/// </summary>
public sealed class VulkanConformanceTests
{
    // Fixed-function ONE / ONE_MINUS_SRC_ALPHA blending may round the source and destination
    // terms separately where the software renderer rounds their sum once. Measured on lavapipe:
    // 0 for a wash over a uniform clear, 1 over the gradient background.
    private const int WashTolerance = 1;

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
        // Moving the background onto the shader path removes this; the bound pins today's behavior.
        AssertMatchesSoftware(CreateBackgroundFrame(sourceWidth, sourceHeight, stretch), width, height, minimumExactRatio: 0.75);

    [Theory]
    [InlineData(MediaCompositionClearMode.Black, 0xFF000000u, 0x80FF8040u)]
    [InlineData(MediaCompositionClearMode.ConfiguredColor, 0xFF336699u, 0x40FFFFFFu)]
    [InlineData(MediaCompositionClearMode.ConfiguredColor, 0x80336699u, 0xC0102030u)]
    [InlineData(MediaCompositionClearMode.Transparent, 0u, 0x01FFFFFFu)]
    [InlineData(MediaCompositionClearMode.Transparent, 0u, 0xFFFF00FFu)]
    public void WashOnly_MatchesSoftware(MediaCompositionClearMode clearMode, uint clearArgb, uint washArgb) =>
        AssertMatchesSoftware(MediaCompositionFrame.Empty(clearMode, clearArgb) with { WashArgb = washArgb }, 13, 7, minimumExactRatio: 1.0, maxChannelDelta: WashTolerance);

    [Theory]
    [InlineData(0x80FF8040u)]
    [InlineData(0x33000000u)]
    [InlineData(0xE0204060u)]
    public void BackgroundAndWash_MatchesSoftware(uint washArgb) =>
        AssertMatchesSoftware(CreateBackgroundFrame(16, 9, MediaCompositionStretchMode.UniformFit) with { WashArgb = washArgb }, 64, 48, minimumExactRatio: 1.0, maxChannelDelta: WashTolerance);

    [Theory]
    [InlineData(300, 216)]
    [InlineData(600, 432)]
    [InlineData(900, 648)]
    [InlineData(640, 480)]
    [InlineData(1280, 720)]
    [InlineData(1920, 1080)]
    [InlineData(333, 250)]
    public void CdgOnly_MatchesSoftwareExactly(int width, int height) =>
        AssertMatchesSoftware(MediaCompositionFrame.Empty() with { CdgOverlay = CdgFixtures.Snapshot() }, width, height, minimumExactRatio: 1.0);

    /// <summary>
    /// A premultiplied subtitle raster on its 1920x1080 canvas, over a background and the CDG
    /// overlay, at the canvas size and scaled: the subtitle pipeline samples and blends it as the
    /// software renderer does.
    /// </summary>
    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(640, 360)]
    [InlineData(1280, 720)]
    public void SubtitleOverBackgroundAndCdg_MatchesSoftware(int width, int height) =>
        AssertMatchesSoftware(
            WithSubtitle(CreateBackgroundFrame(16, 9, MediaCompositionStretchMode.UniformFit) with { CdgOverlay = CdgFixtures.Snapshot() }),
            width,
            height,
            minimumExactRatio: 1.0,
            maxChannelDelta: WashTolerance);

    /// <summary>
    /// Text from the SkiaSharp rasterizer the Vulkan factory uses, composed as Core composes it:
    /// the GPU draws the same text pixels as the software renderer, and they are visible.
    /// </summary>
    [Fact]
    public void RasterizedSubtitleText_MatchesSoftwareAndIsVisible()
    {
        using var rasterizer = new SkiaSubtitleRasterizer();
        SubtitleRasterResult text = rasterizer.Render("Vulkan subtitle text", 1920, 1080);
        MediaCompositionFrame frame = MediaCompositionFrame.Empty() with
        {
            SubtitleBgra = text.Pixels,
            SubtitleWidth = text.Width,
            SubtitleHeight = text.Height,
            SubtitleStride = text.Stride,
            SubtitleCanvasLeft = text.CanvasLeft,
            SubtitleCanvasTop = text.CanvasTop,
            SubtitleCanvasWidth = text.CanvasWidth,
            SubtitleCanvasHeight = text.CanvasHeight,
            SubtitleRevision = 1,
        };

        // 2/3 scale keeps destination pixel centres off source texel edges (see CdgAtTexelEdgeScales).
        AssertMatchesSoftware(frame, 1280, 720, minimumExactRatio: 1.0, maxChannelDelta: WashTolerance);

        using VulkanOffscreenCompositor? compositor = VulkanOffscreenCompositor.TryCreate(out _);
        if (compositor is null)
            return;

        ReadOnlySpan<byte> pixels = compositor.Render(VulkanCompositionPlanner.Plan(frame, 1280, 720));
        var white = 0;
        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            if (pixels[offset] == 255 && pixels[offset + 1] == 255 && pixels[offset + 2] == 255)
                white++;
        }

        Assert.True(white > 200, $"{white} white text pixels");
    }

    [Fact]
    public void SubtitleRaster_IsUploadedOncePerRevision()
    {
        using VulkanOffscreenCompositor? compositor = VulkanOffscreenCompositor.TryCreate(out _);
        if (compositor is null)
            return;

        MediaCompositionFrame frame = WithSubtitle(MediaCompositionFrame.Empty());
        _ = compositor.Render(VulkanCompositionPlanner.Plan(frame, 640, 360));
        _ = compositor.Render(VulkanCompositionPlanner.Plan(frame, 320, 180));
        _ = compositor.Render(VulkanCompositionPlanner.Plan(frame, 640, 360));
        Assert.Equal(1, compositor.SubtitleUploadCount);

        _ = compositor.Render(VulkanCompositionPlanner.Plan(frame with { SubtitleRevision = 2 }, 640, 360));
        _ = compositor.Render(VulkanCompositionPlanner.Plan(frame with { SubtitleRevision = 0 }, 640, 360));
        Assert.Equal(3, compositor.SubtitleUploadCount);
    }

    /// <summary>
    /// At 1.5x and 0.5x some destination pixel centres map exactly onto a source texel edge. The
    /// software renderer then picks a side by double-precision rounding noise and the GPU by fp32
    /// interpolation, so those rows/columns may differ by one source texel - never more.
    /// </summary>
    [Theory]
    [InlineData(450, 324)]
    [InlineData(150, 108)]
    public void CdgAtTexelEdgeScales_StaysWithinOneSourceTexel(int width, int height)
    {
        using VulkanOffscreenCompositor? compositor = VulkanOffscreenCompositor.TryCreate(out _);
        if (compositor is null)
            return;

        CdgRenderSnapshot snapshot = CdgFixtures.Snapshot();
        VulkanFramePlan plan = VulkanCompositionPlanner.Plan(MediaCompositionFrame.Empty() with { CdgOverlay = snapshot }, width, height);
        ReadOnlySpan<byte> actual = compositor.Render(plan);
        var texels = new byte[snapshot.Width * snapshot.Height * 4];
        VulkanCompositionPlanner.ExpandCdg(snapshot, texels);
        VulkanOverlayQuad quad = plan.Cdg!.Value.Quad;

        for (var y = quad.Destination.Top; y < quad.Destination.Bottom; y++)
        {
            for (var x = quad.Destination.Left; x < quad.Destination.Right; x++)
            {
                (int sourceX, int sourceY) = quad.GetSourcePixel(x, y);
                ReadOnlySpan<byte> pixel = actual.Slice(((y * width) + x) * 4, 4);
                var found = false;
                for (var dy = -1; dy <= 1 && !found; dy++)
                {
                    for (var dx = -1; dx <= 1 && !found; dx++)
                    {
                        var sx = Math.Clamp(sourceX + dx, 0, snapshot.Width - 1);
                        var sy = Math.Clamp(sourceY + dy, 0, snapshot.Height - 1);
                        found = pixel.SequenceEqual(ExpectedOverBlack(texels.AsSpan(((sy * snapshot.Width) + sx) * 4, 4)));
                    }
                }

                Assert.True(found, $"pixel ({x}, {y}) is not within one source texel of ({sourceX}, {sourceY})");
            }
        }

        static byte[] ExpectedOverBlack(ReadOnlySpan<byte> texel) => texel[3] == 0 ? [0, 0, 0, 255] : texel.ToArray();
    }

    /// <summary>ENG-014: an unchanged snapshot reuses the texture; a new one is uploaded.</summary>
    [Fact]
    public void UnchangedCdgSnapshot_ReusesTheUploadedTexture()
    {
        using VulkanOffscreenCompositor? compositor = VulkanOffscreenCompositor.TryCreate(out _);
        if (compositor is null)
            return;

        CdgRenderSnapshot snapshot = CdgFixtures.Snapshot();
        VulkanFramePlan plan = VulkanCompositionPlanner.Plan(MediaCompositionFrame.Empty() with { CdgOverlay = snapshot }, 320, 240);
        var first = compositor.Render(plan).ToArray();
        var withoutCdg = VulkanCompositionPlanner.Plan(MediaCompositionFrame.Empty(), 320, 240);
        _ = compositor.Render(withoutCdg);
        var again = compositor.Render(plan).ToArray();

        Assert.Equal(1, compositor.CdgUploadCount);
        Assert.Equal(first, again);

        // Same buffers, new dirty list: what Core hands over when only the dirty regions changed.
        var sameBuffers = new CdgRenderSnapshot(2, 1, snapshot.ColorIndices, snapshot.PaletteArgb, 0, snapshot.BackgroundColorIndex, []);
        Assert.Equal(first, compositor.Render(VulkanCompositionPlanner.Plan(MediaCompositionFrame.Empty() with { CdgOverlay = sameBuffers }, 320, 240)).ToArray());
        Assert.Equal(1, compositor.CdgUploadCount);

        CdgRenderSnapshot changed = CdgFixtures.Snapshot(backgroundIndex: 1);
        var changedPixels = compositor.Render(VulkanCompositionPlanner.Plan(MediaCompositionFrame.Empty() with { CdgOverlay = changed }, 320, 240)).ToArray();

        Assert.Equal(2, compositor.CdgUploadCount);
        Assert.NotEqual(first, changedPixels);
    }

    [Fact]
    public void CdgWithNegativeBackgroundIndex_DrawsNothing() =>
        AssertMatchesSoftware(MediaCompositionFrame.Empty() with { CdgOverlay = CdgFixtures.Snapshot(backgroundIndex: -1) }, 64, 48, minimumExactRatio: 1.0);

    [Theory]
    [InlineData(640, 480)]
    [InlineData(1280, 720)]
    public void BackgroundWashAndCdg_MatchesSoftware(int width, int height) =>
        AssertMatchesSoftware(
            CreateBackgroundFrame(16, 9, MediaCompositionStretchMode.UniformFit) with { WashArgb = 0x80204060, CdgOverlay = CdgFixtures.Snapshot() },
            width,
            height,
            minimumExactRatio: 1.0,
            maxChannelDelta: WashTolerance);

    [Fact]
    public void Factory_ProbeMatchesSupportAndAsksForFrameSinkAndSoftwareDecoding()
    {
        var supported = VulkanPresentationCoordinatorFactory.IsSupported(out _);

        Controllers.MediaPresentationAvailability availability = new VulkanPresentationCoordinatorFactory().ProbeAvailability();

        Assert.Equal(supported, availability.IsAvailable);
        Assert.False(string.IsNullOrWhiteSpace(availability.Detail));
        Assert.True(availability.RequiresFrameSinkSurface);
        Assert.True(availability.RequiresSoftwareDecoding);
    }

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
        Assert.True(backend.PresentComposition(MediaCompositionFrame.Empty() with { WashArgb = 0x80FFFFFF, CdgOverlay = CdgFixtures.Snapshot() }));
        Assert.Equal((10, 2), (sink.Width, sink.Height));
        Assert.Equal(2, presented);

        Assert.True(backend.PresentComposition(MediaCompositionFrame.Empty() with
        {
            SubtitleBgra = new byte[16],
            SubtitleWidth = 2,
            SubtitleHeight = 2,
            SubtitleStride = 8,
        }));
        Assert.False(backend.PresentComposition(MediaCompositionFrame.Empty() with
        {
            SubtitleBgra = new byte[12],
            SubtitleWidth = 2,
            SubtitleHeight = 2,
            SubtitleStride = 8,
        }));
        Assert.True(backend.DetachSurface(surface));
        Assert.False(backend.PresentComposition(MediaCompositionFrame.Empty()));
    }

    [Fact]
    public void Compositor_ResizesBetweenOverlayFrames()
    {
        using VulkanOffscreenCompositor? compositor = VulkanOffscreenCompositor.TryCreate(out _);
        if (compositor is null)
            return;

        MediaCompositionFrame frame = MediaCompositionFrame.Empty() with { WashArgb = 0x80FF0000, CdgOverlay = CdgFixtures.Snapshot() };
        foreach ((int width, int height) in new[] { (64, 48), (300, 216), (17, 5), (64, 48) })
        {
            ReadOnlySpan<byte> pixels = compositor.Render(VulkanCompositionPlanner.Plan(frame, width, height));
            Assert.Equal(width * height * 4, pixels.Length);
        }
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

    [Fact]
    public void Classify_RejectsABackgroundBufferShorterThanItsRows()
    {
        MediaCompositionFrame frame = CreateBackgroundFrame(4, 3, MediaCompositionStretchMode.Fill);

        Assert.Equal(VulkanFrameSupport.Supported, VulkanCompositionPlanner.Classify(frame));
        Assert.Equal(VulkanFrameSupport.InvalidBackground, VulkanCompositionPlanner.Classify(frame with { BackgroundBgra = new byte[(4 * 4 * 3) - 1] }));
        Assert.Equal(VulkanFrameSupport.InvalidBackground, VulkanCompositionPlanner.Classify(frame with { BackgroundStride = 15 }));
    }

    /// <summary>
    /// ENG-039 G7: CD+G before its first memory preset (no background colour index) is transparent
    /// in every renderer. The same snapshot and expected pixel are used by the software and
    /// Direct3D11 tests: every index 1 (opaque red), clear colour opaque blue, pixel (0, 0) blue.
    /// </summary>
    [Fact]
    public void CdgBeforeMemoryPreset_ShowsTheClearColour()
    {
        using VulkanOffscreenCompositor? compositor = VulkanOffscreenCompositor.TryCreate(out _);
        if (compositor is null)
            return;

        var indices = new byte[300 * 216];
        Array.Fill(indices, (byte)1);
        var snapshot = new CdgRenderSnapshot(1, 1, indices, new[] { unchecked((int)0xFF000000), unchecked((int)0xFFFF0000) }, 0, -1, []);
        MediaCompositionFrame frame = MediaCompositionFrame.Empty(MediaCompositionClearMode.ConfiguredColor, 0xFF0000FF) with { CdgOverlay = snapshot };

        ReadOnlySpan<byte> pixels = compositor.Render(VulkanCompositionPlanner.Plan(frame, 300, 216));

        Assert.Equal(new byte[] { 255, 0, 0, 255 }, pixels[..4].ToArray());
    }

    /// <summary>
    /// Renders <paramref name="frame"/> with both renderers. A pixel matches when every channel is
    /// within <paramref name="maxChannelDelta"/>; at least <paramref name="minimumExactRatio"/> of
    /// the pixels must match.
    /// </summary>
    private static void AssertMatchesSoftware(MediaCompositionFrame frame, int width, int height, double minimumExactRatio, int maxChannelDelta = 0)
    {
        using VulkanOffscreenCompositor? compositor = VulkanOffscreenCompositor.TryCreate(out _);
        if (compositor is null)
            return;

        SoftwareRenderOutput expected = new SoftwareCompositionRenderer().Render(frame, width, height);
        Assert.True(expected.IsRendered, expected.Message);
        Assert.Equal(VulkanFrameSupport.Supported, VulkanCompositionPlanner.Classify(frame));
        ReadOnlySpan<byte> actual = compositor.Render(VulkanCompositionPlanner.Plan(frame, width, height));

        ReadOnlySpan<byte> expectedPixels = expected.Pixels.Span;
        Assert.Equal(expectedPixels.Length, actual.Length);
        var matching = 0;
        var largestDelta = 0;
        for (var pixel = 0; pixel < width * height; pixel++)
        {
            var pixelDelta = 0;
            for (var channel = pixel * 4; channel < (pixel * 4) + 4; channel++)
                pixelDelta = Math.Max(pixelDelta, Math.Abs(expectedPixels[channel] - actual[channel]));
            largestDelta = Math.Max(largestDelta, pixelDelta);
            if (pixelDelta <= maxChannelDelta)
                matching++;
        }

        var ratio = matching / (double)(width * height);
        Assert.True(
            ratio >= minimumExactRatio,
            $"{ratio:P1} of pixels match within {maxChannelDelta} on {compositor.DeviceName} (required {minimumExactRatio:P0}, largest channel delta {largestDelta})");
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

    // A premultiplied raster with every alpha level, padded rows, placed on a 1920x1080 canvas as
    // the subtitle rasterizers place text.
    private static MediaCompositionFrame WithSubtitle(MediaCompositionFrame frame)
    {
        const int Width = 301;
        const int Height = 83;
        const int Stride = (Width * 4) + 16;
        var pixels = new byte[Stride * Height];
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                var offset = (y * Stride) + (x * 4);
                var alpha = (byte)((x + (y * 7)) % 256);
                pixels[offset] = (byte)(alpha * (x % 5) / 4);
                pixels[offset + 1] = (byte)(alpha * (y % 3) / 2);
                pixels[offset + 2] = alpha;
                pixels[offset + 3] = alpha;
            }
        }

        return frame with
        {
            SubtitleBgra = pixels,
            SubtitleWidth = Width,
            SubtitleHeight = Height,
            SubtitleStride = Stride,
            SubtitleCanvasLeft = 810,
            SubtitleCanvasTop = 901,
            SubtitleCanvasWidth = 1920,
            SubtitleCanvasHeight = 1080,
            SubtitleRevision = 1,
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

internal static class CdgFixtures
{
    /// <summary>
    /// Full-size CDG snapshot whose neighbouring source pixels always differ, so any sampling
    /// offset shows up. Indices 16 and 17 lie past the 16-entry palette (drawn transparent), and
    /// palette alpha varies to prove it is ignored.
    /// </summary>
    public static CdgRenderSnapshot Snapshot(int backgroundIndex = 0)
    {
        var indices = new byte[CdgConstants.TotalPixels];
        for (var y = 0; y < CdgConstants.FrameHeight; y++)
        {
            for (var x = 0; x < CdgConstants.FrameWidth; x++)
                indices[(y * CdgConstants.FrameWidth) + x] = (byte)(((x * 3) + (y * 5)) % 18);
        }

        var palette = new int[16];
        for (var index = 0; index < palette.Length; index++)
            palette[index] = unchecked((int)(((uint)(index * 16) << 24) | ((uint)(index * 17) << 16) | ((uint)(255 - (index * 13)) << 8) | (uint)((index * 71) & 0xFF)));

        return new CdgRenderSnapshot(1, 1, indices, palette, 0, backgroundIndex, []);
    }
}
