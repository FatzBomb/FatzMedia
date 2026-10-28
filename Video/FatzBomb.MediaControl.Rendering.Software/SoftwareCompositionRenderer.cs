using System.Numerics;
using System.Runtime.InteropServices;
using FatzBomb.MediaControl.Core.Cdg;
using FatzBomb.MediaControl.Core.Graphics.Composition;
using FatzBomb.MediaControl.Core.Graphics.Geometry;

namespace FatzBomb.MediaControl.Rendering.Software;

/// <summary>
/// Deterministically composes software-backed media layers into premultiplied BGRA8 pixels.
/// </summary>
public sealed class SoftwareCompositionRenderer
{
    private const long ParallelPixelThreshold = 512 * 1024;
    private const int MinRowsPerBand = 32;
    // The owner's capture (20+ logical cores) showed one band per core costing about a fifth
    // of the drawing time in fork/join and thread-pool wakeups, with 21 workers each about 2%
    // busy; a few large bands already saturate memory bandwidth.
    private const int MaxBands = 8;
    private const long MinPixelsPerBand = 128 * 1024;

    public SoftwareRenderOutput Render(MediaCompositionFrame frame, int width, int height)
    {
        SoftwareCompositionBuffer? buffer = null;
        return Render(frame, width, height, ref buffer);
    }

    /// <summary>
    /// Renders into <paramref name="reusableBuffer"/> when it has the output size, otherwise into
    /// a new buffer stored back into it (Stage 022A slice 2: the backend presented a new
    /// full-surface array per frame, about 109 MB/s for 1080p software video). The output's
    /// pixels are then only valid until the next render into the same buffer.
    /// </summary>
    internal SoftwareRenderOutput Render(MediaCompositionFrame frame, int width, int height, ref SoftwareCompositionBuffer? reusableBuffer)
    {
        if (width <= 0 || height <= 0)
        {
            return SoftwareRenderOutput.Failure(
                SoftwareRenderStatus.InvalidFrame,
                width,
                height,
                "Output dimensions must be positive.");
        }

        if (!BitConverter.IsLittleEndian)
        {
            // Pixels are processed as packed little-endian BGRA words (ENG-017).
            return SoftwareRenderOutput.Failure(
                SoftwareRenderStatus.UnsupportedFrame,
                width,
                height,
                "The software renderer requires a little-endian platform.");
        }

        if (frame.HasNativeBackground || frame.HasRetainedBackground)
        {
            return SoftwareRenderOutput.Failure(
                SoftwareRenderStatus.UnsupportedFrame,
                width,
                height,
                "Native and retained backgrounds are not supported by the software renderer.");
        }

        string? validationError = ValidateFrame(frame);
        if (validationError != null)
        {
            return SoftwareRenderOutput.Failure(
                SoftwareRenderStatus.InvalidFrame,
                width,
                height,
                validationError);
        }

        SoftwareCompositionBuffer buffer;
        try
        {
            buffer = reusableBuffer is { } reusable && reusable.Width == width && reusable.Height == height
                ? reusable
                : new SoftwareCompositionBuffer(width, height);
            reusableBuffer = buffer;
        }
        catch (OverflowException)
        {
            return SoftwareRenderOutput.Failure(
                SoftwareRenderStatus.InvalidFrame,
                width,
                height,
                "Output dimensions exceed the supported buffer size.");
        }

        Fill(buffer, ColorMath.PremultiplyBgra(frame.ResolveClearArgb()));
        var transformWholeComposition = frame.BaseVisualTransform is { IsIdentity: false, IncludeOverlays: true };
        if (frame.HasBackground)
        {
            DrawBgraLayer(
                buffer,
                frame.BackgroundBgra,
                frame.BackgroundWidth,
                frame.BackgroundStride,
                NearestSampledPlacement.Create(
                    frame.BackgroundWidth,
                    frame.BackgroundHeight,
                    width,
                    height,
                    frame.EffectiveBackgroundStretchMode),
                premultiplied: false,
                transformWholeComposition ? BaseVisualTransform.Identity : frame.BaseVisualTransform);
        }

        if (frame.HasWash)
            DrawWash(buffer, frame.WashArgb);

        if (frame.HasCdgOverlay && !frame.SuppressCdgOverlayDraw && frame.CdgOverlay is { } cdg)
            DrawCdg(buffer, cdg);

        if (frame.HasSubtitleOverlay)
            DrawSubtitle(buffer, frame);

        if (transformWholeComposition)
            ApplyFinalTransform(buffer, frame.BaseVisualTransform);

        return new SoftwareRenderOutput(
            SoftwareRenderStatus.Rendered,
            buffer.Pixels,
            buffer.Width,
            buffer.Height,
            buffer.Stride,
            null);
    }

