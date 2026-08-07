using FatzBomb.MediaControl.Core.Graphics.Composition;
using FatzBomb.MediaControl.Core.Graphics.Geometry;

namespace FatzBomb.MediaControl.Rendering.Software;

internal readonly record struct SoftwareStretch(
    double DestinationLeft,
    double DestinationTop,
    double DestinationWidth,
    double DestinationHeight,
    int ClippedLeft,
    int ClippedTop,
    int ClippedRight,
    int ClippedBottom,
    int SourceWidth,
    int SourceHeight)
{
    public static SoftwareStretch Create(
        int sourceWidth,
        int sourceHeight,
        int outputWidth,
        int outputHeight,
        MediaCompositionStretchMode stretchMode)
    {
        RectD destination = ViewportGeometry.DestinationRect(
            sourceWidth,
            sourceHeight,
            outputWidth,
            outputHeight,
            stretchMode);
        return Create(sourceWidth, sourceHeight, outputWidth, outputHeight, destination);
    }

    public static SoftwareStretch CreateCanvasPlacement(
        int sourceWidth,
        int sourceHeight,
        int canvasLeft,
        int canvasTop,
        int canvasWidth,
        int canvasHeight,
        int outputWidth,
        int outputHeight)
    {
        RectD destination = ViewportGeometry.MapCanvasRegionToViewport(
            new RectD(canvasLeft, canvasTop, sourceWidth, sourceHeight),
            canvasWidth,
            canvasHeight,
            outputWidth,
            outputHeight);
        return Create(sourceWidth, sourceHeight, outputWidth, outputHeight, destination);
    }

    public (int X, int Y) GetSourcePixel(int destinationX, int destinationY)
    {
        var normalizedX = ((destinationX + 0.5) - DestinationLeft) / DestinationWidth;
        var normalizedY = ((destinationY + 0.5) - DestinationTop) / DestinationHeight;
        var sourceX = Math.Clamp((int)Math.Floor(normalizedX * SourceWidth), 0, SourceWidth - 1);
        var sourceY = Math.Clamp((int)Math.Floor(normalizedY * SourceHeight), 0, SourceHeight - 1);
        return (sourceX, sourceY);
    }

    private static SoftwareStretch Create(
        int sourceWidth,
        int sourceHeight,
        int outputWidth,
        int outputHeight,
        RectD destination)
    {
        var left = RoundEdge(destination.Left);
        var top = RoundEdge(destination.Top);
        var right = RoundEdge(destination.Right);
        var bottom = RoundEdge(destination.Bottom);
        return new(
            destination.Left,
            destination.Top,
            Math.Max(1, destination.Width),
            Math.Max(1, destination.Height),
            Math.Clamp(left, 0, outputWidth),
            Math.Clamp(top, 0, outputHeight),
            Math.Clamp(right, 0, outputWidth),
            Math.Clamp(bottom, 0, outputHeight),
            sourceWidth,
            sourceHeight);
    }

    private static int RoundEdge(double value) =>
        checked((int)Math.Round(value, MidpointRounding.AwayFromZero));
}
