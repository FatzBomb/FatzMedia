using System.Numerics;
using System.Runtime.InteropServices;
using FatzBomb.MediaControl.Core.Audio;
using FatzBomb.MediaControl.Core.Configuration;
using FatzBomb.MediaControl.Core.Subtitle;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.DXGI;
using Vortice.Mathematics;
using Vortice.WIC;

namespace FatzBomb.MediaControl.Rendering.Direct3D11.Subtitle;

/// <summary>
/// DirectWrite subtitle and karaoke rasterizer. A karaoke wipe redraws on every frame, so the
/// text formats, text layouts, brushes and the WIC bitmap with its render target are cached
/// (ENG-039 P5-2): an entry not used by a frame is disposed at the end of it, so a steady wipe
/// reuses everything while a scaling transition only keeps what its current frame drew.
/// </summary>
internal sealed class SubtitleTextRenderer(SubtitleTextStyle? style = null, KaraokeTextStyle? karaokeStyle = null) : ISubtitleRasterizer
{
    private static readonly Func<int, Memory<byte>> AllocatePixels = static length => new byte[length];

    // Presentation may request a fresh cue from multiple scheduler paths; the coordinator serializes
    // calls, and the factory is multi-threaded to keep that usage valid if a future preview path is added.
    private readonly ID2D1Factory1 _d2dFactory = D2D1.D2D1CreateFactory<ID2D1Factory1>(Vortice.Direct2D1.FactoryType.MultiThreaded);
    private readonly IDWriteFactory _writeFactory = DWrite.DWriteCreateFactory<IDWriteFactory>(Vortice.DirectWrite.FactoryType.Shared);
    private readonly IWICImagingFactory _wicFactory = new();
    private readonly SubtitleTextStyle _style = (style ?? new SubtitleTextStyle()).Normalize();
    private readonly KaraokeTextStyle _karaokeStyle = (karaokeStyle ?? new KaraokeTextStyle()).Normalize();

    // The caches are used by one frame at a time.
    private readonly Lock _gate = new();
    private readonly Dictionary<FormatKey, CacheEntry<IDWriteTextFormat>> _formats = [];
    private readonly Dictionary<LayoutKey, CacheEntry<IDWriteTextLayout>> _layouts = [];
    private readonly Dictionary<Color4, CacheEntry<ID2D1SolidColorBrush>> _brushes = [];
    private readonly List<IDWriteTextLayout> _lineLayouts = [];
    private long _generation;
    private IWICBitmap? _bitmap;
    private ID2D1RenderTarget? _target;
    private int _targetWidth;
    private int _targetHeight;
    private bool _disposed;

    public SubtitleRasterResult Render(string text, int width, int height) =>
        Render(text, width, height, AllocatePixels);

    public SubtitleRasterResult RenderKaraoke(KaraokeCueTiming timing, TimeSpan position, int width, int height) =>
        RenderKaraoke(timing, position, width, height, AllocatePixels);

    public SubtitleRasterResult Render(string text, int width, int height, Func<int, Memory<byte>> rentPixels)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentNullException.ThrowIfNull(rentPixels);