    private static string? ValidateFrame(MediaCompositionFrame frame)
    {
        if (!frame.IsBackgroundWellFormed)
        {
            return "The software background buffer is invalid.";
        }

        if (frame.CdgOverlay is { IsWellFormed: false })
        {
            return "The CDG snapshot is invalid.";
        }

        if (!frame.SubtitleBgra.IsEmpty
            && (frame.SubtitleWidth <= 0
                || frame.SubtitleHeight <= 0
                || frame.SubtitleStride < (long)frame.SubtitleWidth * 4
                || frame.SubtitleBgra.Length < (long)frame.SubtitleStride * frame.SubtitleHeight))
        {
            return "The subtitle buffer is invalid.";
        }

        return null;
    }

    private static void DrawWash(SoftwareCompositionBuffer buffer, uint argb)
    {
        var alpha = (byte)(argb >> 24);
        var red = (byte)(argb >> 16);
        var green = (byte)(argb >> 8);
        var blue = (byte)argb;
        if (alpha == 0)
            return;

        if (alpha == 255)
        {
            // Opaque straight blend replaces every channel exactly.
            Fill(buffer, SoftwareCompositionBuffer.Pack(blue, green, red, 255));
            return;
        }

        // One alpha for the whole layer. Every byte lane is (source * alpha + 127 + destination *
        // (255 - alpha)) / 255, with source 255 for the alpha lane; that is exactly the straight
        // blend (ENG-017). Vectors take whole runs and the same arithmetic the remainder.
        var inverseAlpha = 255 - alpha;
        var addends = new ushort[Math.Max(4, Vector<ushort>.Count)];
        for (var lane = 0; lane < addends.Length; lane++)
        {
            var source = (lane % 4) switch { 0 => blue, 1 => green, 2 => red, _ => (byte)255 };
            addends[lane] = (ushort)((source * alpha) + 127);
        }

        var width = buffer.Width;
        ForRowBands(0, buffer.Height, width, (top, bottom) =>
        {
            Span<byte> bytes = MemoryMarshal.AsBytes(buffer.Pixels32[(top * width)..(bottom * width)]);
            var offset = 0;
            if (Vector.IsHardwareAccelerated)
            {
                var addend = new Vector<ushort>(addends);
                var inverse = new Vector<ushort>((ushort)inverseAlpha);
                for (; offset <= bytes.Length - Vector<byte>.Count; offset += Vector<byte>.Count)
                {
                    Vector.Widen(new Vector<byte>(bytes[offset..]), out Vector<ushort> low, out Vector<ushort> high);
                    Vector.Narrow(DivideBy255(addend + (low * inverse)), DivideBy255(addend + (high * inverse)))
                        .CopyTo(bytes[offset..]);
                }
            }

            // Vector widths are multiples of a pixel, so the remainder starts on a blue byte.
            for (; offset < bytes.Length; offset++)
            {
                var value = addends[offset % 4] + (bytes[offset] * inverseAlpha);
                bytes[offset] = (byte)((value + 1 + (value >> 8)) >> 8);
            }
        });
    }

