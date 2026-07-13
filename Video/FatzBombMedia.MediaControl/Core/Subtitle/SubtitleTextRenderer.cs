using System.Numerics;
using FatzBombMedia.MediaControl.Core.Configuration;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.DXGI;
using Vortice.Mathematics;
using Vortice.WIC;

namespace FatzBombMedia.MediaControl.Core.Subtitle;

internal readonly record struct SubtitleRenderResult(
    ReadOnlyMemory<byte> Pixels,
    int Width,
    int Height,
    int Stride,
    int CanvasLeft,
    int CanvasTop,
    int CanvasWidth,
    int CanvasHeight);

internal sealed class SubtitleTextRenderer(SubtitleTextStyle? style = null, KaraokeTextStyle? karaokeStyle = null) : IDisposable
{
    // Presentation may request a fresh cue from multiple scheduler paths; the coordinator serializes
    // calls, and the factory is multi-threaded to keep that usage valid if a future preview path is added.
    private readonly ID2D1Factory1 _d2dFactory = D2D1.D2D1CreateFactory<ID2D1Factory1>(Vortice.Direct2D1.FactoryType.MultiThreaded);
    private readonly IDWriteFactory _writeFactory = DWrite.DWriteCreateFactory<IDWriteFactory>(Vortice.DirectWrite.FactoryType.Shared);
    private readonly IWICImagingFactory _wicFactory = new();
    private readonly SubtitleTextStyle _style = (style ?? new SubtitleTextStyle()).Normalize();
    private readonly KaraokeTextStyle _karaokeStyle = (karaokeStyle ?? new KaraokeTextStyle()).Normalize();

    public SubtitleRenderResult Render(string text, int width, int height)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        using IDWriteTextFormat format = _writeFactory.CreateTextFormat(_style.FontFamily, null, FontWeight.Bold, FontStyle.Normal, FontStretch.Normal, _style.FontSize, "");
        using IDWriteTextLayout layout = _writeFactory.CreateTextLayout(text, format, width * 0.85f, height * 0.3f);
        layout.TextAlignment = TextAlignment.Center;
        TextMetrics metrics = layout.Metrics;
        var canvasOrigin = new Vector2(
            width * 0.075f,
            Math.Max(0, height - metrics.Height - (height * 0.08f)));
        var textLeft = canvasOrigin.X + Math.Max(0, ((width * 0.85f) - metrics.Width) / 2);
        RenderBounds bounds = CreateBounds(
            textLeft,
            canvasOrigin.Y,
            textLeft + metrics.Width,
            canvasOrigin.Y + metrics.Height,
            width,
            height,
            _style.OutlineWidth,
            _style.ShadowOffsetX,
            _style.ShadowOffsetY);

