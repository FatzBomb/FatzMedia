using System.Runtime.InteropServices;
using FatzBomb.MediaControl.Core.Audio;
using FatzBomb.MediaControl.Core.Configuration;
using FatzBomb.MediaControl.Core.Subtitle;
using SkiaSharp;

namespace FatzBomb.MediaControl.Rendering.Text;

/// <summary>
/// SkiaSharp subtitle and karaoke rasterizer for the software and Vulkan renderers. It ports the
/// Direct3D11 renderer's DirectWrite rasterizer (layout, outline, shadow, karaoke wipe, countdown
/// arrows, intro and end cards, scroll and page transitions) and returns the same raster:
/// premultiplied BGRA8 bounds on a canvas of the requested size.
/// </summary>
/// <remarks>
/// A karaoke wipe redraws on every frame, so text layouts (with their glyph runs) are cached and an
/// entry not used by a frame is disposed at the end of it, as the DirectWrite rasterizer does; the
/// bitmap and canvas are kept while the raster size stays the same. Calls are serialized by a lock.
/// </remarks>
public sealed class SkiaSubtitleRasterizer : ISubtitleRasterizer
{
    private const int NormalWeight = 400;
    private const int SemiBoldWeight = 600;
    private const int BoldWeight = 700;

    private static readonly Func<int, Memory<byte>> AllocatePixels = static length => new byte[length];

    private readonly SubtitleTextStyle _style;
    private readonly KaraokeTextStyle _karaokeStyle;

    // The caches are used by one frame at a time.
    private readonly Lock _gate = new();
    private readonly Dictionary<FaceKey, SkiaFontFace> _faces = [];
    private readonly Dictionary<LayoutKey, CacheEntry> _layouts = [];
    private readonly List<SkiaTextLayout> _lineLayouts = [];
    // Created by the first frame, so a missing native SkiaSharp library fails a frame (which Core
    // reports as a Subtitle warning) instead of the renderer factory.
    private SKPaint? _paint;
    private SKPaint? _layerPaint;
    // Set when the native library failed to load: later frames draw nothing instead of throwing
    // (and raising a warning) on every karaoke progress step.
    private bool _nativeUnavailable;
    private long _generation;
    private SKBitmap? _bitmap;
    private SKCanvas? _canvas;
    private bool _disposed;

    /// <summary>Creates a rasterizer with the given styles (the defaults when <see langword="null"/>).</summary>
    public SkiaSubtitleRasterizer(SubtitleTextStyle? style = null, KaraokeTextStyle? karaokeStyle = null)
    {
        _style = (style ?? new SubtitleTextStyle()).Normalize();
        _karaokeStyle = (karaokeStyle ?? new KaraokeTextStyle()).Normalize();
    }

    public SubtitleRasterResult Render(string text, int width, int height) =>
        Render(text, width, height, AllocatePixels);

    public SubtitleRasterResult RenderKaraoke(KaraokeCueTiming timing, TimeSpan position, int width, int height) =>
        RenderKaraoke(timing, position, width, height, AllocatePixels);

    public SubtitleRasterResult Render(string text, int width, int height, Func<int, Memory<byte>> rentPixels)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentNullException.ThrowIfNull(rentPixels);

        // Nothing to draw: the empty result NullSubtitleRasterizer returns, and no buffer is rented.
        if (string.IsNullOrWhiteSpace(text))
            return default;