    // Exact x / 255 for 0 <= x <= 255 * 255 + 127, the largest blend numerator (as the scalar
    // remainder above).
    private static Vector<ushort> DivideBy255(Vector<ushort> value) =>
        Vector.ShiftRightLogical(value + Vector<ushort>.One + Vector.ShiftRightLogical(value, 8), 8);

    private static void DrawCdg(SoftwareCompositionBuffer buffer, CdgRenderSnapshot snapshot)
    {
        if (snapshot.BackgroundColorIndex < 0)
            return;

        var mapping = NearestSampledPlacement.Create(
            snapshot.Width,
            snapshot.Height,
            buffer.Width,
            buffer.Height,
            MediaCompositionStretchMode.UniformFit);
        var columnCount = mapping.ClippedRight - mapping.ClippedLeft;
        if (columnCount <= 0)
            return;

        var columns = buffer.GetColumnScratch(columnCount);
        mapping.MapColumns(columns.AsSpan(0, columnCount));
        ForRowBands(mapping.ClippedTop, mapping.ClippedBottom, columnCount, (top, bottom) =>
        {
            ReadOnlySpan<byte> indices = snapshot.ColorIndices.Span;
            ReadOnlySpan<int> palette = snapshot.PaletteArgb.Span;
            Span<uint> pixels = buffer.Pixels32;
            for (var y = top; y < bottom; y++)
            {
                ReadOnlySpan<byte> sourceRow = indices.Slice(mapping.GetSourceY(y) * snapshot.Width, snapshot.Width);
                Span<uint> destinationRow = pixels.Slice((y * buffer.Width) + mapping.ClippedLeft, columnCount);
                for (var index = 0; index < columnCount; index++)
                {
                    var paletteIndex = sourceRow[columns[index]];
                    if (paletteIndex == snapshot.BackgroundColorIndex || paletteIndex >= palette.Length)
                        continue;

                    // CDG transparency is index-based. The D3D11 shader likewise forces every
                    // non-background palette entry opaque instead of consuming palette alpha, and
                    // an opaque straight blend replaces the pixel exactly.
                    destinationRow[index] = unchecked((uint)palette[paletteIndex]) | 0xFF000000;
                }
            }
        });
    }

    private static void DrawSubtitle(SoftwareCompositionBuffer buffer, MediaCompositionFrame frame)
    {
        NearestSampledPlacement mapping = frame.SubtitleCanvasWidth > 0 && frame.SubtitleCanvasHeight > 0
            ? NearestSampledPlacement.CreateCanvasPlacement(
                frame.SubtitleWidth,
                frame.SubtitleHeight,
                frame.SubtitleCanvasLeft,
                frame.SubtitleCanvasTop,
                frame.SubtitleCanvasWidth,
                frame.SubtitleCanvasHeight,
                buffer.Width,
                buffer.Height)
            : NearestSampledPlacement.Create(
                frame.SubtitleWidth,
                frame.SubtitleHeight,
                buffer.Width,
                buffer.Height,
                MediaCompositionStretchMode.UniformFit);
        DrawBgraLayer(
            buffer,
            frame.SubtitleBgra,
            frame.SubtitleWidth,
            frame.SubtitleStride,
            mapping,
            premultiplied: true,
            BaseVisualTransform.Identity);
    }

