using FatzBomb.MediaControl.Core.Cdg;
using FatzBomb.MediaControl.Core.Graphics;
using FatzBomb.MediaControl.Core.Graphics.Composition;
using FatzBomb.MediaControl.Core.Graphics.Geometry;

namespace FatzBomb.MediaControl.Rendering.Vulkan;

/// <summary>What the current Vulkan slice can draw for a composition frame.</summary>
internal enum VulkanFrameSupport
{
    Supported,
    NativeOrRetainedBackground,
    InvalidSubtitle,
    BaseVisualTransform,
    InvalidCdgSnapshot,
    InvalidBackground,
}

/// <summary>
/// Background layer and its blit: software BGRA pixels, or (Stage 045A slice 2) a hardware-decoded
/// frame (<see cref="Native"/>) converted into the video layer, or the video layer kept from the
/// last such frame (<see cref="Retained"/>). <see cref="Revision"/> is the frame's background
/// revision (Core bumps it whenever the pixels change); -1 is unknown, and always uploads.
/// </summary>
internal readonly record struct VulkanBackgroundLayer(
    ReadOnlyMemory<byte> Bgra,
    int Width,
    int Height,
    int Stride,
    VulkanBlit Blit,
    long Revision = -1,
    NativeVideoFrameResource? Native = null,
    bool Retained = false)
{
    /// <summary>Gets whether the layer is drawn from the video layer instead of uploaded pixels.</summary>
    public bool IsVideo => Native is not null || Retained;
}

/// <summary>
/// A nearest-sampled overlay drawn as a textured quad over <see cref="Destination"/>, with the
/// software renderer's sampling (Core <see cref="NearestSampledPlacement"/>).
/// </summary>
internal readonly record struct VulkanOverlayQuad(NearestSampledPlacement Placement)
{
    /// <summary>The clipped integer rectangle that is drawn.</summary>
    public PixelRect Destination => new(Placement.ClippedLeft, Placement.ClippedTop, Placement.ClippedRight, Placement.ClippedBottom);

    /// <summary>
    /// Normalized texture coordinates at the edges of <see cref="Destination"/>. Interpolated
    /// across a viewport equal to <see cref="Destination"/>, pixel centre x + 0.5 receives
    /// ((x + 0.5) - left) / width, so nearest sampling picks <see cref="GetSourcePixel"/>.
    /// </summary>
    public (float U0, float V0, float U1, float V1) TexCoords => (
        (float)((Placement.ClippedLeft - Placement.Destination.Left) / Placement.SamplingWidth),
        (float)((Placement.ClippedTop - Placement.Destination.Top) / Placement.SamplingHeight),
        (float)((Placement.ClippedRight - Placement.Destination.Left) / Placement.SamplingWidth),
        (float)((Placement.ClippedBottom - Placement.Destination.Top) / Placement.SamplingHeight));

    /// <summary>The source pixel the software renderer samples for a destination pixel.</summary>
    public (int X, int Y) GetSourcePixel(int destinationX, int destinationY) =>
        (Placement.GetSourceX(destinationX), Placement.GetSourceY(destinationY));
}

/// <summary>CDG overlay snapshot and where it lands on the output.</summary>
internal readonly record struct VulkanCdgLayer(CdgRenderSnapshot Snapshot, VulkanOverlayQuad Quad);

/// <summary>
/// Rasterized subtitle text (premultiplied BGRA rows) and where it lands on the output.
/// <see cref="Revision"/> is the frame's subtitle revision (Core bumps it for every new raster);
/// zero or less is unknown, and always uploads.
/// </summary>
internal readonly record struct VulkanSubtitleLayer(
    ReadOnlyMemory<byte> Bgra,
    int Width,
    int Height,
    int Stride,
    long Revision,
    VulkanOverlayQuad Quad);

/// <summary>Everything the compositor draws for one frame, in software-renderer order.</summary>
internal readonly record struct VulkanFramePlan(
    int OutputWidth,
    int OutputHeight,
    (byte B, byte G, byte R, byte A) ClearBgraPremultiplied,
    VulkanBackgroundLayer? Background,
    uint? WashArgb,
    VulkanCdgLayer? Cdg,
    VulkanSubtitleLayer? Subtitle = null)
{
    /// <summary>Gets whether the frame needs the graphics pipeline after the clear and blit.</summary>
    public bool HasDraws => WashArgb is not null || Cdg is not null || Subtitle is not null;
}