        lock (_gate)
        {
            if (!BeginFrame())
                return default;
            try
            {
                return RenderTextFrame(text, width, height, rentPixels);
            }
            catch
            {
                // A failure mid-draw may leave saved canvas state behind: start from a new one.
                ReleaseTarget();
                throw;
            }
            finally
            {
                EndFrame();
            }
        }
    }

    public SubtitleRasterResult RenderKaraoke(KaraokeCueTiming timing, TimeSpan position, int width, int height, Func<int, Memory<byte>> rentPixels)
    {
        ArgumentNullException.ThrowIfNull(timing);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentNullException.ThrowIfNull(rentPixels);

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

        lock (_gate)
        {
            if (!BeginFrame())
                return default;
            try
            {
                return RenderKaraokeFrame(timing, position, baseStyle, wipeStyle, width, height, rentPixels);
            }
            catch
            {
                ReleaseTarget();
                throw;
            }
            finally
            {
                EndFrame();
            }
        }
    }

    private SubtitleRasterResult RenderTextFrame(string text, int width, int height, Func<int, Memory<byte>> rentPixels)
    {
        SkiaTextLayout layout = GetLayout(
            text,
            new FormatKey(_style.FontFamily, BoldWeight, false, _style.FontSize),
            width * 0.85f,
            center: true);
        var canvasOriginX = width * 0.075f;
        var canvasOriginY = Math.Max(0, height - layout.Height - (height * 0.08f));
        var textLeft = canvasOriginX + Math.Max(0, ((width * 0.85f) - layout.Width) / 2);
        RenderBounds bounds = CreateBounds(
            textLeft,
            canvasOriginY,
            textLeft + layout.Width,
            canvasOriginY + layout.Height,
            width,
            height,
            _style.OutlineWidth,
            _style.ShadowOffsetX,
            _style.ShadowOffsetY);

        SKCanvas canvas = GetTarget(bounds.Width, bounds.Height);
        var originX = canvasOriginX - bounds.Left;
        var originY = canvasOriginY - bounds.Top;

        canvas.Clear(SKColors.Transparent);
        if ((_style.ShadowArgb >> 24) != 0)
            DrawLayout(canvas, layout, originX + _style.ShadowOffsetX, originY + _style.ShadowOffsetY, ToColor(_style.ShadowArgb));

        if (_style.OutlineWidth > 0 && (_style.OutlineArgb >> 24) != 0)
        {
            SKColor outline = ToColor(_style.OutlineArgb);
            for (var x = -1; x <= 1; x++)
            {
                for (var y = -1; y <= 1; y++)
                {
                    if (x != 0 || y != 0)
                        DrawLayout(canvas, layout, originX + (x * _style.OutlineWidth), originY + (y * _style.OutlineWidth), outline);
                }
            }
        }

        DrawLayout(canvas, layout, originX, originY, ToColor(_style.TextArgb));
        return CopyPixels(bounds, width, height, rentPixels);
    }

    private SubtitleRasterResult RenderKaraokeFrame(
        KaraokeCueTiming timing,
        TimeSpan position,
        KaraokeLayerStyle baseStyle,
        KaraokeLayerStyle wipeStyle,
        int width,
        int height,
        Func<int, Memory<byte>> rentPixels)
    {
        var lyricFormat = new FormatKey(_karaokeStyle.FontFamily, BoldWeight, false, _karaokeStyle.FontSize);
        var countdownFormat = new FormatKey(_karaokeStyle.FontFamily, BoldWeight, false, _karaokeStyle.FontSize * 0.5f);
        SkiaTextLayout countdownArrowLayout = GetLayout("▶", countdownFormat, width, center: false);
        var layoutWidth = width * 0.85f;
        var wipeDuration = TimeSpan.FromMilliseconds(_karaokeStyle.WipeTransitionMilliseconds);

        // Current page lines first, then the outgoing page's, as one list.
        IReadOnlyList<KaraokeLineTiming> lines = timing.Lines;
        IReadOnlyList<KaraokeLineTiming> outgoingLines = timing.OutgoingPageLines;
        List<SkiaTextLayout> allLayouts = _lineLayouts;
        allLayouts.Clear();
        for (var i = 0; i < lines.Count; i++)
            allLayouts.Add(GetLayout(lines[i].Text, lyricFormat, layoutWidth, center: true));
        for (var i = 0; i < outgoingLines.Count; i++)
            allLayouts.Add(GetLayout(outgoingLines[i].Text, lyricFormat, layoutWidth, center: true));
        if (allLayouts.Count == 0)
            throw new InvalidOperationException("The karaoke cue has no lines.");

        var introMetadataOpacity = timing.GetIntroMetadataOpacity(position);
        var endMetadataOpacity = timing.GetEndMetadataOpacity(position);
        var hasPageScaleEffect = timing.ClearsPage
            && ((position >= timing.PageFadeOutStart && position < timing.PageFadeOutEnd)
                || (position >= timing.PageFadeInStart && position < timing.PageFadeInEnd));
        var hasFullCanvasOverlay = introMetadataOpacity > 0
            || endMetadataOpacity > 0
            || hasPageScaleEffect;
        var transitionDuration = TimeSpan.FromMilliseconds(_karaokeStyle.LineTransitionMilliseconds);
        var middleY = height * 0.5f;
        var slotDistance = _karaokeStyle.FontSize * 1.55f;
        var padding = (int)Math.Ceiling(Math.Max(
            Math.Max(_karaokeStyle.OutlineWidth, _karaokeStyle.WipeOutlineWidth),
            Math.Max(Math.Abs(_karaokeStyle.ShadowOffsetX), Math.Abs(_karaokeStyle.ShadowOffsetY))));
        var countdownArrowGap = _karaokeStyle.FontSize * 0.12f;
        var countdownWidth = (countdownArrowLayout.Width * 4) + (countdownArrowGap * 3);
        var maxLineHeight = float.MinValue; // max scale is 1.0
        var minTextLeft = float.MaxValue;
        var maxTextRight = float.MinValue;
        var anyCountdownVisible = false;
        for (var i = 0; i < allLayouts.Count; i++)
        {
            SkiaTextLayout layout = allLayouts[i];
            KaraokeLineTiming line = i < lines.Count ? lines[i] : outgoingLines[i - lines.Count];
            var textLeft = (width * 0.075f) + Math.Max(0, (layoutWidth - layout.Width) / 2);
            var textRight = textLeft + layout.Width;
            maxLineHeight = Math.Max(maxLineHeight, layout.Height);
            minTextLeft = Math.Min(minTextLeft, textLeft);
            if (line.GetCountdownState(position).IsVisible)
            {
                anyCountdownVisible = true;

                // Scaled lyric lines move their visual left edge toward the center.
                // Reserve from the center so all four fixed arrow slots remain inside the bitmap.
                var layoutCenter = (width * 0.075f) + (layoutWidth / 2);
                textRight = Math.Max(textRight, layoutCenter + countdownWidth);
            }

            maxTextRight = Math.Max(maxTextRight, textRight);
        }

        var countdownTopExtension = anyCountdownVisible
            ? countdownArrowLayout.Height + (_karaokeStyle.FontSize * 0.1f)
            : 0;
        RenderBounds bounds = hasFullCanvasOverlay
            ? new RenderBounds(0, 0, width, height)
            : CreateBounds(
                minTextLeft,
                middleY - (slotDistance * 2) - (maxLineHeight / 2) - countdownTopExtension,
                maxTextRight,
                middleY + (slotDistance * 3) + (maxLineHeight / 2),
                width,
                height,
                padding,
                _karaokeStyle.ShadowOffsetX,
                _karaokeStyle.ShadowOffsetY);

        SKCanvas canvas = GetTarget(bounds.Width, bounds.Height);
        canvas.Clear(SKColors.Transparent);
        if (introMetadataOpacity > 0
            && timing.UseSplitMetadataBands
            && timing.Metadata is { } splitMetadata)
        {
            DrawSplitMetadataBands(canvas, splitMetadata, introMetadataOpacity, width, height, bounds);
        }

        var endLyricOpacity = timing.GetEndLyricOpacity(position);
        var context = new LineContext(countdownArrowLayout, bounds, baseStyle, wipeStyle, position, wipeDuration, width, height, layoutWidth, padding);
        if (timing.ClearsPage)
        {
            var fadeOut = timing.GetPageFadeOutProgress(position);
            for (var i = 0; i < outgoingLines.Count; i++)
            {
                var relativeLine = i - timing.OutgoingActiveLineIndex;
                DrawKaraokeLine(
                    canvas,
                    context,
                    outgoingLines[i],
                    middleY + (relativeLine * slotDistance),
                    GetStableLineOpacity(relativeLine) * (1 - fadeOut) * endLyricOpacity,
                    relativeLine,
                    1,
                    Lerp(1, 0.75f, fadeOut));
            }

            var fadeIn = timing.GetPageFadeInProgress(position, transitionDuration);
            for (var i = 0; i < lines.Count; i++)
            {
                var relativeLine = i - timing.ActiveLineIndex;
                DrawKaraokeLine(
                    canvas,
                    context,
                    lines[i],
                    middleY + (relativeLine * slotDistance),
                    GetStableLineOpacity(relativeLine) * fadeIn * endLyricOpacity,
                    relativeLine,
                    1,
                    Lerp(1.5f, 1, fadeIn));
            }
        }
        else
        {
            var progress = timing.GetTransitionProgress(position);
            var hasPreviousLine = timing.ActiveLineIndex > 0;
            for (var i = 0; i < lines.Count; i++)
            {
                var relativeLine = i - timing.ActiveLineIndex;
                (var centerY, var opacity) = GetAnimatedLineState(relativeLine, progress, middleY, slotDistance, hasPreviousLine);
                DrawKaraokeLine(
                    canvas,
                    context,
                    lines[i],
                    centerY,
                    opacity * endLyricOpacity,
                    relativeLine,
                    hasPreviousLine ? progress : 1,
                    1);
            }
        }

        if (introMetadataOpacity > 0
            && !timing.UseSplitMetadataBands
            && timing.Metadata is { } cardMetadata)
        {
            DrawMetadataCard(canvas, cardMetadata, introMetadataOpacity, width, height, bounds, isEndCard: false);
        }

        if (endMetadataOpacity > 0 && timing.Metadata is { } endMetadata)
            DrawMetadataCard(canvas, endMetadata, endMetadataOpacity, width, height, bounds, isEndCard: true);

        allLayouts.Clear();
        return CopyPixels(bounds, width, height, rentPixels);
    }

    private void DrawKaraokeLine(
        SKCanvas canvas,
        in LineContext context,
        KaraokeLineTiming line,
        float centerY,
        float opacity,
        int relativeLine,
        float scrollProgress,
        float presentationScale)
    {
        if (opacity <= 0)
            return;

        RenderBounds bounds = context.Bounds;
        var padding = context.Padding;
        var scale = GetLineScale(relativeLine, scrollProgress);
        var totalScale = scale * presentationScale;
        var effectiveFontSize = _karaokeStyle.FontSize * totalScale;
        var scaledLayoutWidth = context.LayoutWidth * totalScale;
        var scaledFormat = new FormatKey(_karaokeStyle.FontFamily, BoldWeight, false, effectiveFontSize);
        SkiaTextLayout scaledLayout = GetLayout(line.Text, scaledFormat, scaledLayoutWidth, center: true);
        var lineHeight = scaledLayout.Height;
        var y = centerY - (lineHeight / 2);
        var canvasOriginX = (context.Width - scaledLayoutWidth) / 2;
        var originX = canvasOriginX - bounds.Left;
        var originY = y - bounds.Top;
        var left = canvasOriginX + Math.Max(0, (scaledLayoutWidth - scaledLayout.Width) / 2);
        var descenderPadding = effectiveFontSize * 0.35f;
        var layerLeft = Math.Max(0, left - bounds.Left - padding);
        var layerTop = Math.Max(0, y - bounds.Top - context.CountdownArrowLayout.Height - (_karaokeStyle.FontSize * 0.1f) - padding);
        var layerRight = Math.Min(bounds.Width, left - bounds.Left + scaledLayout.Width + padding);
        var layerBottom = Math.Min(bounds.Height, y - bounds.Top + lineHeight + descenderPadding + padding);

        // Fade the composed line once so overlapping text, outline, and wipe passes retain their original colors.
        _layerPaint!.Color = new SKColor(0, 0, 0, ToByte(opacity));
        _ = canvas.SaveLayer(new SKRect(layerLeft, layerTop, layerRight, layerBottom), _layerPaint);
        try
        {
            DrawTextLayer(canvas, scaledLayout, originX, originY, context.BaseStyle, context.BaseStyle.TextArgb, context.BaseStyle.OutlineArgb, context.BaseStyle.ShadowArgb);

            DrawCountdownArrows(canvas, context.CountdownArrowLayout, line, left - bounds.Left, y - bounds.Top, 1, context.Position);

            var highlightedLength = line.GetHighlightedLength(context.Position);
            if (highlightedLength <= 0)
                return;

            // Smooth wipe: two-pass approach.
            // Pass 1: fully-highlighted syllables (before current) at full wipe opacity.
            // Pass 2: current transitioning syllable blended from base color to wipe color.
            var wipeFraction = line.GetSyllableWipeFraction(context.Position, context.WipeDuration);
            var easedWipeFraction = EaseInOutQuint(wipeFraction);

            // Find the boundary between fully-highlighted and transitioning syllables
            var fullyHighlightedLength = line.GetFullyHighlightedLength(context.Position, context.WipeDuration);

            var fullyHighlightedWidth = 0f;
            if (fullyHighlightedLength > 0 && fullyHighlightedLength < line.Text.Length)
                fullyHighlightedWidth = MeasurePrefixWidth(scaledLayout, line.Text, fullyHighlightedLength, scaledFormat, scaledLayoutWidth);
            else if (fullyHighlightedLength >= line.Text.Length)
                fullyHighlightedWidth = scaledLayout.Width;

            float transitioningWidth;
            if (highlightedLength < line.Text.Length)
            {
                transitioningWidth = MeasurePrefixWidth(scaledLayout, line.Text, highlightedLength, scaledFormat, scaledLayoutWidth);

                // Extend to include partial next syllable
                if (easedWipeFraction > 0)
                {
                    var nextWidth = MeasurePrefixWidth(scaledLayout, line.Text, highlightedLength + 1, scaledFormat, scaledLayoutWidth);
                    transitioningWidth += (nextWidth - transitioningWidth) * easedWipeFraction;
                }
            }
            else
            {
                transitioningWidth = scaledLayout.Width;
            }

            KaraokeLayerStyle baseLayer = context.BaseStyle;
            KaraokeLayerStyle wipeLayer = context.WipeStyle;

            // Pass 1: fully-highlighted syllables at full wipe opacity
            if (fullyHighlightedWidth > 0)
            {
                var clip = new SKRect(
                    Math.Max(0, left - bounds.Left - padding),
                    Math.Max(0, y - bounds.Top - padding),
                    Math.Min(bounds.Width, left - bounds.Left + fullyHighlightedWidth + padding),
                    Math.Min(bounds.Height, y - bounds.Top + lineHeight + descenderPadding + padding));
                _ = canvas.Save();
                try
                {
                    canvas.ClipRect(clip, SKClipOperation.Intersect, antialias: true);
                    DrawTextLayer(canvas, scaledLayout, originX, originY, wipeLayer, wipeLayer.TextArgb, wipeLayer.OutlineArgb, wipeLayer.ShadowArgb);
                }
                finally
                {
                    canvas.Restore();
                }
            }

            // Pass 2: current transitioning syllable — draw with interpolated color from base to wipe.
            // Instead of overlaying wipe at low opacity (which darkens through the outline/shadow),
            // we redraw the syllable region with a color that blends directly from base to wipe.
            if (transitioningWidth > fullyHighlightedWidth)
            {
                var clip = new SKRect(
                    Math.Max(0, left - bounds.Left + fullyHighlightedWidth - padding),
                    Math.Max(0, y - bounds.Top - padding),
                    Math.Min(bounds.Width, left - bounds.Left + transitioningWidth + padding),
                    Math.Min(bounds.Height, y - bounds.Top + lineHeight + descenderPadding + padding));
                _ = canvas.Save();
                try
                {
                    canvas.ClipRect(clip, SKClipOperation.Intersect, antialias: true);

                    // Interpolate colors directly from base to wipe to avoid alpha blending artifacts.
                    DrawTextLayer(
                        canvas,
                        scaledLayout,
                        originX,
                        originY,
                        wipeLayer,
                        LerpColor(baseLayer.TextArgb, wipeLayer.TextArgb, easedWipeFraction),
                        LerpColor(baseLayer.OutlineArgb, wipeLayer.OutlineArgb, easedWipeFraction),
                        LerpColor(baseLayer.ShadowArgb, wipeLayer.ShadowArgb, easedWipeFraction));
                }
                finally
                {
                    canvas.Restore();
                }
            }
        }
        finally
        {
            canvas.Restore();
        }
    }

    /// <summary>
    /// The width a layout of just <c>text[..length]</c> would measure (trailing whitespace
    /// excluded), read from the line's own layout. A wrapped line measures a real prefix layout,
    /// which wraps on its own.
    /// </summary>
    private float MeasurePrefixWidth(SkiaTextLayout layout, string text, int length, FormatKey format, float maxWidth) =>
        layout.LineCount > 1
            ? GetLayout(text[..length], format, maxWidth, center: true).Width
            : layout.MeasurePrefixWidth(length);

    private void DrawMetadataCard(
        SKCanvas canvas,
        KaraokeMetadata metadata,
        float opacity,
        int canvasWidth,
        int canvasHeight,
        RenderBounds bounds,
        bool isEndCard)
    {
        var titleFontSize = _karaokeStyle.FontSize * (metadata.IsFallback ? 0.65f : 0.92f);
        var artistFontSize = _karaokeStyle.FontSize * 0.55f;
        var infoFontSize = _karaokeStyle.FontSize * 0.31f;
        var copyrightFontSize = _karaokeStyle.FontSize * 0.23f;

        var titleFormat = new FormatKey(_karaokeStyle.FontFamily, BoldWeight, false, titleFontSize);
        var artistFormat = new FormatKey(_karaokeStyle.FontFamily, SemiBoldWeight, false, artistFontSize);
        var infoFormat = new FormatKey(_karaokeStyle.FontFamily, NormalWeight, false, infoFontSize);
        var copyrightFormat = new FormatKey(_karaokeStyle.FontFamily, NormalWeight, true, copyrightFontSize);

        var boxWidth = canvasWidth * (metadata.IsFallback ? 0.5f : 0.72f);
        var textWidth = boxWidth - (_karaokeStyle.FontSize * 0.9f);
        SkiaTextLayout titleLayout = GetLayout(metadata.Title, titleFormat, textWidth, center: true);
        SkiaTextLayout artistLayout = GetLayout(string.IsNullOrEmpty(metadata.Artist) ? "" : metadata.Artist, artistFormat, textWidth, center: true);
        var infoText = BuildMetadataDetails(metadata, isEndCard);
        SkiaTextLayout infoLayout = GetLayout(infoText, infoFormat, textWidth, center: true);
        SkiaTextLayout copyrightLayout = GetLayout(metadata.Copyright, copyrightFormat, textWidth, center: true);

        var gap = _karaokeStyle.FontSize * 0.13f;
        var sectionGap = _karaokeStyle.FontSize * 0.2f;
        var padding = _karaokeStyle.FontSize * 0.45f;
        var contentHeight = titleLayout.Height
            + (string.IsNullOrEmpty(metadata.Artist) ? 0 : artistLayout.Height + gap)
            + (string.IsNullOrEmpty(infoText) ? 0 : infoLayout.Height + sectionGap)
            + (string.IsNullOrEmpty(metadata.Copyright) ? 0 : copyrightLayout.Height + sectionGap);
        var boxHeight = contentHeight + (padding * 2);
        var boxLeft = (canvasWidth - boxWidth) / 2f;
        var boxTop = (canvasHeight - boxHeight) / 2f;

        DrawSmokedGlassPanel(
            canvas,
            SKRect.Create(boxLeft - bounds.Left, boxTop - bounds.Top, boxWidth, boxHeight),
            _karaokeStyle.FontSize * 0.22f,
            opacity,
            0xFF62D6E8);

        var textY = boxTop + padding;
        var textX = boxLeft + ((boxWidth - textWidth) / 2) - bounds.Left;
        SKColor shadow = ToColor(0xD0000000, opacity);
        DrawMetadataText(canvas, titleLayout, textX, textY - bounds.Top, ToColor(0xFFFFF4DC, opacity), shadow, 2.2f);
        textY += titleLayout.Height;

        if (!string.IsNullOrEmpty(metadata.Artist))
        {
            textY += gap;
            DrawMetadataText(canvas, artistLayout, textX, textY - bounds.Top, ToColor(0xFF78DCE8, opacity), shadow, 1.5f);
            textY += artistLayout.Height;
        }

        if (!string.IsNullOrEmpty(infoText))
        {
            textY += sectionGap;
            DrawMetadataText(canvas, infoLayout, textX, textY - bounds.Top, ToColor(0xFFD5DCE6, opacity), shadow, 1f);
            textY += infoLayout.Height;
        }

        if (!string.IsNullOrEmpty(metadata.Copyright))
        {
            textY += sectionGap;
            DrawMetadataText(canvas, copyrightLayout, textX, textY - bounds.Top, ToColor(0xFFB8A87A, opacity), shadow, 1f);
        }
    }

    private void DrawSplitMetadataBands(
        SKCanvas canvas,
        KaraokeMetadata metadata,
        float opacity,
        int canvasWidth,
        int canvasHeight,
        RenderBounds bounds)
    {
        var topHeight = canvasHeight * 0.17f;
        var bottomHeight = canvasHeight * 0.16f;
        DrawSmokedGlassBand(canvas, SKRect.Create(-bounds.Left, -bounds.Top, canvasWidth, topHeight), opacity, false);
        DrawSmokedGlassBand(canvas, SKRect.Create(-bounds.Left, canvasHeight - bottomHeight - bounds.Top, canvasWidth, bottomHeight), opacity, true);

        var titleSize = _karaokeStyle.FontSize * (metadata.IsFallback ? 0.52f : 0.68f);
        var secondarySize = _karaokeStyle.FontSize * 0.36f;
        var detailSize = _karaokeStyle.FontSize * 0.27f;
        var titleFormat = new FormatKey(_karaokeStyle.FontFamily, BoldWeight, false, titleSize);
        var secondaryFormat = new FormatKey(_karaokeStyle.FontFamily, SemiBoldWeight, false, secondarySize);
        var detailFormat = new FormatKey(_karaokeStyle.FontFamily, NormalWeight, false, detailSize);
        var textWidth = canvasWidth * 0.88f;
        SkiaTextLayout titleLayout = GetLayout(metadata.Title, titleFormat, textWidth, center: true);
        var secondary = string.Join("  •  ", new[] { metadata.Artist, metadata.Year }.Where(static value => !string.IsNullOrWhiteSpace(value)));
        SkiaTextLayout secondaryLayout = GetLayout(secondary, secondaryFormat, textWidth, center: true);
        SkiaTextLayout bottomLayout = GetLayout(BuildBandDetails(metadata), detailFormat, textWidth, center: true);

        SKColor shadow = ToColor(0xD0000000, opacity);
        var x = ((canvasWidth - textWidth) / 2f) - bounds.Left;
        var titleY = (topHeight * 0.12f) - bounds.Top;
        DrawMetadataText(canvas, titleLayout, x, titleY, ToColor(0xFFFFF4DC, opacity), shadow, 1.8f);
        if (secondary.Length > 0)
            DrawMetadataText(canvas, secondaryLayout, x, titleY + titleLayout.Height, ToColor(0xFF78DCE8, opacity), shadow, 1.2f);
        DrawMetadataText(canvas, bottomLayout, x, canvasHeight - bottomHeight + (bottomHeight * 0.14f) - bounds.Top, ToColor(0xFFD5DCE6, opacity), shadow, 1f);
    }

    private static string BuildMetadataDetails(KaraokeMetadata metadata, bool isEndCard)
    {
        var lines = new List<string>();
        if (!isEndCard && !metadata.IsFallback)
        {
            var credits = BuildWriterCredits(metadata);
            if (credits.Length > 0)
                lines.Add(credits);
        }
        if (!string.IsNullOrWhiteSpace(metadata.Year))
            lines.Add(metadata.Year);
        if (!isEndCard)
        {
            lines.Add($"Length  {FormatDuration(metadata.Duration)}");
            var music = BuildMusicDetails(metadata);
            if (music.Length > 0)
                lines.Add(music);
            if (!metadata.IsFallback && metadata.AdditionalInfo.Count > 0)
                lines.Add(string.Join("  •  ", metadata.AdditionalInfo));
        }
        return string.Join('\n', lines);
    }

    private static string BuildBandDetails(KaraokeMetadata metadata)
    {
        var lines = new List<string> { $"Length  {FormatDuration(metadata.Duration)}" };
        var music = BuildMusicDetails(metadata);
        if (music.Length > 0)
            lines.Add(music);
        if (!string.IsNullOrWhiteSpace(metadata.Copyright))
            lines.Add(metadata.Copyright);
        return string.Join('\n', lines);
    }

    private static string BuildWriterCredits(KaraokeMetadata metadata) =>
        !string.IsNullOrWhiteSpace(metadata.Writer) && !string.IsNullOrWhiteSpace(metadata.Lyricist)
            ? string.Equals(metadata.Writer, metadata.Lyricist, StringComparison.OrdinalIgnoreCase)
                ? $"Written By / Lyricist:  {metadata.Writer}"
                : $"Written By:  {metadata.Writer}  •  Lyricist:  {metadata.Lyricist}"
            : !string.IsNullOrWhiteSpace(metadata.Writer)
                ? $"Written By:  {metadata.Writer}"
                : string.IsNullOrWhiteSpace(metadata.Lyricist) ? string.Empty : $"Lyricist:  {metadata.Lyricist}";

    private static string BuildMusicDetails(KaraokeMetadata metadata)
    {
        var details = new List<string>();
        if (!string.IsNullOrWhiteSpace(metadata.KeySignature))
            details.Add($"Key  {metadata.KeySignature}");
        if (!string.IsNullOrWhiteSpace(metadata.Tempo))
            details.Add($"Tempo  {metadata.Tempo}");
        if (!string.IsNullOrWhiteSpace(metadata.TimeSignature))
            details.Add($"Timing  {metadata.TimeSignature}");
        return string.Join("  •  ", details);
    }

    private static string FormatDuration(TimeSpan duration) => duration.TotalHours >= 1
        ? $"{(int)duration.TotalHours}:{duration.Minutes:00}:{duration.Seconds:00}"
        : $"{(int)duration.TotalMinutes}:{duration.Seconds:00}";

    private void DrawSmokedGlassPanel(SKCanvas canvas, SKRect rect, float radius, float opacity, uint accentArgb)
    {
        SKPaint paint = _paint!;
        paint.Color = ToColor(0f, 0f, 0f, 0.42f * opacity);
        canvas.DrawRoundRect(SKRect.Create(rect.Left + 8, rect.Top + 10, rect.Width, rect.Height), radius, radius, paint);
        paint.Color = ToColor(0.055f, 0.075f, 0.11f, 0.88f * opacity);
        canvas.DrawRoundRect(rect, radius, radius, paint);
        paint.Color = ToColor(0.2f, 0.34f, 0.44f, 0.12f * opacity);
        var innerRadius = Math.Max(1, radius - 3);
        canvas.DrawRoundRect(SKRect.Create(rect.Left + 3, rect.Top + 3, rect.Width - 6, rect.Height * 0.42f), innerRadius, innerRadius, paint);
        paint.Color = ToColor(accentArgb, 0.5f * opacity);
        paint.Style = SKPaintStyle.Stroke;
        paint.StrokeWidth = 1.5f;
        canvas.DrawRoundRect(rect, radius, radius, paint);
        paint.Style = SKPaintStyle.Fill;
    }

    private void DrawSmokedGlassBand(SKCanvas canvas, SKRect rect, float opacity, bool borderAtTop)
    {
        SKPaint paint = _paint!;
        paint.Color = ToColor(0.025f, 0.04f, 0.065f, 0.78f * opacity);
        canvas.DrawRect(rect, paint);
        paint.Color = ToColor(0.12f, 0.2f, 0.27f, 0.16f * opacity);
        canvas.DrawRect(SKRect.Create(rect.Left, rect.Top, rect.Width, rect.Height * 0.45f), paint);
        paint.Color = ToColor(0.3f, 0.8f, 0.9f, 0.45f * opacity);
        paint.Style = SKPaintStyle.Stroke;
        paint.StrokeWidth = 1.5f;
        var y = borderAtTop ? rect.Top : rect.Bottom - 1;
        canvas.DrawLine(rect.Left, y, rect.Right, y, paint);
        paint.Style = SKPaintStyle.Fill;
    }

    private void DrawMetadataText(SKCanvas canvas, SkiaTextLayout layout, float x, float y, SKColor text, SKColor shadow, float shadowOffset)
    {
        DrawLayout(canvas, layout, x + shadowOffset, y + shadowOffset, shadow);
        DrawLayout(canvas, layout, x, y, text);
    }

    private void DrawCountdownArrows(
        SKCanvas canvas,
        SkiaTextLayout arrowLayout,
        KaraokeLineTiming line,
        float lyricLeft,
        float lyricTop,
        float opacity,
        TimeSpan position)
    {
        KaraokeCountdownState state = line.GetCountdownState(position);
        if (!state.IsVisible)
            return;

        var arrowGap = _karaokeStyle.FontSize * 0.1f;
        var slotSpacing = arrowLayout.Width + (_karaokeStyle.FontSize * 0.12f);
        var countdownOpacity = opacity * state.Opacity;
        const uint redArgb = 0xFFFF0000;
        const uint greenArgb = 0xFF00C000;
        SKColor red = ToColor(redArgb, countdownOpacity);
        SKColor green = ToColor(greenArgb, countdownOpacity);
        SKColor active = ToColor(LerpColor(redArgb, greenArgb, state.ActiveArrowProgress), countdownOpacity);

        for (var i = 0; i < 4; i++)
        {
            SKColor color = i < state.CompletedArrowCount
                ? green
                : i == state.CompletedArrowCount && state.CompletedArrowCount < 4
                    ? active
                    : red;
            DrawLayout(canvas, arrowLayout, lyricLeft + (i * slotSpacing), lyricTop - arrowLayout.Height - arrowGap, color);
        }
    }

    private void DrawTextLayer(
        SKCanvas canvas,
        SkiaTextLayout layout,
        float x,
        float y,
        KaraokeLayerStyle style,
        uint textArgb,
        uint outlineArgb,
        uint shadowArgb)
    {
        if ((style.ShadowArgb >> 24) != 0)
            DrawLayout(canvas, layout, x + _karaokeStyle.ShadowOffsetX, y + _karaokeStyle.ShadowOffsetY, ToColor(shadowArgb));

        if (style.OutlineWidth > 0 && (style.OutlineArgb >> 24) != 0)
        {
            SKColor outline = ToColor(outlineArgb);
            for (var dx = -1; dx <= 1; dx++)
            {
                for (var dy = -1; dy <= 1; dy++)
                {
                    if (dx != 0 || dy != 0)
                        DrawLayout(canvas, layout, x + (dx * style.OutlineWidth), y + (dy * style.OutlineWidth), outline);
                }
            }
        }

        DrawLayout(canvas, layout, x, y, ToColor(textArgb));
    }

    private void DrawLayout(SKCanvas canvas, SkiaTextLayout layout, float x, float y, SKColor color)
    {
        _paint!.Color = color;
        layout.Draw(canvas, x, y, _paint);
    }

    private (float CenterY, float Opacity) GetAnimatedLineState(
        int relativeLine,
        float progress,
        float middleY,
        float slotDistance,
        bool hasPreviousLine)
    {
        var easedProgress = SmoothStep(progress);
        if (!hasPreviousLine)
        {
            return (
                middleY + (relativeLine * slotDistance),
                Lerp(0, GetStableLineOpacity(relativeLine), easedProgress));
        }

        var previousRelativeLine = relativeLine + 1;
        return (
            Lerp(
                middleY + (previousRelativeLine * slotDistance),
                middleY + (relativeLine * slotDistance),
                easedProgress),
            Lerp(
                GetStableLineOpacity(previousRelativeLine),
                GetStableLineOpacity(relativeLine),
                easedProgress));
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

    private static float GetStableLineScale(int relativeLine) => relativeLine switch
    {
        0 => 1.0f,
        -1 or 1 => 0.80f,
        -2 or 2 => 0.60f,
        _ => 0.40f,
    };

    private static float GetLineScale(int relativeLine, float scrollProgress)
    {
        var easedProgress = SmoothStep(scrollProgress);
        var previousScale = GetStableLineScale(relativeLine + 1);
        var currentScale = GetStableLineScale(relativeLine);
        return Lerp(previousScale, currentScale, easedProgress);
    }

    private static float EaseInOutQuint(float t) =>
        t < 0.5f
            ? 16 * t * t * t * t * t
            : 1 - (MathF.Pow((-2 * t) + 2, 5) / 2);

    private static float SmoothStep(float t) => t * t * (3 - (2 * t));

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

    private static byte ToByte(float unit) => (byte)Math.Round(Math.Clamp(unit, 0, 1) * 255);

    private static SKColor ToColor(uint argb) => new(argb);

    private static SKColor ToColor(uint argb, float opacity) => new(
        (byte)(argb >> 16),
        (byte)(argb >> 8),
        (byte)argb,
        ToByte(((argb >> 24) & 0xFF) / 255f * Math.Clamp(opacity, 0, 1)));

    private static SKColor ToColor(float red, float green, float blue, float alpha) =>
        new(ToByte(red), ToByte(green), ToByte(blue), ToByte(alpha));

    private static uint LerpColor(uint from, uint to, float amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        var fa = (byte)((from >> 24) & 0xFF);
        var fr = (byte)((from >> 16) & 0xFF);
        var fg = (byte)((from >> 8) & 0xFF);
        var fb = (byte)(from & 0xFF);
        var ta = (byte)((to >> 24) & 0xFF);
        var tr = (byte)((to >> 16) & 0xFF);
        var tg = (byte)((to >> 8) & 0xFF);
        var tb = (byte)(to & 0xFF);
        var a = (byte)(fa + ((ta - fa) * amount));
        var r = (byte)(fr + ((tr - fr) * amount));
        var g = (byte)(fg + ((tg - fg) * amount));
        var b = (byte)(fb + ((tb - fb) * amount));
        return (uint)((a << 24) | (r << 16) | (g << 8) | b);
    }

    // False when the native SkiaSharp library is unavailable: the first frame reports it (Core
    // raises one Subtitle warning) and later frames draw nothing.
    private bool BeginFrame()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_nativeUnavailable)
            return false;

        try
        {
            _paint ??= new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
            _layerPaint ??= new SKPaint();
        }
        catch (Exception ex) when (ex is DllNotFoundException or TypeInitializationException or EntryPointNotFoundException or BadImageFormatException)
        {
            _nativeUnavailable = true;
            throw new InvalidOperationException(
                "The SkiaSharp native library (libSkiaSharp) could not be loaded or does not match SkiaSharp; subtitles and karaoke text are not drawn.",
                ex);
        }

        _generation++;
        return true;
    }

    // Disposes the layouts this frame did not use.
    private void EndFrame()
    {
        foreach (KeyValuePair<LayoutKey, CacheEntry> pair in _layouts)
        {
            if (pair.Value.LastUsed == _generation)
                continue;

            pair.Value.Layout?.Dispose();
            _ = _layouts.Remove(pair.Key);
        }
    }

    private SkiaTextLayout GetLayout(string text, FormatKey format, float maxWidth, bool center)
    {
        ref CacheEntry entry = ref CollectionsMarshal.GetValueRefOrAddDefault(
            _layouts,
            new LayoutKey(text, format, maxWidth, center),
            out _);
        entry.Layout ??= SkiaTextLayout.Create(text, GetFace(format), format.Size, maxWidth, center);
        entry.LastUsed = _generation;
        return entry.Layout;
    }

    private SkiaFontFace GetFace(FormatKey format)
    {
        var key = new FaceKey(format.Family, format.Weight, format.Italic);
        ref SkiaFontFace? face = ref CollectionsMarshal.GetValueRefOrAddDefault(_faces, key, out _);
        face ??= new SkiaFontFace(format.Family, format.Weight, format.Italic);
        return face;
    }

    // One bitmap and canvas, kept while the raster size stays the same.
    private SKCanvas GetTarget(int width, int height)
    {
        if (_canvas is not null && _bitmap!.Width == width && _bitmap.Height == height)
            return _canvas;

        ReleaseTarget();
        _bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        _canvas = new SKCanvas(_bitmap);
        return _canvas;
    }

    private void ReleaseTarget()
    {
        _canvas?.Dispose();
        _canvas = null;
        _bitmap?.Dispose();
        _bitmap = null;
    }

    private SubtitleRasterResult CopyPixels(RenderBounds bounds, int canvasWidth, int canvasHeight, Func<int, Memory<byte>> rentPixels)
    {
        _canvas!.Flush();
        var stride = bounds.Width * 4;
        var length = checked(stride * bounds.Height);
        Memory<byte> pixels = rentPixels(length)[..length];
        ReadOnlySpan<byte> source = _bitmap!.GetPixelSpan();
        var sourceStride = _bitmap.RowBytes;
        if (sourceStride == stride)
        {
            source[..length].CopyTo(pixels.Span);
        }
        else
        {
            Span<byte> destination = pixels.Span;
            for (var row = 0; row < bounds.Height; row++)
                source.Slice(row * sourceStride, stride).CopyTo(destination.Slice(row * stride, stride));
        }

        return new SubtitleRasterResult(
            pixels,
            bounds.Width,
            bounds.Height,
            stride,
            bounds.Left,
            bounds.Top,
            canvasWidth,
            canvasHeight);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
            foreach (CacheEntry entry in _layouts.Values)
                entry.Layout?.Dispose();
            _layouts.Clear();
            _faces.Clear();
            _lineLayouts.Clear();
            ReleaseTarget();
            _paint?.Dispose();
            _layerPaint?.Dispose();
        }
    }

    private struct CacheEntry
    {
        public SkiaTextLayout? Layout;
        public long LastUsed;
    }

    private readonly record struct FaceKey(string Family, int Weight, bool Italic);
    private readonly record struct FormatKey(string Family, int Weight, bool Italic, float Size);
    private readonly record struct LayoutKey(string Text, FormatKey Format, float MaxWidth, bool Center);
    private readonly record struct KaraokeLayerStyle(uint TextArgb, uint OutlineArgb, float OutlineWidth, uint ShadowArgb);
    private readonly record struct RenderBounds(int Left, int Top, int Width, int Height);

    private readonly record struct LineContext(
        SkiaTextLayout CountdownArrowLayout,
        RenderBounds Bounds,
        KaraokeLayerStyle BaseStyle,
        KaraokeLayerStyle WipeStyle,
        TimeSpan Position,
        TimeSpan WipeDuration,
        int Width,
        int Height,
        float LayoutWidth,
        int Padding);
}
