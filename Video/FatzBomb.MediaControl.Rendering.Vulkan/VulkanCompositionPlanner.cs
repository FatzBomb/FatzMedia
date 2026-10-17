using FatzBomb.MediaControl.Core.Graphics.Composition;
using FatzBomb.MediaControl.Core.Graphics.Geometry;

namespace FatzBomb.MediaControl.Rendering.Vulkan;

/// <summary>What the current Vulkan slice can draw for a composition frame.</summary>
internal enum VulkanFrameSupport
{
    Supported,
    NativeOrRetainedBackground,
    Overlays,
    BaseVisualTransform,
}

/// <summary>
/// Pure planning shared with the software renderer's conventions: clear colour resolution and
/// premultiplication, background destination rectangle (Core <see cref="ViewportGeometry"/>),
/// and clipping of off-target destinations with a proportional source crop.
/// </summary>
internal static class VulkanCompositionPlanner
{
    public static VulkanFrameSupport Classify(in MediaCompositionFrame frame)
    {
        if (frame.HasNativeBackground || frame.HasRetainedBackground)
            return VulkanFrameSupport.NativeOrRetainedBackground;
        if (frame.HasWash || (frame.HasCdgOverlay && !frame.SuppressCdgOverlayDraw) || frame.HasSubtitleOverlay)
            return VulkanFrameSupport.Overlays;
        if (!frame.BaseVisualTransform.IsIdentity)
            return VulkanFrameSupport.BaseVisualTransform;
        return VulkanFrameSupport.Supported;
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

    private static int Round(double value) => checked((int)Math.Round(value, MidpointRounding.AwayFromZero));

    private static byte Premultiply(byte value, byte alpha) => (byte)(((value * alpha) + 127) / 255);
}
