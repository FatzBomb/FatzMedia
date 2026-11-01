using System.Runtime.InteropServices;
using FatzBomb.MediaControl.Core.Configuration;
using FatzBomb.MediaControl.Core.Subtitle;
using SkiaSharp;

namespace FatzBomb.MediaControl.Rendering.Text;

/// <summary>
/// SkiaSharp plain-subtitle rasterizer for the software and Vulkan renderers. It ports the
/// Direct3D11 renderer's DirectWrite subtitle rasterizer (layout, wrapping, outline, shadow) and
/// returns the same raster: premultiplied BGRA8 bounds on a canvas of the requested size.
/// </summary>
/// <remarks>
/// Karaoke lyrics are not drawn here: <c>RenderKaraoke</c> returns an empty result. On the Software
/// and Vulkan renderers they come from FatzAudio's lyric surface in a later stage. Text layouts
/// (with their glyph runs) are cached and an entry not used by a frame is disposed at the end of
/// it; the bitmap and canvas are kept while the raster size stays the same. Calls are serialized
/// by a lock.
/// </remarks>
public sealed class SkiaSubtitleRasterizer : ISubtitleRasterizer
{
    private const int BoldWeight = 700;

    private static readonly Func<int, Memory<byte>> AllocatePixels = static length => new byte[length];

    private readonly SubtitleTextStyle _style;

    // The caches are used by one frame at a time.
    private readonly Lock _gate = new();
    private readonly Dictionary<FaceKey, SkiaFontFace> _faces = [];
    private readonly Dictionary<LayoutKey, CacheEntry> _layouts = [];
    // Created by the first frame, so a missing native SkiaSharp library fails a frame (which Core
    // reports as a Subtitle warning) instead of the renderer factory.
    private SKPaint? _paint;
    // Set when the native library failed to load: later frames draw nothing instead of throwing
    // (and raising a warning) on every frame.
    private bool _nativeUnavailable;
    private long _generation;
    private SKBitmap? _bitmap;
    private SKCanvas? _canvas;
    private bool _disposed;

    /// <summary>Creates a rasterizer with the given styles (the defaults when <see langword="null"/>).</summary>
    public SkiaSubtitleRasterizer(SubtitleTextStyle? style = null)
    {
        _style = (style ?? new SubtitleTextStyle()).Normalize();
    }

    public SubtitleRasterResult Render(string text, int width, int height) =>
        Render(text, width, height, AllocatePixels);

    /// <summary>
    /// Returns an empty result without drawing. Karaoke lyrics on the Software and Vulkan renderers
    /// come from FatzAudio's lyric surface in a later stage; this rasterizer draws plain subtitles only.
    /// </summary>
    public SubtitleRasterResult RenderKaraoke(KaraokeCueTiming timing, TimeSpan position, int width, int height) =>
        default;

    /// <inheritdoc cref="RenderKaraoke(KaraokeCueTiming, TimeSpan, int, int)"/>
    public SubtitleRasterResult RenderKaraoke(KaraokeCueTiming timing, TimeSpan position, int width, int height, Func<int, Memory<byte>> rentPixels) =>
        default;

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

    private void DrawLayout(SKCanvas canvas, SkiaTextLayout layout, float x, float y, SKColor color)
    {
        _paint!.Color = color;
        layout.Draw(canvas, x, y, _paint);
    }

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

    private static SKColor ToColor(uint argb) => new(argb);

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
        }
        catch (Exception ex) when (ex is DllNotFoundException or TypeInitializationException or EntryPointNotFoundException or BadImageFormatException)
        {
            _nativeUnavailable = true;
            throw new InvalidOperationException(
                "The SkiaSharp native library (libSkiaSharp) could not be loaded or does not match SkiaSharp; subtitles are not drawn.",
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
            ReleaseTarget();
            _paint?.Dispose();
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
    private readonly record struct RenderBounds(int Left, int Top, int Width, int Height);
}
