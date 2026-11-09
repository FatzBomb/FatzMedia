using FatzMedia.Video.Core.Graphics.Composition;

namespace FatzMedia.Video.Core.Graphics.Geometry;

/// <summary>
/// Centralizes viewport and aspect-ratio math for renderer layers.
///
/// This is intentionally pure math. It should not know about CDG packets,
/// video decode, DirectComposition, swap chains, or presentation timing.
/// </summary>
public static class ViewportGeometry
{
    public static RectD FullViewport(double viewportWidth, double viewportHeight)
        => new(0, 0, Math.Max(1, viewportWidth), Math.Max(1, viewportHeight));

    public static RectD DestinationRect(
        double sourceWidth,
        double sourceHeight,
        double viewportWidth,
        double viewportHeight,
        MediaCompositionStretchMode stretch)
    {
        var inputWidth = Math.Max(1, sourceWidth);
        var inputHeight = Math.Max(1, sourceHeight);
        var outputWidth = Math.Max(1, viewportWidth);
        var outputHeight = Math.Max(1, viewportHeight);

        if (stretch == MediaCompositionStretchMode.Fill)
        {
            return FullViewport(outputWidth, outputHeight);
        }

        var scaleX = outputWidth / inputWidth;
        var scaleY = outputHeight / inputHeight;
        var scale = stretch switch
        {
            MediaCompositionStretchMode.Original => 1.0,
            MediaCompositionStretchMode.UniformToFill => Math.Max(scaleX, scaleY),
            MediaCompositionStretchMode.UniformFit => Math.Min(scaleX, scaleY),
            _ => Math.Min(scaleX, scaleY)
        };

        var displayWidth = Math.Max(1, Math.Round(inputWidth * scale));
        var displayHeight = Math.Max(1, Math.Round(inputHeight * scale));
        var left = (outputWidth - displayWidth) / 2.0;
        var top = (outputHeight - displayHeight) / 2.0;

        return new RectD(left, top, displayWidth, displayHeight);
    }

    public static ViewportMapping MapSourceToViewport(
        double sourceWidth,
        double sourceHeight,
        double viewportWidth,
        double viewportHeight,
        MediaCompositionStretchMode stretch)
    {
        var inputWidth = Math.Max(1, sourceWidth);
        var inputHeight = Math.Max(1, sourceHeight);
        var outputWidth = Math.Max(1, viewportWidth);
        var outputHeight = Math.Max(1, viewportHeight);

        if (stretch == MediaCompositionStretchMode.Fill)
        {
            return new ViewportMapping(
                new RectD(0, 0, inputWidth, inputHeight),
                FullViewport(outputWidth, outputHeight));
        }

        RectD desiredDestination = DestinationRect(inputWidth, inputHeight, outputWidth, outputHeight, stretch);
        var visibleLeft = Math.Max(0, desiredDestination.Left);
        var visibleTop = Math.Max(0, desiredDestination.Top);
        var visibleRight = Math.Min(outputWidth, desiredDestination.Right);
        var visibleBottom = Math.Min(outputHeight, desiredDestination.Bottom);

        if (visibleRight <= visibleLeft || visibleBottom <= visibleTop)
        {
            return new ViewportMapping(
                new RectD(0, 0, inputWidth, inputHeight),
                FullViewport(outputWidth, outputHeight));
        }

        var scaleX = desiredDestination.Width / inputWidth;
        var scaleY = desiredDestination.Height / inputHeight;
        var scale = Math.Max(0.000001, (scaleX + scaleY) / 2.0);

        var sourceLeft = Math.Clamp((visibleLeft - desiredDestination.Left) / scale, 0, Math.Max(0, inputWidth - 1));
        var sourceTop = Math.Clamp((visibleTop - desiredDestination.Top) / scale, 0, Math.Max(0, inputHeight - 1));
        var sourceRight = Math.Clamp((visibleRight - desiredDestination.Left) / scale, sourceLeft + 1, inputWidth);
        var sourceBottom = Math.Clamp((visibleBottom - desiredDestination.Top) / scale, sourceTop + 1, inputHeight);

        return new ViewportMapping(
            new RectD(sourceLeft, sourceTop, sourceRight - sourceLeft, sourceBottom - sourceTop),
            new RectD(visibleLeft, visibleTop, visibleRight - visibleLeft, visibleBottom - visibleTop));
    }

    public static RectD UniformFit(
        double sourceWidth,
        double sourceHeight,
        double viewportWidth,
        double viewportHeight)
        => DestinationRect(sourceWidth, sourceHeight, viewportWidth, viewportHeight, MediaCompositionStretchMode.UniformFit);

    public static RectD UniformToFill(
        double sourceWidth,
        double sourceHeight,
        double viewportWidth,
        double viewportHeight)
        => DestinationRect(sourceWidth, sourceHeight, viewportWidth, viewportHeight, MediaCompositionStretchMode.UniformToFill);

    public static RectD MapCanvasRegionToViewport(
        RectD canvasRegion,
        double canvasWidth,
        double canvasHeight,
        double viewportWidth,
        double viewportHeight)
    {
        var normalizedCanvasWidth = Math.Max(1, canvasWidth);
        var normalizedCanvasHeight = Math.Max(1, canvasHeight);
        var normalizedViewportWidth = Math.Max(1, viewportWidth);
        var normalizedViewportHeight = Math.Max(1, viewportHeight);
        var scale = Math.Min(
            normalizedViewportWidth / normalizedCanvasWidth,
            normalizedViewportHeight / normalizedCanvasHeight);
        var canvasLeft = (normalizedViewportWidth - (normalizedCanvasWidth * scale)) / 2;
        var canvasTop = (normalizedViewportHeight - (normalizedCanvasHeight * scale)) / 2;
        return new RectD(
            canvasLeft + (canvasRegion.Left * scale),
            canvasTop + (canvasRegion.Top * scale),
            Math.Max(1, canvasRegion.Width * scale),
            Math.Max(1, canvasRegion.Height * scale));
    }
}