        using IWICBitmap bitmap = _wicFactory.CreateBitmap((uint)bounds.Width, (uint)bounds.Height, PixelFormat.Format32bppPBGRA, BitmapCreateCacheOption.CacheOnLoad);
        using ID2D1RenderTarget target = _d2dFactory.CreateWicBitmapRenderTarget(
            bitmap,
            new RenderTargetProperties(new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied)));
        using ID2D1SolidColorBrush textBrush = target.CreateSolidColorBrush(ToColor(_style.TextArgb));
        using ID2D1SolidColorBrush outlineBrush = target.CreateSolidColorBrush(ToColor(_style.OutlineArgb));
        using ID2D1SolidColorBrush shadowBrush = target.CreateSolidColorBrush(ToColor(_style.ShadowArgb));
        Vector2 origin = canvasOrigin - new Vector2(bounds.Left, bounds.Top);

        target.BeginDraw();
        target.Clear(new Color4(0f, 0f, 0f, 0f));
        if ((_style.ShadowArgb >> 24) != 0)
            target.DrawTextLayout(origin + new Vector2(_style.ShadowOffsetX, _style.ShadowOffsetY), layout, shadowBrush);

        if (_style.OutlineWidth > 0 && (_style.OutlineArgb >> 24) != 0)
        {
            for (var x = -1; x <= 1; x++)
            {
                for (var y = -1; y <= 1; y++)
                {
                    if (x != 0 || y != 0)
                        target.DrawTextLayout(origin + new Vector2(x * _style.OutlineWidth, y * _style.OutlineWidth), layout, outlineBrush);
                }
            }
        }

        target.DrawTextLayout(origin, layout, textBrush);
        _ = target.EndDraw();

        var stride = bounds.Width * 4;
        var pixels = new byte[checked(stride * bounds.Height)];
        bitmap.CopyPixels((uint)stride, pixels);
        return new SubtitleRenderResult(
            pixels,
            bounds.Width,
            bounds.Height,
            stride,
            bounds.Left,
            bounds.Top,
            width,
            height);
    }

    public SubtitleRenderResult RenderKaraoke(KaraokeCueTiming timing, TimeSpan position, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(timing);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        var baseStyle = new KaraokeLayerStyle(
            _karaokeStyle.TextArgb,
            _karaokeStyle.OutlineArgb,
            _karaokeStyle.OutlineWidth,
            _karaokeStyle.ShadowArgb);
        var wipeStyle = new KaraokeLayerStyle(
            _karaokeStyle.WipeTextArgb,
            _karaokeStyle.WipeOutlineArgb,
            _karaokeStyle.WipeOutlineWidth,
            _karaokeStyle.WipeShadowArgb);

        return RenderKaraokeFrame(timing, position, baseStyle, wipeStyle, width, height);
    }

    private SubtitleRenderResult RenderKaraokeFrame(
        KaraokeCueTiming timing,
        TimeSpan position,
        KaraokeLayerStyle baseStyle,
        KaraokeLayerStyle wipeStyle,
        int width,
        int height)
    {
        using IDWriteTextFormat format = _writeFactory.CreateTextFormat(_karaokeStyle.FontFamily, null, FontWeight.Bold, FontStyle.Normal, FontStretch.Normal, _karaokeStyle.FontSize, "");
        var layoutWidth = width * 0.85f;
        IDWriteTextLayout[] layouts = [.. timing.Lines.Select(line => _writeFactory.CreateTextLayout(line.Text, format, layoutWidth, height * 0.2f))];
        IDWriteTextLayout[] outgoingLayouts = [.. timing.OutgoingPageLines.Select(line => _writeFactory.CreateTextLayout(line.Text, format, layoutWidth, height * 0.2f))];

        try
        {
            foreach (IDWriteTextLayout? layout in layouts.Concat(outgoingLayouts))
                layout.TextAlignment = TextAlignment.Center;

            var transitionDuration = TimeSpan.FromMilliseconds(_karaokeStyle.LineTransitionMilliseconds);
            var middleY = height * 0.5f;
            var slotDistance = _karaokeStyle.FontSize * 1.55f;
            var padding = (int)Math.Ceiling(Math.Max(
                Math.Max(_karaokeStyle.OutlineWidth, _karaokeStyle.WipeOutlineWidth),
                Math.Max(Math.Abs(_karaokeStyle.ShadowOffsetX), Math.Abs(_karaokeStyle.ShadowOffsetY))));
            IDWriteTextLayout[] allLayouts = [.. layouts, .. outgoingLayouts];
            var maxLineHeight = allLayouts.Max(static layout => layout.Metrics.Height);
            var minTextLeft = allLayouts.Min(layout => (width * 0.075f) + Math.Max(0, (layoutWidth - layout.Metrics.Width) / 2));
            var maxTextRight = allLayouts.Max(layout => (width * 0.075f) + Math.Max(0, (layoutWidth - layout.Metrics.Width) / 2) + layout.Metrics.Width);
            RenderBounds bounds = CreateBounds(
                minTextLeft,
                middleY - (slotDistance * 2) - (maxLineHeight / 2),
                maxTextRight,
                middleY + (slotDistance * 3) + (maxLineHeight / 2),
                width,
                height,
                padding,
                _karaokeStyle.ShadowOffsetX,
                _karaokeStyle.ShadowOffsetY);

            using IWICBitmap bitmap = _wicFactory.CreateBitmap((uint)bounds.Width, (uint)bounds.Height, PixelFormat.Format32bppPBGRA, BitmapCreateCacheOption.CacheOnLoad);
            using ID2D1RenderTarget target = _d2dFactory.CreateWicBitmapRenderTarget(
                bitmap,
                new RenderTargetProperties(new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied)));

            target.BeginDraw();
            target.Clear(new Color4(0f, 0f, 0f, 0f));
            if (timing.ClearsPage)
            {
                var fadeOut = timing.GetPageFadeOutProgress(position);
                for (var i = 0; i < outgoingLayouts.Length; i++)
                {
                    var relativeLine = i - timing.OutgoingActiveLineIndex;
                    DrawKaraokeLine(
                        target,
                        format,
                        timing.OutgoingPageLines[i],
                        outgoingLayouts[i],
                        middleY + (relativeLine * slotDistance),
                        GetStableLineOpacity(relativeLine) * (1 - fadeOut),
                        bounds,
                        baseStyle,
                        wipeStyle,
                        position,
                        width,
                        height,
                        layoutWidth,
                        padding);
                }

                var fadeIn = timing.GetPageFadeInProgress(position, transitionDuration);
                for (var i = 0; i < layouts.Length; i++)
                {
                    var relativeLine = i - timing.ActiveLineIndex;
                    DrawKaraokeLine(
                        target,
                        format,
                        timing.Lines[i],
                        layouts[i],
                        Lerp(middleY + ((relativeLine + 1) * slotDistance), middleY + (relativeLine * slotDistance), fadeIn),
                        GetStableLineOpacity(relativeLine) * fadeIn,
                        bounds,
                        baseStyle,
                        wipeStyle,
                        position,
                        width,
                        height,
                        layoutWidth,
                        padding);
                }
            }
            else
            {
                var progress = timing.GetTransitionProgress(position, transitionDuration);
                for (var i = 0; i < layouts.Length; i++)
                {
                    var relativeLine = i - timing.ActiveLineIndex;
                    (var centerY, var opacity) = GetAnimatedLineState(
                        relativeLine,
                        progress,
                        middleY,
                        slotDistance,
                        timing.ActiveLineIndex > 0);
                    DrawKaraokeLine(
                        target,
                        format,
                        timing.Lines[i],
                        layouts[i],
                        centerY,
                        opacity,
                        bounds,
                        baseStyle,
                        wipeStyle,
                        position,
                        width,
                        height,
                        layoutWidth,
                        padding);
                }
            }
            _ = target.EndDraw();

            var stride = bounds.Width * 4;
            var pixels = new byte[checked(stride * bounds.Height)];
            bitmap.CopyPixels((uint)stride, pixels);
            return new SubtitleRenderResult(
                pixels,
                bounds.Width,
                bounds.Height,
                stride,
                bounds.Left,
                bounds.Top,
                width,
                height);
        }
        finally
        {
            foreach (IDWriteTextLayout? layout in layouts)
                layout.Dispose();
            foreach (IDWriteTextLayout? layout in outgoingLayouts)
                layout.Dispose();
        }
    }

    private void DrawKaraokeLine(
        ID2D1RenderTarget target,
        IDWriteTextFormat format,
        KaraokeLineTiming line,
        IDWriteTextLayout layout,
        float centerY,
        float opacity,
        RenderBounds bounds,
        KaraokeLayerStyle baseStyle,
        KaraokeLayerStyle wipeStyle,
        TimeSpan position,
        int width,
        int height,
        float layoutWidth,
        int padding)
    {
        if (opacity <= 0)
            return;

        TextMetrics metrics = layout.Metrics;
        var y = centerY - (metrics.Height / 2);
        var canvasOrigin = new Vector2(width * 0.075f, y);
        Vector2 origin = canvasOrigin - new Vector2(bounds.Left, bounds.Top);
        var left = canvasOrigin.X + Math.Max(0, (layoutWidth - metrics.Width) / 2);

        using (ID2D1SolidColorBrush textBrush = target.CreateSolidColorBrush(ToColor(baseStyle.TextArgb, opacity)))
        using (ID2D1SolidColorBrush outlineBrush = target.CreateSolidColorBrush(ToColor(baseStyle.OutlineArgb, opacity)))
        using (ID2D1SolidColorBrush shadowBrush = target.CreateSolidColorBrush(ToColor(baseStyle.ShadowArgb, opacity)))
            DrawTextLayer(target, layout, origin, textBrush, outlineBrush, shadowBrush, baseStyle);

        var highlightedLength = line.GetHighlightedLength(position);
        if (highlightedLength <= 0)
            return;

        var highlightedWidth = metrics.Width;
        if (highlightedLength < line.Text.Length)
        {
            var prefix = line.Text[..highlightedLength];
            using IDWriteTextLayout prefixLayout = _writeFactory.CreateTextLayout(prefix, format, layoutWidth, height * 0.2f);
            highlightedWidth = prefixLayout.Metrics.Width;
        }

        var clip = new Vortice.RawRectF(
            Math.Max(0, left - bounds.Left - padding),
            Math.Max(0, y - bounds.Top - padding),
            Math.Min(bounds.Width, left - bounds.Left + highlightedWidth + padding),
            Math.Min(bounds.Height, y - bounds.Top + metrics.Height + padding));
        target.PushAxisAlignedClip(clip, AntialiasMode.PerPrimitive);
        using (ID2D1SolidColorBrush textBrush = target.CreateSolidColorBrush(ToColor(wipeStyle.TextArgb, opacity)))
        using (ID2D1SolidColorBrush outlineBrush = target.CreateSolidColorBrush(ToColor(wipeStyle.OutlineArgb, opacity)))
        using (ID2D1SolidColorBrush shadowBrush = target.CreateSolidColorBrush(ToColor(wipeStyle.ShadowArgb, opacity)))
            DrawTextLayer(target, layout, origin, textBrush, outlineBrush, shadowBrush, wipeStyle);
        target.PopAxisAlignedClip();
    }

    private void DrawTextLayer(
        ID2D1RenderTarget target,
        IDWriteTextLayout layout,
        Vector2 origin,
        ID2D1SolidColorBrush textBrush,
        ID2D1SolidColorBrush outlineBrush,
        ID2D1SolidColorBrush shadowBrush,
        KaraokeLayerStyle style)
    {
        if ((style.ShadowArgb >> 24) != 0)
            target.DrawTextLayout(origin + new Vector2(_karaokeStyle.ShadowOffsetX, _karaokeStyle.ShadowOffsetY), layout, shadowBrush);

        if (style.OutlineWidth > 0 && (style.OutlineArgb >> 24) != 0)
        {
            for (var x = -1; x <= 1; x++)
            {
                for (var y = -1; y <= 1; y++)
                {
                    if (x != 0 || y != 0)
                        target.DrawTextLayout(origin + new Vector2(x * style.OutlineWidth, y * style.OutlineWidth), layout, outlineBrush);
                }
            }
        }

        target.DrawTextLayout(origin, layout, textBrush);
    }

    private (float CenterY, float Opacity) GetAnimatedLineState(
        int relativeLine,
        float progress,
        float middleY,
        float slotDistance,
        bool hasPreviousLine)
    {
        var previousRelativeLine = relativeLine + 1;
        return (
            Lerp(
                middleY + (previousRelativeLine * slotDistance),
                middleY + (relativeLine * slotDistance),
                progress),
            Lerp(
                hasPreviousLine ? GetStableLineOpacity(previousRelativeLine) : 0,
                GetStableLineOpacity(relativeLine),
                progress));
    }

    private float GetStableLineOpacity(int relativeLine) => relativeLine switch
    {
        0 => 1,
        -1 => 0.5f,
        -2 => 0.25f,
        1 => _karaokeStyle.NextLineOpacity,
        2 => _karaokeStyle.NextLineOpacity * 0.55f,
        _ => 0,
    };

    private static float Lerp(float start, float end, float amount) => start + ((end - start) * amount);

    private static RenderBounds CreateBounds(
        float left,
        float top,
        float right,
        float bottom,
        int canvasWidth,
        int canvasHeight,
        float outlineWidth,
        float shadowOffsetX,
        float shadowOffsetY)
    {
        var padding = Math.Max(2, (int)Math.Ceiling(outlineWidth) + 2);
        var boundsLeft = Math.Clamp((int)Math.Floor(left + Math.Min(0, shadowOffsetX)) - padding, 0, canvasWidth - 1);
        var boundsTop = Math.Clamp((int)Math.Floor(top + Math.Min(0, shadowOffsetY)) - padding, 0, canvasHeight - 1);
        var boundsRight = Math.Clamp((int)Math.Ceiling(right + Math.Max(0, shadowOffsetX)) + padding, boundsLeft + 1, canvasWidth);
        var boundsBottom = Math.Clamp((int)Math.Ceiling(bottom + Math.Max(0, shadowOffsetY)) + padding, boundsTop + 1, canvasHeight);
        return new RenderBounds(boundsLeft, boundsTop, boundsRight - boundsLeft, boundsBottom - boundsTop);
    }

    private static Color4 ToColor(uint argb) => new(
        ((argb >> 16) & 0xFF) / 255f,
        ((argb >> 8) & 0xFF) / 255f,
        (argb & 0xFF) / 255f,
        ((argb >> 24) & 0xFF) / 255f);

    private static Color4 ToColor(uint argb, float opacity) => new(
        ((argb >> 16) & 0xFF) / 255f,
        ((argb >> 8) & 0xFF) / 255f,
        (argb & 0xFF) / 255f,
        ((argb >> 24) & 0xFF) / 255f * Math.Clamp(opacity, 0, 1));

    public void Dispose()
    {
        _wicFactory.Dispose();
        _writeFactory.Dispose();
        _d2dFactory.Dispose();
    }

    private readonly record struct KaraokeLayerStyle(uint TextArgb, uint OutlineArgb, float OutlineWidth, uint ShadowArgb);
    private readonly record struct RenderBounds(int Left, int Top, int Width, int Height);
}
