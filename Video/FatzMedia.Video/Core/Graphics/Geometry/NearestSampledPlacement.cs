using FatzMedia.Video.Core.Graphics.Composition;

namespace FatzMedia.Video.Core.Graphics.Geometry;

/// <summary>
/// Where a nearest-sampled layer lands on an output, shared by the software and Vulkan
/// renderers so their pixels match exactly. Destination edges round away from zero and clip
/// to the output; destination pixel x samples source column
/// floor(((x + 0.5) - left) / width * sourceWidth), clamped (likewise for rows).
/// </summary>
/// <param name="Destination">The unrounded destination rectangle; it may extend past the output.</param>
/// <param name="ClippedLeft">First output column drawn.</param>
/// <param name="ClippedTop">First output row drawn.</param>
/// <param name="ClippedRight">Output column after the last one drawn.</param>
/// <param name="ClippedBottom">Output row after the last one drawn.</param>
/// <param name="SourceWidth">Source width in pixels.</param>
/// <param name="SourceHeight">Source height in pixels.</param>
public readonly record struct NearestSampledPlacement(
    RectD Destination,
    int ClippedLeft,
    int ClippedTop,
    int ClippedRight,
    int ClippedBottom,
    int SourceWidth,
    int SourceHeight)
{
    /// <summary>Gets whether no output pixel is drawn.</summary>
    public bool IsEmpty => ClippedRight <= ClippedLeft || ClippedBottom <= ClippedTop;

    /// <summary>The destination width sampling divides by (at least one pixel).</summary>
    public double SamplingWidth => Math.Max(1, Destination.Width);

    /// <summary>The destination height sampling divides by (at least one pixel).</summary>
    public double SamplingHeight => Math.Max(1, Destination.Height);

    /// <summary>Places a source with <see cref="ViewportGeometry.DestinationRect"/>.</summary>
    public static NearestSampledPlacement Create(
        int sourceWidth,
        int sourceHeight,
        int outputWidth,
        int outputHeight,
        MediaCompositionStretchMode stretchMode) =>
        Create(
            sourceWidth,
            sourceHeight,
            outputWidth,
            outputHeight,
            ViewportGeometry.DestinationRect(sourceWidth, sourceHeight, outputWidth, outputHeight, stretchMode));

    /// <summary>
    /// Places a source that covers the region at (<paramref name="canvasLeft"/>,
    /// <paramref name="canvasTop"/>) of a canvas, with the canvas mapped onto the output by
    /// <see cref="ViewportGeometry.MapCanvasRegionToViewport"/>.
    /// </summary>
    public static NearestSampledPlacement CreateCanvasPlacement(
        int sourceWidth,
        int sourceHeight,
        int canvasLeft,
        int canvasTop,
        int canvasWidth,
        int canvasHeight,
        int outputWidth,
        int outputHeight) =>
        Create(
            sourceWidth,
            sourceHeight,
            outputWidth,
            outputHeight,
            ViewportGeometry.MapCanvasRegionToViewport(
                new RectD(canvasLeft, canvasTop, sourceWidth, sourceHeight),
                canvasWidth,
                canvasHeight,
                outputWidth,
                outputHeight));

    /// <summary>Places a source at an explicit destination rectangle.</summary>
    public static NearestSampledPlacement Create(
        int sourceWidth,
        int sourceHeight,
        int outputWidth,
        int outputHeight,
        RectD destination) => new(
            destination,
            Math.Clamp(RoundEdge(destination.Left), 0, outputWidth),
            Math.Clamp(RoundEdge(destination.Top), 0, outputHeight),
            Math.Clamp(RoundEdge(destination.Right), 0, outputWidth),
            Math.Clamp(RoundEdge(destination.Bottom), 0, outputHeight),
            sourceWidth,
            sourceHeight);

    // Each axis maps independently, so renderers map every column once per layer and every row
    // once per row instead of both per pixel (ENG-017).

    /// <summary>The source column sampled for output column <paramref name="destinationX"/>.</summary>
    public int GetSourceX(int destinationX)
    {
        var normalizedX = ((destinationX + 0.5) - Destination.Left) / SamplingWidth;
        return Math.Clamp((int)Math.Floor(normalizedX * SourceWidth), 0, SourceWidth - 1);
    }

    /// <summary>The source row sampled for output row <paramref name="destinationY"/>.</summary>
    public int GetSourceY(int destinationY)
    {
        var normalizedY = ((destinationY + 0.5) - Destination.Top) / SamplingHeight;
        return Math.Clamp((int)Math.Floor(normalizedY * SourceHeight), 0, SourceHeight - 1);
    }

    /// <summary>Fills <paramref name="sourceColumns"/> with the source column of each clipped destination column.</summary>
    public void MapColumns(Span<int> sourceColumns)
    {
        for (var index = 0; index < sourceColumns.Length; index++)
            sourceColumns[index] = GetSourceX(ClippedLeft + index);
    }

    /// <summary>Rounds a destination edge to a pixel boundary, halves away from zero.</summary>
    public static int RoundEdge(double value) =>
        checked((int)Math.Round(value, MidpointRounding.AwayFromZero));
}