    private static void DrawBgraLayer(
        SoftwareCompositionBuffer buffer,
        ReadOnlyMemory<byte> source,
        int sourceWidth,
        int sourceStride,
        NearestSampledPlacement mapping,
        bool premultiplied,
        BaseVisualTransform transform)
    {
        var columnCount = mapping.ClippedRight - mapping.ClippedLeft;
        if (columnCount <= 0)
            return;

        var columns = buffer.GetColumnScratch(columnCount);
        mapping.MapColumns(columns.AsSpan(0, columnCount));
        var identity = transform.IsIdentity;
        ForRowBands(mapping.ClippedTop, mapping.ClippedBottom, columnCount, (top, bottom) =>
        {
            ReadOnlySpan<byte> sourcePixels = source.Span;
            Span<uint> pixels = buffer.Pixels32;
            for (var y = top; y < bottom; y++)
            {
                ReadOnlySpan<uint> sourceRow = MemoryMarshal.Cast<byte, uint>(
                    sourcePixels.Slice(mapping.GetSourceY(y) * sourceStride, sourceWidth * 4));
                var destinationStart = (y * buffer.Width) + mapping.ClippedLeft;
                Span<uint> destinationRow = pixels.Slice(destinationStart, columnCount);
                for (var index = 0; index < columnCount; index++)
                {
                    var sourcePixel = sourceRow[columns[index]];
                    var alpha = (byte)(sourcePixel >> 24);
                    if (identity)
                    {
                        // Exact shortcuts of the blends below (ENG-017): an opaque source replaces
                        // the destination, and a transparent one leaves it unchanged (straight
                        // alpha always; premultiplied only when the colour is zero too).
                        if (alpha == 255)
                        {
                            destinationRow[index] = sourcePixel;
                            continue;
                        }

                        if (alpha == 0 && (!premultiplied || sourcePixel == 0))
                            continue;
                    }

                    var blue = (byte)sourcePixel;
                    var green = (byte)(sourcePixel >> 8);
                    var red = (byte)(sourcePixel >> 16);
                    var offset = (destinationStart + index) * 4;
                    if (!identity)
                    {
                        (byte transformedBlue, byte transformedGreen, byte transformedRed, byte transformedAlpha) =
                            SoftwareBaseVisualTransform.ApplyStraightToPremultiplied(blue, green, red, alpha, transform);
                        buffer.BlendPremultipliedAt(offset, transformedBlue, transformedGreen, transformedRed, transformedAlpha);
                    }
                    else if (premultiplied)
                    {
                        buffer.BlendPremultipliedAt(offset, blue, green, red, alpha);
                    }
                    else
                    {
                        buffer.BlendStraightAt(offset, blue, green, red, alpha);
                    }
                }
            }
        });
    }

    private static void Fill(SoftwareCompositionBuffer buffer, uint pixel)
    {
        var width = buffer.Width;
        ForRowBands(0, buffer.Height, width, (top, bottom) => buffer.Pixels32[(top * width)..(bottom * width)].Fill(pixel));
    }

    /// <summary>
    /// Runs <paramref name="drawRows"/> over [<paramref name="top"/>, <paramref name="bottom"/>),
    /// split into at most <see cref="MaxBands"/> parallel row bands when the area is large
    /// (ENG-017: one ultrawide frame is about 5 million pixels). Each band writes only its own rows and reads shared input, so
    /// the output is identical to one pass; small frames stay on the calling thread.
    /// </summary>
    private static void ForRowBands(int top, int bottom, int width, Action<int, int> drawRows)
    {
        var rows = bottom - top;
        var pixels = (long)rows * width;
        var bands = pixels < ParallelPixelThreshold
            ? 1
            : (int)Math.Min(
                Math.Min(Environment.ProcessorCount, MaxBands),
                Math.Min(rows / MinRowsPerBand, pixels / MinPixelsPerBand));
        if (bands <= 1)
        {
            if (rows > 0)
                drawRows(top, bottom);
            return;
        }

        _ = Parallel.For(0, bands, band => drawRows(
            top + (int)((long)rows * band / bands),
            top + (int)((long)rows * (band + 1) / bands)));
    }

    private static void ApplyFinalTransform(SoftwareCompositionBuffer buffer, BaseVisualTransform transform)
    {
        Span<byte> pixels = buffer.Pixels;
        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            (byte blue, byte green, byte red, byte alpha) = SoftwareBaseVisualTransform.ApplyPremultipliedToPremultiplied(
                pixels[offset],
                pixels[offset + 1],
                pixels[offset + 2],
                pixels[offset + 3],
                transform);
            pixels[offset] = blue;
            pixels[offset + 1] = green;
            pixels[offset + 2] = red;
            pixels[offset + 3] = alpha;
        }
    }
}