/// <summary>
/// Pure planning shared with the software renderer's conventions: clear colour resolution and
/// premultiplication, background destination rectangle (Core <see cref="ViewportGeometry"/>),
/// clipping of off-target destinations with a proportional source crop, overlay sampling
/// placement, and CDG palette expansion.
/// </summary>
internal static class VulkanCompositionPlanner
{
    public static VulkanFrameSupport Classify(in MediaCompositionFrame frame)
    {
        // Hardware frames on a Vulkan device, and the video layer kept from them, are drawn
        // (Stage 045A slice 2); another API's frames are not.
        if (frame.HasNativeBackground && frame.BackgroundNativeResource!.BackendId != MediaGraphicsBackendId.Vulkan)
            return VulkanFrameSupport.NativeOrRetainedBackground;
        // The software renderer refuses the same subtitle buffer (the upload would read past it).
        if (!frame.SubtitleBgra.IsEmpty
            && (frame.SubtitleWidth <= 0
                || frame.SubtitleHeight <= 0
                || frame.SubtitleStride < (long)frame.SubtitleWidth * 4
                || frame.SubtitleBgra.Length < (long)frame.SubtitleStride * frame.SubtitleHeight))
        {
            return VulkanFrameSupport.InvalidSubtitle;
        }

        if (!frame.BaseVisualTransform.IsIdentity)
            return VulkanFrameSupport.BaseVisualTransform;
        // The software renderer refuses the whole frame for the same background buffer (ENG-039:
        // the upload would read past it) and the same snapshot.
        if (!frame.IsBackgroundWellFormed)
            return VulkanFrameSupport.InvalidBackground;
        return frame.CdgOverlay is { IsWellFormed: false }
            ? VulkanFrameSupport.InvalidCdgSnapshot
            : VulkanFrameSupport.Supported;
    }

    /// <summary>Plans a frame that <see cref="Classify"/> reported as supported.</summary>
    public static VulkanFramePlan Plan(in MediaCompositionFrame frame, int outputWidth, int outputHeight)
    {
        VulkanBackgroundLayer? background = null;
        if (frame.HasNativeBackground)
        {
            if (PlanBackground(frame.BackgroundNativeWidth, frame.BackgroundNativeHeight, outputWidth, outputHeight, frame.EffectiveBackgroundStretchMode) is { } nativeBlit)
            {
                background = new VulkanBackgroundLayer(
                    ReadOnlyMemory<byte>.Empty,
                    frame.BackgroundNativeWidth,
                    frame.BackgroundNativeHeight,
                    frame.BackgroundNativeWidth * 4,
                    nativeBlit,
                    frame.BackgroundRevision,
                    Native: frame.BackgroundNativeResource);
            }
        }
        else if (frame.HasBackground
            && PlanBackground(frame.BackgroundWidth, frame.BackgroundHeight, outputWidth, outputHeight, frame.EffectiveBackgroundStretchMode) is { } blit)
        {
            background = new VulkanBackgroundLayer(frame.BackgroundBgra, frame.BackgroundWidth, frame.BackgroundHeight, frame.BackgroundStride, blit, frame.BackgroundRevision);
        }
        else if (frame.HasRetainedBackground
            && PlanBackground(frame.BackgroundWidth, frame.BackgroundHeight, outputWidth, outputHeight, frame.EffectiveBackgroundStretchMode) is { } retainedBlit)
        {
            background = new VulkanBackgroundLayer(
                ReadOnlyMemory<byte>.Empty,
                frame.BackgroundWidth,
                frame.BackgroundHeight,
                frame.BackgroundWidth * 4,
                retainedBlit,
                frame.BackgroundRevision,
                Retained: true);
        }

        VulkanCdgLayer? cdg = null;
        if (frame is { HasCdgOverlay: true, SuppressCdgOverlayDraw: false, CdgOverlay: { BackgroundColorIndex: >= 0 } snapshot }
            && PlanOverlay(snapshot.Width, snapshot.Height, outputWidth, outputHeight, MediaCompositionStretchMode.UniformFit) is { } quad)
        {
            cdg = new VulkanCdgLayer(snapshot, quad);
        }

        // Placed as the software renderer places it: on its canvas, or fitted when it has none.
        VulkanSubtitleLayer? subtitle = null;
        if (frame.HasSubtitleOverlay)
        {
            NearestSampledPlacement placement = frame.SubtitleCanvasWidth > 0 && frame.SubtitleCanvasHeight > 0
                ? NearestSampledPlacement.CreateCanvasPlacement(
                    frame.SubtitleWidth,
                    frame.SubtitleHeight,
                    frame.SubtitleCanvasLeft,
                    frame.SubtitleCanvasTop,
                    frame.SubtitleCanvasWidth,
                    frame.SubtitleCanvasHeight,
                    outputWidth,
                    outputHeight)
                : NearestSampledPlacement.Create(frame.SubtitleWidth, frame.SubtitleHeight, outputWidth, outputHeight, MediaCompositionStretchMode.UniformFit);
            if (!placement.IsEmpty)
            {
                subtitle = new VulkanSubtitleLayer(
                    frame.SubtitleBgra,
                    frame.SubtitleWidth,
                    frame.SubtitleHeight,
                    frame.SubtitleStride,
                    frame.SubtitleRevision,
                    new VulkanOverlayQuad(placement));
            }
        }

        return new VulkanFramePlan(
            outputWidth,
            outputHeight,
            ResolveClear(frame),
            background,
            frame.HasWash ? frame.WashArgb : null,
            cdg,
            subtitle);
    }

