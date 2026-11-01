using FatzBomb.MediaControl.Core.Configuration;
using FatzBomb.MediaControl.Core.Subtitle;
using FatzBomb.MediaControl.Rendering.Text;

namespace FatzBomb.MediaControl.Tests;

/// <summary>
/// The SkiaSharp plain-subtitle rasterizer the software and Vulkan renderers use: premultiplied
/// BGRA bounds on the canvas, the caller's buffer and its caches. Karaoke is not drawn.
/// </summary>
public sealed class SkiaSubtitleRasterizerTests
{
    private const int CanvasWidth = 1920;
    private const int CanvasHeight = 1080;
    private const uint Unsung = 0xFFFFFFFF;

    [Fact]
    public void Render_PlainText_DrawsNearTheBottomCentreOnly()
    {
        using var rasterizer = new SkiaSubtitleRasterizer();

        SubtitleRasterResult result = rasterizer.Render("A plain subtitle", CanvasWidth, CanvasHeight);

        Assert.Equal((CanvasWidth, CanvasHeight), (result.CanvasWidth, result.CanvasHeight));
        Assert.Equal(result.Width * 4, result.Stride);
        Assert.Equal(result.Stride * result.Height, result.Pixels.Length);
        Assert.True(result.Width < CanvasWidth * 0.85 && result.Height < CanvasHeight * 0.2, $"{result.Width}x{result.Height}");
        Assert.True(result.CanvasTop > CanvasHeight * 0.6, $"top {result.CanvasTop}");
        var centre = result.CanvasLeft + (result.Width / 2.0);
        Assert.InRange(centre, (CanvasWidth / 2.0) - 8, (CanvasWidth / 2.0) + 8);

        // Text in the middle, transparent padding around it, and premultiplied colour throughout.
        ReadOnlySpan<byte> pixels = result.Pixels.Span;
        var opaque = 0;
        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            Assert.True(pixels[offset] <= pixels[offset + 3] && pixels[offset + 1] <= pixels[offset + 3] && pixels[offset + 2] <= pixels[offset + 3]);
            if (pixels[offset + 3] == 255)
                opaque++;
        }

        Assert.True(opaque > 500, $"{opaque} opaque pixels");
        for (var x = 0; x < result.Width; x++)
        {
            Assert.Equal(0, pixels[(x * 4) + 3]);
            Assert.Equal(0, pixels[((result.Height - 1) * result.Stride) + (x * 4) + 3]);
        }
    }

    [Fact]
    public void Render_EmptyText_ReturnsAnEmptyResultWithoutRenting()
    {
        using var rasterizer = new SkiaSubtitleRasterizer();
        var rented = 0;

        SubtitleRasterResult result = rasterizer.Render("  \n ", CanvasWidth, CanvasHeight, length =>
        {
            rented++;
            return new byte[length];
        });

        Assert.True(result.Pixels.IsEmpty);
        Assert.Equal(0, rented);
        _ = Assert.Throws<ArgumentNullException>(() => rasterizer.Render(null!, CanvasWidth, CanvasHeight));
    }

    [Fact]
    public void RenderKaraoke_ReturnsAnEmptyResultWithoutDrawingOrAllocating()
    {
        using var rasterizer = new SkiaSubtitleRasterizer();
        KaraokeCueTiming timing = new(
            [new KaraokeLineTiming("Sing along", [new(0, 4, TimeSpan.FromSeconds(1)), new(5, 5, TimeSpan.FromSeconds(2))])],
            ActiveLineIndex: 0,
            TransitionStart: TimeSpan.Zero,
            TransitionEnd: TimeSpan.Zero);
        var rented = 0;
        Func<int, Memory<byte>> rent = length =>
        {
            rented++;
            return new byte[length];
        };

        // Warm up, then measure: karaoke lyrics come from FatzAudio's lyric surface, not this rasterizer.
        _ = rasterizer.RenderKaraoke(timing, TimeSpan.FromSeconds(1.5), CanvasWidth, CanvasHeight, rent);
        var before = GC.GetAllocatedBytesForCurrentThread();
        SubtitleRasterResult pooled = rasterizer.RenderKaraoke(timing, TimeSpan.FromSeconds(1.5), CanvasWidth, CanvasHeight, rent);
        SubtitleRasterResult plain = rasterizer.RenderKaraoke(timing, TimeSpan.FromSeconds(1.5), CanvasWidth, CanvasHeight);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(pooled.Pixels.IsEmpty);
        Assert.True(plain.Pixels.IsEmpty);
        Assert.Equal(0, rented);
        Assert.True(allocated < 1024, $"{allocated} bytes allocated");
    }

    [Fact]
    public void Render_PooledOverload_WritesIntoTheRentedBuffer()
    {
        using var rasterizer = new SkiaSubtitleRasterizer();
        var buffer = new byte[CanvasWidth * CanvasHeight * 4];
        Func<int, Memory<byte>> rent = length => buffer.AsMemory(0, length);

        SubtitleRasterResult result = rasterizer.Render("A plain subtitle", CanvasWidth, CanvasHeight, rent);

        Assert.True(result.Pixels.Span.Overlaps(buffer));
        Assert.Contains(buffer.AsSpan(0, result.Pixels.Length).ToArray(), static value => value != 0);
    }

    [Fact]
    public void Render_CachedPath_MatchesAColdRasterizer()
    {
        using var warm = new SkiaSubtitleRasterizer();
        for (var frame = 0; frame < 5; frame++)
            _ = warm.Render("A plain subtitle", CanvasWidth, CanvasHeight);

        using var cold = new SkiaSubtitleRasterizer();
        AssertSameRaster(cold.Render("A plain subtitle", CanvasWidth, CanvasHeight), warm.Render("A plain subtitle", CanvasWidth, CanvasHeight));
    }

    [Fact]
    public void Dispose_IsIdempotentAndRejectsLaterFrames()
    {
        var rasterizer = new SkiaSubtitleRasterizer();
        _ = rasterizer.Render("Before disposal", CanvasWidth, CanvasHeight);

        rasterizer.Dispose();
        rasterizer.Dispose();

        _ = Assert.Throws<ObjectDisposedException>(() => rasterizer.Render("After disposal", CanvasWidth, CanvasHeight));
    }

    [Fact]
    public void Render_UnknownFontFamily_FallsBackToAnInstalledTypeface()
    {
        using var rasterizer = new SkiaSubtitleRasterizer(new SubtitleTextStyle { FontFamily = "No Such Font Family 1234", FontSize = 44, TextArgb = Unsung });

        SubtitleRasterResult result = rasterizer.Render("Fallback", CanvasWidth, CanvasHeight);

        Assert.Contains(result.Pixels.ToArray(), static value => value == 255);
    }

    private static void AssertSameRaster(SubtitleRasterResult expected, SubtitleRasterResult actual)
    {
        Assert.Equal(
            (expected.Width, expected.Height, expected.Stride, expected.CanvasLeft, expected.CanvasTop),
            (actual.Width, actual.Height, actual.Stride, actual.CanvasLeft, actual.CanvasTop));
        Assert.True(expected.Pixels.Span.SequenceEqual(actual.Pixels.Span), "Cached raster pixels differ from an uncached raster.");
        Assert.Contains(actual.Pixels.ToArray(), static value => value != 0);
    }
}
