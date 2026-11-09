using FatzMedia.Video.Core.Configuration;
using FatzMedia.Video.Core.Subtitle;
using FatzMedia.Video.Text;

namespace FatzMedia.Video.Tests;

/// <summary>
/// The SkiaSharp subtitle and karaoke rasterizer the software and Vulkan renderers use: premultiplied
/// BGRA bounds on the canvas, the karaoke wipe colours, the caller's buffer and its caches.
/// </summary>
public sealed class SkiaSubtitleRasterizerTests
{
    private const int CanvasWidth = 1920;
    private const int CanvasHeight = 1080;
    private const uint Sung = 0xFFFF0000;
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

    [Theory]
    [InlineData(0.5, false, false)] // before the first syllable: all unsung
    [InlineData(2.0, true, false)] // first word sung, second not
    [InlineData(10.0, true, true)] // the whole line sung
    public void RenderKaraoke_ShowsSungColourLeftOfTheWipeAndUnsungRightOfIt(double seconds, bool leftSung, bool rightSung)
    {
        using var rasterizer = new SkiaSubtitleRasterizer(karaokeStyle: PlainKaraokeStyle());
        var timing = new KaraokeCueTiming(
            [new KaraokeLineTiming("MMMM MMMM", [new(0, 4, TimeSpan.FromSeconds(1)), new(5, 4, TimeSpan.FromSeconds(3))])],
            ActiveLineIndex: 0,
            TransitionStart: TimeSpan.Zero,
            TransitionEnd: TimeSpan.Zero);

        SubtitleRasterResult result = rasterizer.RenderKaraoke(timing, TimeSpan.FromSeconds(seconds), CanvasWidth, CanvasHeight);

        // Opaque pixels across the line's middle band, by canvas x.
        var opaque = new List<(int X, uint Argb)>();
        ReadOnlySpan<byte> pixels = result.Pixels.Span;
        for (var canvasY = (CanvasHeight / 2) - 10; canvasY <= (CanvasHeight / 2) + 10; canvasY++)
        {
            var y = canvasY - result.CanvasTop;
            for (var x = 0; x < result.Width; x++)
            {
                var offset = (y * result.Stride) + (x * 4);
                if (pixels[offset + 3] == 255)
                    opaque.Add((result.CanvasLeft + x, 0xFF000000u | ((uint)pixels[offset + 2] << 16) | ((uint)pixels[offset + 1] << 8) | pixels[offset]));
            }
        }

        Assert.NotEmpty(opaque);
        var left = opaque.Min(static pixel => pixel.X);
        var right = opaque.Max(static pixel => pixel.X);
        var quarter = (right - left) / 4;
        // Sung text is red and unsung white; an opaque glyph-edge pixel can mix the two passes, so
        // classify by the green channel, which only the unsung white carries.
        static bool IsSung(uint argb) => ((argb >> 8) & 0xFF) < 128;
        Assert.All(opaque.Where(pixel => pixel.X < left + quarter), pixel => Assert.Equal(leftSung, IsSung(pixel.Argb)));
        Assert.All(opaque.Where(pixel => pixel.X > right - quarter), pixel => Assert.Equal(rightSung, IsSung(pixel.Argb)));
        Assert.Contains(opaque, pixel => pixel.Argb == (leftSung ? Sung : Unsung));
    }

    [Fact]
    public void RenderKaraoke_PooledOverload_WritesIntoTheRentedBufferAndAllocatesLittle()
    {
        using var rasterizer = new SkiaSubtitleRasterizer();
        KaraokeCueTiming timing = CreateTiming();
        var buffer = new byte[CanvasWidth * CanvasHeight * 4];
        Func<int, Memory<byte>> rent = length => buffer.AsMemory(0, length);

        // Warm the caches, then wipe through the active line at 60 frames per second.
        for (var frame = 0; frame < 10; frame++)
            _ = rasterizer.RenderKaraoke(timing, FramePosition(frame), CanvasWidth, CanvasHeight, rent);

        const int Frames = 120;
        var before = GC.GetAllocatedBytesForCurrentThread();
        SubtitleRasterResult last = default;
        for (var frame = 10; frame < 10 + Frames; frame++)
            last = rasterizer.RenderKaraoke(timing, FramePosition(frame), CanvasWidth, CanvasHeight, rent);
        var perFrame = (GC.GetAllocatedBytesForCurrentThread() - before) / Frames;

        Assert.True(perFrame < 16 * 1024, $"{perFrame} bytes allocated per frame");
        Assert.True(last.Pixels.Span.Overlaps(buffer));
        Assert.Contains(buffer.AsSpan(0, last.Pixels.Length).ToArray(), static value => value != 0);
    }