    /// <summary>Same clear policy as the software renderer, returned premultiplied.</summary>
    public static (byte B, byte G, byte R, byte A) ResolveClear(in MediaCompositionFrame frame)
    {
        var bgra = ColorMath.PremultiplyBgra(frame.ResolveClearArgb());
        return ((byte)bgra, (byte)(bgra >> 8), (byte)(bgra >> 16), (byte)(bgra >> 24));
    }

    /// <summary>
    /// Plans the background blit, or <see langword="null"/> when nothing of the source lands on
    /// the output. Destination edges round away from zero like the software renderer.
    /// </summary>
    public static VulkanBlit? PlanBackground(int sourceWidth, int sourceHeight, int outputWidth, int outputHeight, MediaCompositionStretchMode stretchMode)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0 || outputWidth <= 0 || outputHeight <= 0)
            return null;

        var placement = NearestSampledPlacement.Create(sourceWidth, sourceHeight, outputWidth, outputHeight, stretchMode);
        RectD destination = placement.Destination;
        if (destination.Width <= 0 || destination.Height <= 0 || placement.IsEmpty)
            return null;

        // Map the clipped destination back into source space (only differs from the full
        // source when the destination extends past the output, e.g. UniformToFill).
        double ToSourceX(int x) => (x - destination.X) / destination.Width * sourceWidth;
        double ToSourceY(int y) => (y - destination.Y) / destination.Height * sourceHeight;
        var sourceLeft = Math.Clamp(NearestSampledPlacement.RoundEdge(ToSourceX(placement.ClippedLeft)), 0, sourceWidth);
        var sourceTop = Math.Clamp(NearestSampledPlacement.RoundEdge(ToSourceY(placement.ClippedTop)), 0, sourceHeight);
        var sourceRight = Math.Clamp(NearestSampledPlacement.RoundEdge(ToSourceX(placement.ClippedRight)), 0, sourceWidth);
        var sourceBottom = Math.Clamp(NearestSampledPlacement.RoundEdge(ToSourceY(placement.ClippedBottom)), 0, sourceHeight);
        if (sourceRight <= sourceLeft || sourceBottom <= sourceTop)
            return null;

        return new VulkanBlit(
            new PixelRect(sourceLeft, sourceTop, sourceRight, sourceBottom),
            new PixelRect(placement.ClippedLeft, placement.ClippedTop, placement.ClippedRight, placement.ClippedBottom));
    }

    /// <summary>
    /// Plans a nearest-sampled overlay with the software renderer's placement, or returns
    /// <see langword="null"/> when no destination pixel is covered.
    /// </summary>
    public static VulkanOverlayQuad? PlanOverlay(int sourceWidth, int sourceHeight, int outputWidth, int outputHeight, MediaCompositionStretchMode stretchMode)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0 || outputWidth <= 0 || outputHeight <= 0)
            return null;

        var placement = NearestSampledPlacement.Create(sourceWidth, sourceHeight, outputWidth, outputHeight, stretchMode);
        return placement.IsEmpty ? null : new VulkanOverlayQuad(placement);
    }

    /// <summary>
    /// Expands CDG palette indices into tightly packed straight BGRA8, as the software renderer
    /// draws them: the background index and indices past the palette are fully transparent;
    /// every other entry is forced opaque (CDG transparency is index-based, palette alpha is
    /// ignored). The snapshot must have passed <see cref="Classify"/>.
    /// </summary>
    public static void ExpandCdg(CdgRenderSnapshot snapshot, Span<byte> destinationBgra)
    {
        var pixelCount = snapshot.Width * snapshot.Height;
        ArgumentOutOfRangeException.ThrowIfLessThan(destinationBgra.Length, pixelCount * 4);
        ReadOnlySpan<byte> indices = snapshot.ColorIndices.Span[..pixelCount];
        ReadOnlySpan<int> palette = snapshot.PaletteArgb.Span;
        var backgroundIndex = snapshot.BackgroundColorIndex;
        for (var pixel = 0; pixel < pixelCount; pixel++)
        {
            Span<byte> texel = destinationBgra.Slice(pixel * 4, 4);
            var index = indices[pixel];
            if (index == backgroundIndex || index >= palette.Length)
            {
                texel.Clear();
                continue;
            }

            var argb = unchecked((uint)palette[index]);
            texel[0] = (byte)argb;
            texel[1] = (byte)(argb >> 8);
            texel[2] = (byte)(argb >> 16);
            texel[3] = 255;
        }
    }
}
