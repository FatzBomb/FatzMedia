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

/// <summary>Software BGRA background layer and its blit.</summary>
internal readonly record struct VulkanBackgroundLayer(ReadOnlyMemory<byte> Bgra, int Width, int Height, int Stride, VulkanBlit Blit);

/// <summary>
/// Nearest-sampled overlay placement with the software renderer's mapping: destination pixel
/// (x, y) samples source pixel floor(((x + 0.5) - left) / width * sourceWidth) (likewise for
/// y), clamped. <see cref="Destination"/> is the clipped integer rectangle that is drawn.
/// </summary>
internal readonly record struct VulkanOverlayQuad(
    PixelRect Destination,
    double DestinationLeft,
    double DestinationTop,
    double DestinationWidth,
    double DestinationHeight,
    int SourceWidth,
    int SourceHeight)
{
    /// <summary>
    /// Normalized texture coordinates at the edges of <see cref="Destination"/>. Interpolated
    /// across a viewport equal to <see cref="Destination"/>, pixel centre x + 0.5 receives
    /// ((x + 0.5) - left) / width, so nearest sampling picks <see cref="GetSourcePixel"/>.
    /// </summary>
    public (float U0, float V0, float U1, float V1) TexCoords => (
        (float)((Destination.Left - DestinationLeft) / DestinationWidth),
        (float)((Destination.Top - DestinationTop) / DestinationHeight),
        (float)((Destination.Right - DestinationLeft) / DestinationWidth),
        (float)((Destination.Bottom - DestinationTop) / DestinationHeight));

    /// <summary>The source pixel the software renderer samples for a destination pixel.</summary>
    public (int X, int Y) GetSourcePixel(int destinationX, int destinationY)
    {
        var normalizedX = ((destinationX + 0.5) - DestinationLeft) / DestinationWidth;
        var normalizedY = ((destinationY + 0.5) - DestinationTop) / DestinationHeight;
        return (
            Math.Clamp((int)Math.Floor(normalizedX * SourceWidth), 0, SourceWidth - 1),
            Math.Clamp((int)Math.Floor(normalizedY * SourceHeight), 0, SourceHeight - 1));
    }
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
        // Same validation as the software renderer, which refuses the whole frame.
        if (frame.CdgOverlay is { } cdg
            && (!cdg.ColorIndices.IsEmpty || !cdg.PaletteArgb.IsEmpty)
            && (cdg.ColorIndices.Length < cdg.Width * cdg.Height || cdg.PaletteArgb.IsEmpty))
        {
            return VulkanFrameSupport.InvalidCdgSnapshot;
        }

        return VulkanFrameSupport.Supported;
    }

    /// <summary>Plans a frame that <see cref="Classify"/> reported as supported.</summary>
    public static VulkanFramePlan Plan(in MediaCompositionFrame frame, int outputWidth, int outputHeight)
    {
        VulkanBackgroundLayer? background = null;
        if (frame.HasBackground
            && PlanBackground(frame.BackgroundWidth, frame.BackgroundHeight, outputWidth, outputHeight, frame.EffectiveBackgroundStretchMode) is { } blit)
        {
            background = new VulkanBackgroundLayer(frame.BackgroundBgra, frame.BackgroundWidth, frame.BackgroundHeight, frame.BackgroundStride, blit);
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

        RectD destination = ViewportGeometry.DestinationRect(sourceWidth, sourceHeight, outputWidth, outputHeight, stretchMode);
        if (destination.Width <= 0 || destination.Height <= 0)
            return null;

        var left = Round(destination.X);
        var top = Round(destination.Y);
        var right = Round(destination.Right);
        var bottom = Round(destination.Bottom);
        var clippedLeft = Math.Clamp(left, 0, outputWidth);
        var clippedTop = Math.Clamp(top, 0, outputHeight);
        var clippedRight = Math.Clamp(right, 0, outputWidth);
        var clippedBottom = Math.Clamp(bottom, 0, outputHeight);
        if (clippedRight <= clippedLeft || clippedBottom <= clippedTop)
            return null;

        // Map the clipped destination back into source space (only differs from the full
        // source when the destination extends past the output, e.g. UniformToFill).
        double ToSourceX(int x) => (x - destination.X) / destination.Width * sourceWidth;
        double ToSourceY(int y) => (y - destination.Y) / destination.Height * sourceHeight;
        var sourceLeft = Math.Clamp(Round(ToSourceX(clippedLeft)), 0, sourceWidth);
        var sourceTop = Math.Clamp(Round(ToSourceY(clippedTop)), 0, sourceHeight);
        var sourceRight = Math.Clamp(Round(ToSourceX(clippedRight)), 0, sourceWidth);
        var sourceBottom = Math.Clamp(Round(ToSourceY(clippedBottom)), 0, sourceHeight);
        if (sourceRight <= sourceLeft || sourceBottom <= sourceTop)
            return null;

        return new VulkanBlit(
            new PixelRect(sourceLeft, sourceTop, sourceRight, sourceBottom),
            new PixelRect(clippedLeft, clippedTop, clippedRight, clippedBottom));
    }

    /// <summary>
    /// Plans a nearest-sampled overlay the way <c>SoftwareStretch.Create</c> does (the Vulkan
    /// package must not reference the software renderer, so the small formula lives here), or
    /// returns <see langword="null"/> when no destination pixel is covered.
    /// </summary>
    public static VulkanOverlayQuad? PlanOverlay(int sourceWidth, int sourceHeight, int outputWidth, int outputHeight, MediaCompositionStretchMode stretchMode)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0 || outputWidth <= 0 || outputHeight <= 0)
            return null;

        RectD destination = ViewportGeometry.DestinationRect(sourceWidth, sourceHeight, outputWidth, outputHeight, stretchMode);
        var clipped = new PixelRect(
            Math.Clamp(Round(destination.Left), 0, outputWidth),
            Math.Clamp(Round(destination.Top), 0, outputHeight),
            Math.Clamp(Round(destination.Right), 0, outputWidth),
            Math.Clamp(Round(destination.Bottom), 0, outputHeight));
        if (clipped.IsEmpty)
            return null;

        return new VulkanOverlayQuad(
            clipped,
            destination.Left,
            destination.Top,
            Math.Max(1, destination.Width),
            Math.Max(1, destination.Height),
            sourceWidth,
            sourceHeight);
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

    private static int Round(double value) => checked((int)Math.Round(value, MidpointRounding.AwayFromZero));

    private static byte Premultiply(byte value, byte alpha) => (byte)(((value * alpha) + 127) / 255);
}
