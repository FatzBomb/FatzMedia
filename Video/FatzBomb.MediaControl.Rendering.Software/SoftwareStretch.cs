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

    // Each axis maps independently, so renderers map every column once per layer and every row
    // once per row instead of both per pixel (ENG-017).
    public int GetSourceX(int destinationX)
    {
        var normalizedX = ((destinationX + 0.5) - DestinationLeft) / DestinationWidth;
        return Math.Clamp((int)Math.Floor(normalizedX * SourceWidth), 0, SourceWidth - 1);
    }

    public int GetSourceY(int destinationY)
    {
        var normalizedY = ((destinationY + 0.5) - DestinationTop) / DestinationHeight;
        return Math.Clamp((int)Math.Floor(normalizedY * SourceHeight), 0, SourceHeight - 1);
    }

    /// <summary>Fills <paramref name="sourceColumns"/> with the source column of each clipped destination column.</summary>
    public void MapColumns(Span<int> sourceColumns)
    {
        for (var index = 0; index < sourceColumns.Length; index++)
            sourceColumns[index] = GetSourceX(ClippedLeft + index);
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
