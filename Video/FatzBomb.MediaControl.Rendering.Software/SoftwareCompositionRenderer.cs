using FatzBomb.MediaControl.Core.Cdg;
using FatzBomb.MediaControl.Core.Graphics.Composition;

namespace FatzBomb.MediaControl.Rendering.Software;

/// <summary>
/// Deterministically composes software-backed media layers into premultiplied BGRA8 pixels.
/// </summary>
public sealed class SoftwareCompositionRenderer
{
    public SoftwareRenderOutput Render(MediaCompositionFrame frame, int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            return SoftwareRenderOutput.Failure(
                SoftwareRenderStatus.InvalidFrame,
                width,
                height,
                "Output dimensions must be positive.");
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
            buffer = new SoftwareCompositionBuffer(width, height);
        }
        catch (OverflowException)
        {
            return SoftwareRenderOutput.Failure(
                SoftwareRenderStatus.InvalidFrame,
                width,
                height,
                "Output dimensions exceed the supported buffer size.");
        }

        buffer.Clear(ResolveClearArgb(frame));
        if (frame.HasBackground)
        {
            DrawBgraLayer(
                buffer,
                frame.BackgroundBgra.Span,
                frame.BackgroundWidth,
                frame.BackgroundHeight,
                frame.BackgroundStride,
                SoftwareStretch.Create(
                    frame.BackgroundWidth,
                    frame.BackgroundHeight,
                    width,
                    height,
                    frame.EffectiveBackgroundStretchMode),
                premultiplied: false);
        }

        if (frame.HasWash)
            DrawWash(buffer, frame.WashArgb);

        if (frame.HasCdgOverlay && !frame.SuppressCdgOverlayDraw && frame.CdgOverlay is { } cdg)
            DrawCdg(buffer, cdg);

        if (frame.HasSubtitleOverlay)
            DrawSubtitle(buffer, frame);

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
        if (!frame.BackgroundBgra.IsEmpty
            && (frame.BackgroundWidth <= 0
                || frame.BackgroundHeight <= 0
                || frame.BackgroundStride < (long)frame.BackgroundWidth * 4
                || frame.BackgroundBgra.Length < (long)frame.BackgroundStride * frame.BackgroundHeight))
        {
            return "The software background buffer is invalid.";
        }

        if (frame.CdgOverlay is { } cdg
            && (!cdg.ColorIndices.IsEmpty || !cdg.PaletteArgb.IsEmpty)
            && (cdg.ColorIndices.Length < cdg.Width * cdg.Height || cdg.PaletteArgb.IsEmpty))
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

    private static uint ResolveClearArgb(MediaCompositionFrame frame) => frame.ClearMode switch
    {
        MediaCompositionClearMode.Transparent or MediaCompositionClearMode.PreservePrevious => 0,
        MediaCompositionClearMode.ConfiguredColor => frame.ClearArgb,
        _ => frame.ClearArgb == 0 ? 0xFF000000 : frame.ClearArgb
    };

    private static void DrawWash(SoftwareCompositionBuffer buffer, uint argb)
    {
        var alpha = (byte)(argb >> 24);
        var red = (byte)(argb >> 16);
        var green = (byte)(argb >> 8);
        var blue = (byte)argb;
        for (var y = 0; y < buffer.Height; y++)
        {
            for (var x = 0; x < buffer.Width; x++)
                buffer.BlendStraight(x, y, blue, green, red, alpha);
        }
    }

    private static void DrawCdg(SoftwareCompositionBuffer buffer, CdgRenderSnapshot snapshot)
    {
        if (snapshot.BackgroundColorIndex < 0)
            return;

        var mapping = SoftwareStretch.Create(
            snapshot.Width,
            snapshot.Height,
            buffer.Width,
            buffer.Height,
            MediaCompositionStretchMode.UniformFit);
        ReadOnlySpan<byte> indices = snapshot.ColorIndices.Span;
        ReadOnlySpan<int> palette = snapshot.PaletteArgb.Span;
        for (var y = mapping.ClippedTop; y < mapping.ClippedBottom; y++)
        {
            for (var x = mapping.ClippedLeft; x < mapping.ClippedRight; x++)
            {
                (int sourceX, int sourceY) = mapping.GetSourcePixel(x, y);
                var paletteIndex = indices[(sourceY * snapshot.Width) + sourceX];
                if (paletteIndex == snapshot.BackgroundColorIndex || paletteIndex >= palette.Length)
                    continue;

                var argb = unchecked((uint)palette[paletteIndex]);
                // CDG transparency is index-based. The D3D11 shader likewise forces every
                // non-background palette entry opaque instead of consuming palette alpha.
                buffer.BlendStraight(
                    x,
                    y,
                    (byte)argb,
                    (byte)(argb >> 8),
                    (byte)(argb >> 16),
                    255);
            }
        }
    }

    private static void DrawSubtitle(SoftwareCompositionBuffer buffer, MediaCompositionFrame frame)
    {
        SoftwareStretch mapping = frame.SubtitleCanvasWidth > 0 && frame.SubtitleCanvasHeight > 0
            ? SoftwareStretch.CreateCanvasPlacement(
                frame.SubtitleWidth,
                frame.SubtitleHeight,
                frame.SubtitleCanvasLeft,
                frame.SubtitleCanvasTop,
                frame.SubtitleCanvasWidth,
                frame.SubtitleCanvasHeight,
                buffer.Width,
                buffer.Height)
            : SoftwareStretch.Create(
                frame.SubtitleWidth,
                frame.SubtitleHeight,
                buffer.Width,
                buffer.Height,
                MediaCompositionStretchMode.UniformFit);
        DrawBgraLayer(
            buffer,
            frame.SubtitleBgra.Span,
            frame.SubtitleWidth,
            frame.SubtitleHeight,
            frame.SubtitleStride,
            mapping,
            premultiplied: true);
    }

    private static void DrawBgraLayer(
        SoftwareCompositionBuffer buffer,
        ReadOnlySpan<byte> source,
        int sourceWidth,
        int sourceHeight,
        int sourceStride,
        SoftwareStretch mapping,
        bool premultiplied)
    {
        for (var y = mapping.ClippedTop; y < mapping.ClippedBottom; y++)
        {
            for (var x = mapping.ClippedLeft; x < mapping.ClippedRight; x++)
            {
                (int sourceX, int sourceY) = mapping.GetSourcePixel(x, y);
                var sourceOffset = (sourceY * sourceStride) + (sourceX * 4);
                if (premultiplied)
                {
                    buffer.BlendPremultiplied(
                        x,
                        y,
                        source[sourceOffset],
                        source[sourceOffset + 1],
                        source[sourceOffset + 2],
                        source[sourceOffset + 3]);
                }
                else
                {
                    buffer.BlendStraight(
                        x,
                        y,
                        source[sourceOffset],
                        source[sourceOffset + 1],
                        source[sourceOffset + 2],
                        source[sourceOffset + 3]);
                }
            }
        }
    }
}