    [Fact]
    public void RenderKaraoke_CachedPath_MatchesAColdRasterizer()
    {
        KaraokeCueTiming timing = CreateTiming();
        using var warm = new SkiaSubtitleRasterizer();
        for (var frame = 0; frame < 30; frame++)
            _ = warm.RenderKaraoke(timing, FramePosition(frame), CanvasWidth, CanvasHeight);

        foreach (var frame in new[] { 30, 45, 90 })
        {
            using var cold = new SkiaSubtitleRasterizer();
            AssertSameRaster(
                cold.RenderKaraoke(timing, FramePosition(frame), CanvasWidth, CanvasHeight),
                warm.RenderKaraoke(timing, FramePosition(frame), CanvasWidth, CanvasHeight));
        }

        using var coldText = new SkiaSubtitleRasterizer();
        AssertSameRaster(coldText.Render("A plain subtitle", CanvasWidth, CanvasHeight), warm.Render("A plain subtitle", CanvasWidth, CanvasHeight));
    }

    [Fact]
    public void Dispose_IsIdempotentAndRejectsLaterFrames()
    {
        var rasterizer = new SkiaSubtitleRasterizer();
        _ = rasterizer.Render("Before disposal", CanvasWidth, CanvasHeight);

        rasterizer.Dispose();
        rasterizer.Dispose();

        _ = Assert.Throws<ObjectDisposedException>(() => rasterizer.Render("After disposal", CanvasWidth, CanvasHeight));
        _ = Assert.Throws<ObjectDisposedException>(() => rasterizer.RenderKaraoke(CreateTiming(), FramePosition(0), CanvasWidth, CanvasHeight));
    }

    [Fact]
    public void Render_UnknownFontFamily_FallsBackToAnInstalledTypeface()
    {
        using var rasterizer = new SkiaSubtitleRasterizer(new SubtitleTextStyle { FontFamily = "No Such Font Family 1234", FontSize = 44, TextArgb = Unsung });

        SubtitleRasterResult result = rasterizer.Render("Fallback", CanvasWidth, CanvasHeight);

        Assert.Contains(result.Pixels.ToArray(), static value => value == 255);
    }

    private static KaraokeTextStyle PlainKaraokeStyle() => new()
    {
        TextArgb = Unsung,
        WipeTextArgb = Sung,
        OutlineArgb = 0,
        WipeOutlineArgb = 0,
        ShadowArgb = 0,
        WipeShadowArgb = 0,
    };

    private static void AssertSameRaster(SubtitleRasterResult expected, SubtitleRasterResult actual)
    {
        Assert.Equal(
            (expected.Width, expected.Height, expected.Stride, expected.CanvasLeft, expected.CanvasTop),
            (actual.Width, actual.Height, actual.Stride, actual.CanvasLeft, actual.CanvasTop));
        Assert.True(expected.Pixels.Span.SequenceEqual(actual.Pixels.Span), "Cached raster pixels differ from an uncached raster.");
        Assert.Contains(actual.Pixels.ToArray(), static value => value != 0);
    }

    // The second of three lines is active and its syllables start every 250 ms from 1 s.
    internal static KaraokeCueTiming CreateTiming()
    {
        static KaraokeLineTiming Line(string text, double startSeconds)
        {
            var words = text.Split(' ');
            var segments = new List<KaraokeTextSegment>();
            var offset = 0;
            for (var i = 0; i < words.Length; i++)
            {
                segments.Add(new KaraokeTextSegment(offset, words[i].Length, TimeSpan.FromSeconds(startSeconds + (i * 0.25))));
                offset += words[i].Length + 1;
            }

            return new KaraokeLineTiming(text, segments);
        }

        return new KaraokeCueTiming(
            [
                Line("Singing the first line here", -2),
                Line("Now the wipe crosses this line slowly", 1),
                Line("And the next one is waiting", 4),
            ],
            ActiveLineIndex: 1,
            TransitionStart: TimeSpan.Zero,
            TransitionEnd: TimeSpan.FromSeconds(10));
    }

    private static TimeSpan FramePosition(int frame) => TimeSpan.FromSeconds(1 + (frame / 60.0));
}