        lock (_gate)
        {
            BeginFrame();
            try
            {
                return RenderTextFrame(text, width, height, rentPixels);
            }
            catch
            {
                // A failure mid-draw leaves the cached target between BeginDraw and EndDraw:
                // the next frame starts from a new one.
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
            BeginFrame();
            try
            {
                return RenderKaraokeFrame(timing, position, baseStyle, wipeStyle, width, height, rentPixels);
            }
            catch
            {
                // A failure mid-draw leaves the cached target between BeginDraw and EndDraw:
                // the next frame starts from a new one.
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
        IDWriteTextLayout layout = GetLayout(
            text,
            new FormatKey(_style.FontFamily, FontWeight.Bold, FontStyle.Normal, _style.FontSize),
            width * 0.85f,
            height * 0.3f,
            TextAlignment.Center);
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

        ID2D1RenderTarget target = GetTarget(bounds.Width, bounds.Height);
        ID2D1SolidColorBrush textBrush = GetBrush(ToColor(_style.TextArgb));
        ID2D1SolidColorBrush outlineBrush = GetBrush(ToColor(_style.OutlineArgb));
        ID2D1SolidColorBrush shadowBrush = GetBrush(ToColor(_style.ShadowArgb));
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
        var lyricFormat = new FormatKey(_karaokeStyle.FontFamily, FontWeight.Bold, FontStyle.Normal, _karaokeStyle.FontSize);
        var countdownFormat = new FormatKey(_karaokeStyle.FontFamily, FontWeight.Bold, FontStyle.Normal, _karaokeStyle.FontSize * 0.5f);
        IDWriteTextLayout countdownArrowLayout = GetLayout("▶", countdownFormat, width, height * 0.2f, TextAlignment.Leading);
        var layoutWidth = width * 0.85f;
        var wipeDuration = TimeSpan.FromMilliseconds(_karaokeStyle.WipeTransitionMilliseconds);

        // Current page lines first, then the outgoing page's, as one list.
        IReadOnlyList<KaraokeLineTiming> lines = timing.Lines;
        IReadOnlyList<KaraokeLineTiming> outgoingLines = timing.OutgoingPageLines;
        List<IDWriteTextLayout> allLayouts = _lineLayouts;
        allLayouts.Clear();
        for (var i = 0; i < lines.Count; i++)
            allLayouts.Add(GetLayout(lines[i].Text, lyricFormat, layoutWidth, height * 0.2f, TextAlignment.Center));
        for (var i = 0; i < outgoingLines.Count; i++)
            allLayouts.Add(GetLayout(outgoingLines[i].Text, lyricFormat, layoutWidth, height * 0.2f, TextAlignment.Center));
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
        TextMetrics countdownArrowMetrics = countdownArrowLayout.Metrics;
        var countdownArrowGap = _karaokeStyle.FontSize * 0.12f;
        var countdownWidth = (countdownArrowMetrics.Width * 4) + (countdownArrowGap * 3);
        var maxLineHeight = float.MinValue; // max scale is 1.0
        var minTextLeft = float.MaxValue;
        var maxTextRight = float.MinValue;
        var anyCountdownVisible = false;
        for (var i = 0; i < allLayouts.Count; i++)
        {
            TextMetrics metrics = allLayouts[i].Metrics;
            KaraokeLineTiming line = i < lines.Count ? lines[i] : outgoingLines[i - lines.Count];
            var textLeft = (width * 0.075f) + Math.Max(0, (layoutWidth - metrics.Width) / 2);
            var textRight = textLeft + metrics.Width;
            maxLineHeight = Math.Max(maxLineHeight, metrics.Height * 1.0f);
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
            ? countdownArrowMetrics.Height + (_karaokeStyle.FontSize * 0.1f)
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

        ID2D1RenderTarget target = GetTarget(bounds.Width, bounds.Height);
        target.BeginDraw();
        target.Clear(new Color4(0f, 0f, 0f, 0f));
        if (introMetadataOpacity > 0
            && timing.UseSplitMetadataBands
            && timing.Metadata is { } splitMetadata)
        {
            DrawSplitMetadataBands(
                target,
                splitMetadata,
                introMetadataOpacity,
                width,
                height,
                bounds);
        }

        var endLyricOpacity = timing.GetEndLyricOpacity(position);
        if (timing.ClearsPage)
        {
            var fadeOut = timing.GetPageFadeOutProgress(position);
            for (var i = 0; i < outgoingLines.Count; i++)
            {
                var relativeLine = i - timing.OutgoingActiveLineIndex;
                DrawKaraokeLine(
                    target,
                    countdownArrowLayout,
                    outgoingLines[i],
                    middleY + (relativeLine * slotDistance),
                    GetStableLineOpacity(relativeLine) * (1 - fadeOut) * endLyricOpacity,
                    relativeLine,
                    1,
                    Lerp(1, 0.75f, fadeOut),
                    bounds,
                    baseStyle,
                    wipeStyle,
                    position,
                    wipeDuration,
                    width,
                    height,
                    layoutWidth,
                    padding);
            }

            var fadeIn = timing.GetPageFadeInProgress(position, transitionDuration);
            for (var i = 0; i < lines.Count; i++)
            {
                var relativeLine = i - timing.ActiveLineIndex;
                DrawKaraokeLine(
                    target,
                    countdownArrowLayout,
                    lines[i],
                    middleY + (relativeLine * slotDistance),
                    GetStableLineOpacity(relativeLine) * fadeIn * endLyricOpacity,
                    relativeLine,
                    1,
                    Lerp(1.5f, 1, fadeIn),
                    bounds,
                    baseStyle,
                    wipeStyle,
                    position,
                    wipeDuration,
                    width,
                    height,
                    layoutWidth,
                    padding);
            }
        }
        else
        {
            var progress = timing.GetTransitionProgress(position);
            var hasPreviousLine = timing.ActiveLineIndex > 0;
            for (var i = 0; i < lines.Count; i++)
            {
                var relativeLine = i - timing.ActiveLineIndex;
                (var centerY, var opacity) = GetAnimatedLineState(
                    relativeLine,
                    progress,
                    middleY,
                    slotDistance,
                    hasPreviousLine);
                DrawKaraokeLine(
                    target,
                    countdownArrowLayout,
                    lines[i],
                    centerY,
                    opacity * endLyricOpacity,
                    relativeLine,
                    hasPreviousLine ? progress : 1,
                    1,
                    bounds,
                    baseStyle,
                    wipeStyle,
                    position,
                    wipeDuration,
                    width,
                    height,
                    layoutWidth,
                    padding);
            }
        }

        if (introMetadataOpacity > 0
            && !timing.UseSplitMetadataBands
            && timing.Metadata is { } cardMetadata)
        {
            DrawMetadataCard(
                target,
                cardMetadata,
                introMetadataOpacity,
                width,
                height,
                bounds,
                isEndCard: false);
        }

        if (endMetadataOpacity > 0 && timing.Metadata is { } endMetadata)
        {
            DrawMetadataCard(
                target,
                endMetadata,
                endMetadataOpacity,
                width,
                height,
                bounds,
                isEndCard: true);
        }

        _ = target.EndDraw();
        allLayouts.Clear();
        return CopyPixels(bounds, width, height, rentPixels);
    }

    private void DrawKaraokeLine(
        ID2D1RenderTarget target,
        IDWriteTextLayout countdownArrowLayout,
        KaraokeLineTiming line,
        float centerY,
        float opacity,
        int relativeLine,
        float scrollProgress,
        float presentationScale,
        RenderBounds bounds,
        KaraokeLayerStyle baseStyle,
        KaraokeLayerStyle wipeStyle,
        TimeSpan position,
        TimeSpan wipeDuration,
        int width,
        int height,
        float layoutWidth,
        int padding)
    {
        if (opacity <= 0)
            return;

        var scale = GetLineScale(relativeLine, scrollProgress);
        var totalScale = scale * presentationScale;
        var effectiveFontSize = _karaokeStyle.FontSize * totalScale;
        var scaledLayoutWidth = layoutWidth * totalScale;
        var scaledLayoutHeight = height * 0.2f * totalScale;
        var scaledFormat = new FormatKey(_karaokeStyle.FontFamily, FontWeight.Bold, FontStyle.Normal, effectiveFontSize);
        IDWriteTextLayout scaledLayout = GetLayout(
            line.Text,
            scaledFormat,
            scaledLayoutWidth,
            scaledLayoutHeight,
            TextAlignment.Center);
        TextMetrics metrics = scaledLayout.Metrics;
        var lineHeight = metrics.Height;
        var y = centerY - (lineHeight / 2);
        var canvasOrigin = new Vector2((width - scaledLayoutWidth) / 2, y);
        Vector2 origin = canvasOrigin - new Vector2(bounds.Left, bounds.Top);
        var left = canvasOrigin.X + Math.Max(0, (scaledLayoutWidth - metrics.Width) / 2);
        var descenderPadding = effectiveFontSize * 0.35f;
        var layerLeft = Math.Max(0, left - bounds.Left - padding);
        var layerTop = Math.Max(0, y - bounds.Top - countdownArrowLayout.Metrics.Height - (_karaokeStyle.FontSize * 0.1f) - padding);
        var layerRight = Math.Min(bounds.Width, left - bounds.Left + metrics.Width + padding);
        var layerBottom = Math.Min(bounds.Height, y - bounds.Top + lineHeight + descenderPadding + padding);

        // Fade the composed line once so overlapping text, outline, and wipe passes retain their original colors.
        var layerParameters = new LayerParameters
        {
            ContentBounds = new Vortice.RawRectF(layerLeft, layerTop, layerRight, layerBottom),
            GeometricMask = null,
            MaskAntialiasMode = AntialiasMode.Aliased,
            MaskTransform = Matrix3x2.Identity,
            Opacity = opacity,
            OpacityBrush = null,
            LayerOptions = LayerOptions.None,
        };
        target.PushLayer(layerParameters, null);
        try
        {
            DrawTextLayer(target, scaledLayout, origin, baseStyle.TextArgb, baseStyle.OutlineArgb, baseStyle.ShadowArgb, baseStyle);

            DrawCountdownArrows(
                target,
                countdownArrowLayout,
                line,
                left - bounds.Left,
                y - bounds.Top,
                1,
                position);

            var highlightedLength = line.GetHighlightedLength(position);
            if (highlightedLength <= 0)
                return;

            // Smooth wipe: two-pass approach.
            // Pass 1: fully-highlighted syllables (before current) at full wipe opacity.
            // Pass 2: current transitioning syllable blended from base color to wipe color.
            var wipeFraction = line.GetSyllableWipeFraction(position, wipeDuration);
            var easedWipeFraction = EaseInOutQuint(wipeFraction);

            // Find the boundary between fully-highlighted and transitioning syllables
            var fullyHighlightedLength = line.GetFullyHighlightedLength(position, wipeDuration);

            // Compute widths
            var fullyHighlightedWidth = 0f;
            if (fullyHighlightedLength > 0 && fullyHighlightedLength < line.Text.Length)
            {
                fullyHighlightedWidth = MeasurePrefixWidth(scaledLayout, metrics, line.Text, fullyHighlightedLength, scaledFormat, scaledLayoutWidth, scaledLayoutHeight);
            }
            else if (fullyHighlightedLength >= line.Text.Length)
            {
                fullyHighlightedWidth = metrics.Width;
            }

            var transitioningWidth = fullyHighlightedWidth;
            if (highlightedLength < line.Text.Length)
            {
                transitioningWidth = MeasurePrefixWidth(scaledLayout, metrics, line.Text, highlightedLength, scaledFormat, scaledLayoutWidth, scaledLayoutHeight);

                // Extend to include partial next syllable
                if (easedWipeFraction > 0 && highlightedLength < line.Text.Length)
                {
                    var nextWidth = MeasurePrefixWidth(scaledLayout, metrics, line.Text, highlightedLength + 1, scaledFormat, scaledLayoutWidth, scaledLayoutHeight);
                    transitioningWidth += (nextWidth - transitioningWidth) * easedWipeFraction;
                }
            }
            else
            {
                transitioningWidth = metrics.Width;
            }

            // Pass 1: fully-highlighted syllables at full wipe opacity
            if (fullyHighlightedWidth > 0)
            {
                var clip = new Vortice.RawRectF(
                    Math.Max(0, left - bounds.Left - padding),
                    Math.Max(0, y - bounds.Top - padding),
                    Math.Min(bounds.Width, left - bounds.Left + fullyHighlightedWidth + padding),
                    Math.Min(bounds.Height, y - bounds.Top + lineHeight + descenderPadding + padding));
                target.PushAxisAlignedClip(clip, AntialiasMode.PerPrimitive);
                try
                {
                    DrawTextLayer(target, scaledLayout, origin, wipeStyle.TextArgb, wipeStyle.OutlineArgb, wipeStyle.ShadowArgb, wipeStyle);
                }
                finally
                {
                    target.PopAxisAlignedClip();
                }
            }

            // Pass 2: current transitioning syllable — draw with interpolated color from base to wipe.
            // Instead of overlaying wipe at low opacity (which darkens through the outline/shadow),
            // we redraw the syllable region with a color that blends directly from base to wipe.
            if (transitioningWidth > fullyHighlightedWidth)
            {
                var clip = new Vortice.RawRectF(
                    Math.Max(0, left - bounds.Left + fullyHighlightedWidth - padding),
                    Math.Max(0, y - bounds.Top - padding),
                    Math.Min(bounds.Width, left - bounds.Left + transitioningWidth + padding),
                    Math.Min(bounds.Height, y - bounds.Top + lineHeight + descenderPadding + padding));
                target.PushAxisAlignedClip(clip, AntialiasMode.PerPrimitive);
                try
                {
                    // Interpolate colors directly from base to wipe to avoid alpha blending artifacts.
                    var blendedTextArgb = LerpColor(baseStyle.TextArgb, wipeStyle.TextArgb, easedWipeFraction);
                    var blendedOutlineArgb = LerpColor(baseStyle.OutlineArgb, wipeStyle.OutlineArgb, easedWipeFraction);
                    var blendedShadowArgb = LerpColor(baseStyle.ShadowArgb, wipeStyle.ShadowArgb, easedWipeFraction);
                    DrawTextLayer(target, scaledLayout, origin, blendedTextArgb, blendedOutlineArgb, blendedShadowArgb, wipeStyle);
                }
                finally
                {
                    target.PopAxisAlignedClip();
                }
            }
        }
        finally
        {
            target.PopLayer();
        }
    }

    /// <summary>
    /// The width a layout of just <c>text[..length]</c> would measure (trailing whitespace
    /// excluded), read from the line's own layout by hit-testing instead of building a prefix
    /// layout per frame. A wrapped line measures a real prefix layout, which wraps on its own.
    /// </summary>
    private float MeasurePrefixWidth(
        IDWriteTextLayout layout,
        in TextMetrics metrics,
        string text,
        int length,
        FormatKey format,
        float maxWidth,
        float maxHeight)
    {
        if (metrics.LineCount > 1)
        {
            using IDWriteTextLayout prefixLayout = _writeFactory.CreateTextLayout(text[..length], GetFormat(format), maxWidth, maxHeight);
            return prefixLayout.Metrics.Width;
        }

        var end = length;
        while (end > 0 && char.IsWhiteSpace(text[end - 1]))
            end--;
        if (end == 0)
            return 0;

        _ = layout.HitTestTextPosition(0, false, out Vector2 start);
        _ = layout.HitTestTextPosition((uint)(end - 1), true, out Vector2 stop);
        return Math.Max(0, stop.X - start.X);
    }

    private void DrawMetadataCard(
        ID2D1RenderTarget target,
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

        var titleFormat = new FormatKey(_karaokeStyle.FontFamily, FontWeight.Bold, FontStyle.Normal, titleFontSize);
        var artistFormat = new FormatKey(_karaokeStyle.FontFamily, FontWeight.SemiBold, FontStyle.Normal, artistFontSize);
        var infoFormat = new FormatKey(_karaokeStyle.FontFamily, FontWeight.Normal, FontStyle.Normal, infoFontSize);
        var copyrightFormat = new FormatKey(_karaokeStyle.FontFamily, FontWeight.Normal, FontStyle.Italic, copyrightFontSize);

        var boxWidth = canvasWidth * (metadata.IsFallback ? 0.5f : 0.72f);
        var textWidth = boxWidth - (_karaokeStyle.FontSize * 0.9f);
        IDWriteTextLayout titleLayout = GetLayout(metadata.Title, titleFormat, textWidth, canvasHeight * 0.15f, TextAlignment.Center);
        IDWriteTextLayout artistLayout = GetLayout(
            string.IsNullOrEmpty(metadata.Artist) ? "" : metadata.Artist,
            artistFormat,
            textWidth,
            canvasHeight * 0.1f,
            TextAlignment.Center);

        var infoText = BuildMetadataDetails(metadata, isEndCard);
        IDWriteTextLayout infoLayout = GetLayout(infoText, infoFormat, textWidth, canvasHeight * 0.24f, TextAlignment.Center);
        IDWriteTextLayout copyrightLayout = GetLayout(metadata.Copyright, copyrightFormat, textWidth, canvasHeight * 0.08f, TextAlignment.Center);

        TextMetrics titleMetrics = titleLayout.Metrics;
        TextMetrics artistMetrics = artistLayout.Metrics;
        TextMetrics infoMetrics = infoLayout.Metrics;
        TextMetrics copyrightMetrics = copyrightLayout.Metrics;

        var gap = _karaokeStyle.FontSize * 0.13f;
        var sectionGap = _karaokeStyle.FontSize * 0.2f;
        var padding = _karaokeStyle.FontSize * 0.45f;
        var contentHeight = titleMetrics.Height
            + (string.IsNullOrEmpty(metadata.Artist) ? 0 : artistMetrics.Height + gap)
            + (string.IsNullOrEmpty(infoText) ? 0 : infoMetrics.Height + sectionGap)
            + (string.IsNullOrEmpty(metadata.Copyright) ? 0 : copyrightMetrics.Height + sectionGap);
        var boxHeight = contentHeight + (padding * 2);
        var boxLeft = (canvasWidth - boxWidth) / 2f;
        var boxTop = (canvasHeight - boxHeight) / 2f;

        var rect = new System.Drawing.RectangleF(boxLeft - bounds.Left, boxTop - bounds.Top, boxWidth, boxHeight);
        DrawSmokedGlassPanel(target, rect, _karaokeStyle.FontSize * 0.22f, opacity, 0xFF62D6E8);

        var textY = boxTop + padding;
        var textX = boxLeft + ((boxWidth - textWidth) / 2) - bounds.Left;
        ID2D1SolidColorBrush shadowBrush = GetBrush(ToColor(0xD0000000, opacity));
        ID2D1SolidColorBrush titleBrush = GetBrush(ToColor(0xFFFFF4DC, opacity));
        ID2D1SolidColorBrush artistBrush = GetBrush(ToColor(0xFF78DCE8, opacity));
        ID2D1SolidColorBrush infoBrush = GetBrush(ToColor(0xFFD5DCE6, opacity));
        ID2D1SolidColorBrush copyrightBrush = GetBrush(ToColor(0xFFB8A87A, opacity));
        DrawMetadataText(target, titleLayout, new Vector2(textX, textY - bounds.Top), titleBrush, shadowBrush, 2.2f);
        textY += titleMetrics.Height;

        if (!string.IsNullOrEmpty(metadata.Artist))
        {
            textY += gap;
            DrawMetadataText(target, artistLayout, new Vector2(textX, textY - bounds.Top), artistBrush, shadowBrush, 1.5f);
            textY += artistMetrics.Height;
        }

        if (!string.IsNullOrEmpty(infoText))
        {
            textY += sectionGap;
            DrawMetadataText(target, infoLayout, new Vector2(textX, textY - bounds.Top), infoBrush, shadowBrush, 1f);
            textY += infoMetrics.Height;
        }

        if (!string.IsNullOrEmpty(metadata.Copyright))
        {
            textY += sectionGap;
            DrawMetadataText(target, copyrightLayout, new Vector2(textX, textY - bounds.Top), copyrightBrush, shadowBrush, 1f);
        }
    }

    private void DrawSplitMetadataBands(
        ID2D1RenderTarget target,
        KaraokeMetadata metadata,
        float opacity,
        int canvasWidth,
        int canvasHeight,
        RenderBounds bounds)
    {
        var topHeight = canvasHeight * 0.17f;
        var bottomHeight = canvasHeight * 0.16f;
        DrawSmokedGlassBand(target, new System.Drawing.RectangleF(-bounds.Left, -bounds.Top, canvasWidth, topHeight), opacity, false);
        DrawSmokedGlassBand(target, new System.Drawing.RectangleF(-bounds.Left, canvasHeight - bottomHeight - bounds.Top, canvasWidth, bottomHeight), opacity, true);

        var titleSize = _karaokeStyle.FontSize * (metadata.IsFallback ? 0.52f : 0.68f);
        var secondarySize = _karaokeStyle.FontSize * 0.36f;
        var detailSize = _karaokeStyle.FontSize * 0.27f;
        var titleFormat = new FormatKey(_karaokeStyle.FontFamily, FontWeight.Bold, FontStyle.Normal, titleSize);
        var secondaryFormat = new FormatKey(_karaokeStyle.FontFamily, FontWeight.SemiBold, FontStyle.Normal, secondarySize);
        var detailFormat = new FormatKey(_karaokeStyle.FontFamily, FontWeight.Normal, FontStyle.Normal, detailSize);
        var textWidth = canvasWidth * 0.88f;
        IDWriteTextLayout titleLayout = GetLayout(metadata.Title, titleFormat, textWidth, topHeight * 0.55f, TextAlignment.Center);
        var secondary = string.Join("  •  ", new[] { metadata.Artist, metadata.Year }.Where(static value => !string.IsNullOrWhiteSpace(value)));
        IDWriteTextLayout secondaryLayout = GetLayout(secondary, secondaryFormat, textWidth, topHeight * 0.32f, TextAlignment.Center);
        IDWriteTextLayout bottomLayout = GetLayout(BuildBandDetails(metadata), detailFormat, textWidth, bottomHeight * 0.82f, TextAlignment.Center);

        ID2D1SolidColorBrush shadowBrush = GetBrush(ToColor(0xD0000000, opacity));
        ID2D1SolidColorBrush titleBrush = GetBrush(ToColor(0xFFFFF4DC, opacity));
        ID2D1SolidColorBrush accentBrush = GetBrush(ToColor(0xFF78DCE8, opacity));
        ID2D1SolidColorBrush detailBrush = GetBrush(ToColor(0xFFD5DCE6, opacity));
        var x = ((canvasWidth - textWidth) / 2f) - bounds.Left;
        var titleY = (topHeight * 0.12f) - bounds.Top;
        DrawMetadataText(target, titleLayout, new Vector2(x, titleY), titleBrush, shadowBrush, 1.8f);
        if (secondary.Length > 0)
            DrawMetadataText(target, secondaryLayout, new Vector2(x, titleY + titleLayout.Metrics.Height), accentBrush, shadowBrush, 1.2f);
        DrawMetadataText(target, bottomLayout, new Vector2(x, canvasHeight - bottomHeight + (bottomHeight * 0.14f) - bounds.Top), detailBrush, shadowBrush, 1f);
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
                lines.Add(string.Join("  \u2022  ", metadata.AdditionalInfo));
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
                : $"Written By:  {metadata.Writer}  \u2022  Lyricist:  {metadata.Lyricist}"
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
        return string.Join("  \u2022  ", details);
    }

    private static string FormatDuration(TimeSpan duration) => duration.TotalHours >= 1
        ? $"{(int)duration.TotalHours}:{duration.Minutes:00}:{duration.Seconds:00}"
        : $"{(int)duration.TotalMinutes}:{duration.Seconds:00}";

    private void DrawSmokedGlassPanel(ID2D1RenderTarget target, System.Drawing.RectangleF rect, float radius, float opacity, uint accentArgb)
    {
        ID2D1SolidColorBrush shadowBrush = GetBrush(new Color4(0f, 0f, 0f, 0.42f * opacity));
        ID2D1SolidColorBrush glassBrush = GetBrush(new Color4(0.055f, 0.075f, 0.11f, 0.88f * opacity));
        ID2D1SolidColorBrush sheenBrush = GetBrush(new Color4(0.2f, 0.34f, 0.44f, 0.12f * opacity));
        ID2D1SolidColorBrush borderBrush = GetBrush(ToColor(accentArgb, 0.5f * opacity));
        target.FillRoundedRectangle(new RoundedRectangle(new System.Drawing.RectangleF(rect.X + 8, rect.Y + 10, rect.Width, rect.Height), radius, radius), shadowBrush);
        var rounded = new RoundedRectangle(rect, radius, radius);
        target.FillRoundedRectangle(rounded, glassBrush);
        target.FillRoundedRectangle(new RoundedRectangle(new System.Drawing.RectangleF(rect.X + 3, rect.Y + 3, rect.Width - 6, rect.Height * 0.42f), Math.Max(1, radius - 3), Math.Max(1, radius - 3)), sheenBrush);
        target.DrawRoundedRectangle(rounded, borderBrush, 1.5f);
    }

    private void DrawSmokedGlassBand(ID2D1RenderTarget target, System.Drawing.RectangleF rect, float opacity, bool borderAtTop)
    {
        ID2D1SolidColorBrush baseBrush = GetBrush(new Color4(0.025f, 0.04f, 0.065f, 0.78f * opacity));
        ID2D1SolidColorBrush sheenBrush = GetBrush(new Color4(0.12f, 0.2f, 0.27f, 0.16f * opacity));
        ID2D1SolidColorBrush borderBrush = GetBrush(new Color4(0.3f, 0.8f, 0.9f, 0.45f * opacity));
        target.FillRectangle(rect, baseBrush);
        target.FillRectangle(new System.Drawing.RectangleF(rect.X, rect.Y, rect.Width, rect.Height * 0.45f), sheenBrush);
        var y = borderAtTop ? rect.Top : rect.Bottom - 1;
        target.DrawLine(new Vector2(rect.Left, y), new Vector2(rect.Right, y), borderBrush, 1.5f);
    }

    private static void DrawMetadataText(ID2D1RenderTarget target, IDWriteTextLayout layout, Vector2 origin, ID2D1SolidColorBrush textBrush, ID2D1SolidColorBrush shadowBrush, float shadowOffset)
    {
        target.DrawTextLayout(origin + new Vector2(shadowOffset, shadowOffset), layout, shadowBrush);
        target.DrawTextLayout(origin, layout, textBrush);
    }

    private void DrawCountdownArrows(
        ID2D1RenderTarget target,
        IDWriteTextLayout arrowLayout,
        KaraokeLineTiming line,
        float lyricLeft,
        float lyricTop,
        float opacity,
        TimeSpan position)
    {
        KaraokeCountdownState state = line.GetCountdownState(position);
        if (!state.IsVisible)
            return;

        TextMetrics arrowMetrics = arrowLayout.Metrics;
        var arrowGap = _karaokeStyle.FontSize * 0.1f;
        var slotSpacing = arrowMetrics.Width + (_karaokeStyle.FontSize * 0.12f);
        var countdownOpacity = opacity * state.Opacity;
        const uint redArgb = 0xFFFF0000;
        const uint greenArgb = 0xFF00C000;
        var activeArgb = LerpColor(redArgb, greenArgb, state.ActiveArrowProgress);
        ID2D1SolidColorBrush redBrush = GetBrush(ToColor(redArgb, countdownOpacity));
        ID2D1SolidColorBrush greenBrush = GetBrush(ToColor(greenArgb, countdownOpacity));
        ID2D1SolidColorBrush activeBrush = GetBrush(ToColor(activeArgb, countdownOpacity));

        for (var i = 0; i < 4; i++)
        {
            ID2D1SolidColorBrush brush = i < state.CompletedArrowCount
                ? greenBrush
                : i == state.CompletedArrowCount && state.CompletedArrowCount < 4
                    ? activeBrush
                    : redBrush;
            var arrowOrigin = new Vector2(
                lyricLeft + (i * slotSpacing),
                lyricTop - arrowMetrics.Height - arrowGap);
            target.DrawTextLayout(arrowOrigin, arrowLayout, brush);
        }
    }

    private void DrawTextLayer(
        ID2D1RenderTarget target,
        IDWriteTextLayout layout,
        Vector2 origin,
        uint textArgb,
        uint outlineArgb,
        uint shadowArgb,
        KaraokeLayerStyle style)
    {
        if ((style.ShadowArgb >> 24) != 0)
            target.DrawTextLayout(origin + new Vector2(_karaokeStyle.ShadowOffsetX, _karaokeStyle.ShadowOffsetY), layout, GetBrush(ToColor(shadowArgb)));

        if (style.OutlineWidth > 0 && (style.OutlineArgb >> 24) != 0)
        {
            ID2D1SolidColorBrush outlineBrush = GetBrush(ToColor(outlineArgb));
            for (var x = -1; x <= 1; x++)
            {
                for (var y = -1; y <= 1; y++)
                {
                    if (x != 0 || y != 0)
                        target.DrawTextLayout(origin + new Vector2(x * style.OutlineWidth, y * style.OutlineWidth), layout, outlineBrush);
                }
            }
        }

        target.DrawTextLayout(origin, layout, GetBrush(ToColor(textArgb)));
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

    private void BeginFrame()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _generation++;
    }

    // Disposes what this frame did not use.
    private void EndFrame()
    {
        Evict(_layouts);
        Evict(_formats);
        Evict(_brushes);
    }

    private void Evict<TKey, TValue>(Dictionary<TKey, CacheEntry<TValue>> cache)
        where TKey : notnull
        where TValue : class, IDisposable
    {
        foreach (KeyValuePair<TKey, CacheEntry<TValue>> pair in cache)
        {
            if (pair.Value.LastUsed == _generation)
                continue;

            pair.Value.Value?.Dispose();
            _ = cache.Remove(pair.Key);
        }
    }

    private static void Clear<TKey, TValue>(Dictionary<TKey, CacheEntry<TValue>> cache)
        where TKey : notnull
        where TValue : class, IDisposable
    {
        foreach (CacheEntry<TValue> entry in cache.Values)
            entry.Value?.Dispose();
        cache.Clear();
    }

    private IDWriteTextFormat GetFormat(FormatKey key)
    {
        ref CacheEntry<IDWriteTextFormat> entry = ref CollectionsMarshal.GetValueRefOrAddDefault(_formats, key, out _);
        entry.Value ??= _writeFactory.CreateTextFormat(key.Family, null, key.Weight, key.Style, FontStretch.Normal, key.Size, "");
        entry.LastUsed = _generation;
        return entry.Value;
    }

    private IDWriteTextLayout GetLayout(string text, FormatKey format, float maxWidth, float maxHeight, TextAlignment alignment)
    {
        // The format is looked up on every use so it stays cached as long as its layouts do.
        IDWriteTextFormat textFormat = GetFormat(format);
        ref CacheEntry<IDWriteTextLayout> entry = ref CollectionsMarshal.GetValueRefOrAddDefault(
            _layouts,
            new LayoutKey(text, format, maxWidth, maxHeight, alignment),
            out _);
        if (entry.Value is null)
        {
            IDWriteTextLayout layout = _writeFactory.CreateTextLayout(text, textFormat, maxWidth, maxHeight);
            if (alignment != TextAlignment.Leading)
                layout.TextAlignment = alignment;
            entry.Value = layout;
        }

        entry.LastUsed = _generation;
        return entry.Value;
    }

    private ID2D1SolidColorBrush GetBrush(Color4 color)
    {
        ref CacheEntry<ID2D1SolidColorBrush> entry = ref CollectionsMarshal.GetValueRefOrAddDefault(_brushes, color, out _);
        entry.Value ??= _target!.CreateSolidColorBrush(color);
        entry.LastUsed = _generation;
        return entry.Value;
    }

    // One WIC bitmap and render target, kept while the raster size stays the same. Brushes belong
    // to the render target, so they go with it.
    private ID2D1RenderTarget GetTarget(int width, int height)
    {
        if (_target is not null && _targetWidth == width && _targetHeight == height)
            return _target;

        ReleaseTarget();
        _bitmap = _wicFactory.CreateBitmap((uint)width, (uint)height, PixelFormat.Format32bppPBGRA, BitmapCreateCacheOption.CacheOnLoad);
        _target = _d2dFactory.CreateWicBitmapRenderTarget(
            _bitmap,
            new RenderTargetProperties(new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied)));
        _targetWidth = width;
        _targetHeight = height;
        return _target;
    }

    private void ReleaseTarget()
    {
        Clear(_brushes);
        _target?.Dispose();
        _target = null;
        _bitmap?.Dispose();
        _bitmap = null;
        _targetWidth = 0;
        _targetHeight = 0;
    }

    private SubtitleRasterResult CopyPixels(RenderBounds bounds, int canvasWidth, int canvasHeight, Func<int, Memory<byte>> rentPixels)
    {
        var stride = bounds.Width * 4;
        var length = checked(stride * bounds.Height);
        Memory<byte> pixels = rentPixels(length)[..length];
        _bitmap!.CopyPixels((uint)stride, pixels.Span);
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
            Clear(_layouts);
            Clear(_formats);
            ReleaseTarget();
            _lineLayouts.Clear();
        }

        _wicFactory.Dispose();
        _writeFactory.Dispose();
        _d2dFactory.Dispose();
    }

    private struct CacheEntry<T>
        where T : class
    {
        public T? Value;
        public long LastUsed;
    }

    private readonly record struct FormatKey(string Family, FontWeight Weight, FontStyle Style, float Size);
    private readonly record struct LayoutKey(string Text, FormatKey Format, float MaxWidth, float MaxHeight, TextAlignment Alignment);
    private readonly record struct KaraokeLayerStyle(uint TextArgb, uint OutlineArgb, float OutlineWidth, uint ShadowArgb);
    private readonly record struct RenderBounds(int Left, int Top, int Width, int Height);
}
