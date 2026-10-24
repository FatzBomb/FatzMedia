using FatzBomb.MediaControl.Core.Cdg;
using FatzBomb.MediaControl.Core.Graphics.Composition;
using FatzBomb.MediaControl.Core.Graphics.Geometry;

namespace FatzBomb.MediaControl.Rendering.Vulkan;

/// <summary>What the current Vulkan slice can draw for a composition frame.</summary>
internal enum VulkanFrameSupport
{
    Supported,
    NativeOrRetainedBackground,
    Subtitles,
    BaseVisualTransform,
    InvalidCdgSnapshot,
}

/// <summary>
/// Software BGRA background layer and its blit. <see cref="Revision"/> is the frame's background
/// revision (Core bumps it whenever the pixels change); -1 is unknown, and always uploads.
/// </summary>
internal readonly record struct VulkanBackgroundLayer(ReadOnlyMemory<byte> Bgra, int Width, int Height, int Stride, VulkanBlit Blit, long Revision = -1);

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

/// <summary>Everything the compositor draws for one frame, in software-renderer order.</summary>
internal readonly record struct VulkanFramePlan(
    int OutputWidth,
    int OutputHeight,
    (byte B, byte G, byte R, byte A) ClearBgraPremultiplied,
    VulkanBackgroundLayer? Background,
    uint? WashArgb,
    VulkanCdgLayer? Cdg)
{
    /// <summary>Gets whether the frame needs the graphics pipeline after the clear and blit.</summary>
    public bool HasDraws => WashArgb is not null || Cdg is not null;
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
        if (frame.HasNativeBackground || frame.HasRetainedBackground)
            return VulkanFrameSupport.NativeOrRetainedBackground;
        if (frame.HasSubtitleOverlay)
            return VulkanFrameSupport.Subtitles;
        if (!frame.BaseVisualTransform.IsIdentity)
            return VulkanFrameSupport.BaseVisualTransform;
        // The software renderer refuses the whole frame for the same snapshot.
        return frame.CdgOverlay is { IsWellFormed: false }
            ? VulkanFrameSupport.InvalidCdgSnapshot
            : VulkanFrameSupport.Supported;
    }

    /// <summary>Plans a frame that <see cref="Classify"/> reported as supported.</summary>
    public static VulkanFramePlan Plan(in MediaCompositionFrame frame, int outputWidth, int outputHeight)
    {
        VulkanBackgroundLayer? background = null;
        if (frame.HasBackground
            && PlanBackground(frame.BackgroundWidth, frame.BackgroundHeight, outputWidth, outputHeight, frame.EffectiveBackgroundStretchMode) is { } blit)
        {
            background = new VulkanBackgroundLayer(frame.BackgroundBgra, frame.BackgroundWidth, frame.BackgroundHeight, frame.BackgroundStride, blit, frame.BackgroundRevision);
        }

        VulkanCdgLayer? cdg = null;
        if (frame is { HasCdgOverlay: true, SuppressCdgOverlayDraw: false, CdgOverlay: { BackgroundColorIndex: >= 0 } snapshot }
            && PlanOverlay(snapshot.Width, snapshot.Height, outputWidth, outputHeight, MediaCompositionStretchMode.UniformFit) is { } quad)
        {
            cdg = new VulkanCdgLayer(snapshot, quad);
        }

        return new VulkanFramePlan(
            outputWidth,
            outputHeight,
            ResolveClear(frame),
            background,
            frame.HasWash ? frame.WashArgb : null,
            cdg);
    }

    /// <summary>Same clear policy as the software renderer, returned premultiplied.</summary>
    public static (byte B, byte G, byte R, byte A) ResolveClear(in MediaCompositionFrame frame)
    {
        var argb = frame.ClearMode switch
        {
            MediaCompositionClearMode.Transparent or MediaCompositionClearMode.PreservePrevious => 0u,
            MediaCompositionClearMode.ConfiguredColor => frame.ClearArgb,
            _ => frame.ClearArgb == 0 ? 0xFF000000u : frame.ClearArgb,
        };
        var alpha = (byte)(argb >> 24);
        return (
            Premultiply((byte)argb, alpha),
            Premultiply((byte)(argb >> 8), alpha),
            Premultiply((byte)(argb >> 16), alpha),
            alpha);
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

    private static byte Premultiply(byte value, byte alpha) => (byte)(((value * alpha) + 127) / 255);
}
