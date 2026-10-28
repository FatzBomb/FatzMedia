using FatzBomb.MediaControl.Core.Subtitle;
using FatzBomb.MediaControl.Rendering.Direct3D11.Subtitle;

namespace FatzBomb.MediaControl.Tests;

/// <summary>
/// ENG-039 P5-2: the DirectWrite karaoke rasterizer caches its formats, layouts, brushes and
/// render target, and writes into the caller's buffer, so a steady wipe stops allocating a raster
/// per frame. Windows only (DirectWrite, Direct2D and WIC).
/// </summary>
public sealed class D3D11SubtitleTextRendererTests
{
    private const int CanvasWidth = 1920;
    private const int CanvasHeight = 1080;

    [Fact]
    public void RenderKaraoke_SteadyWipe_AllocatesUnder64KbPerFrame()
    {
        using var renderer = new SubtitleTextRenderer();
        KaraokeCueTiming timing = CreateTiming();
        var buffer = new byte[CanvasWidth * CanvasHeight * 4];
        Func<int, Memory<byte>> rent = length => buffer.AsMemory(0, length);

        // Warm the caches, then wipe through the active line at 60 frames per second.
        for (var frame = 0; frame < 10; frame++)
            _ = renderer.RenderKaraoke(timing, FramePosition(frame), CanvasWidth, CanvasHeight, rent);

        const int Frames = 120;
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var frame = 10; frame < 10 + Frames; frame++)
            _ = renderer.RenderKaraoke(timing, FramePosition(frame), CanvasWidth, CanvasHeight, rent);
        var perFrame = (GC.GetAllocatedBytesForCurrentThread() - before) / Frames;

        Assert.True(perFrame < 64 * 1024, $"{perFrame} bytes allocated per frame");
    }

    [Fact]
    public void RenderKaraoke_CachedPath_MatchesUncachedPixels()
    {
        KaraokeCueTiming timing = CreateTiming();
        using var warm = new SubtitleTextRenderer();
        for (var frame = 0; frame < 30; frame++)
            _ = warm.RenderKaraoke(timing, FramePosition(frame), CanvasWidth, CanvasHeight);

        foreach (var frame in new[] { 30, 45, 90 })
        {
            TimeSpan position = FramePosition(frame);
            SubtitleRasterResult cached = warm.RenderKaraoke(timing, position, CanvasWidth, CanvasHeight);
            using var cold = new SubtitleTextRenderer();
            SubtitleRasterResult uncached = cold.RenderKaraoke(timing, position, CanvasWidth, CanvasHeight);

            AssertSameRaster(uncached, cached);
        }

        // The plain subtitle path shares the caches and the render target.
        SubtitleRasterResult text = warm.Render("A plain subtitle", CanvasWidth, CanvasHeight);
        using var coldText = new SubtitleTextRenderer();
        AssertSameRaster(coldText.Render("A plain subtitle", CanvasWidth, CanvasHeight), text);

        // The caller's buffer receives the same pixels.
        var buffer = new byte[CanvasWidth * CanvasHeight * 4];
        SubtitleRasterResult rented = warm.RenderKaraoke(timing, FramePosition(45), CanvasWidth, CanvasHeight, length => buffer.AsMemory(0, length));
        using var coldRented = new SubtitleTextRenderer();
        AssertSameRaster(coldRented.RenderKaraoke(timing, FramePosition(45), CanvasWidth, CanvasHeight), rented);
    }

    private static void AssertSameRaster(SubtitleRasterResult expected, SubtitleRasterResult actual)
    {
        Assert.Equal(
            (expected.Width, expected.Height, expected.Stride, expected.CanvasLeft, expected.CanvasTop),
            (actual.Width, actual.Height, actual.Stride, actual.CanvasLeft, actual.CanvasTop));
        Assert.True(expected.Pixels.Span.SequenceEqual(actual.Pixels.Span), "Cached raster pixels differ from an uncached raster.");
        Assert.Contains(actual.Pixels.ToArray(), value => value != 0);
    }

    // The second of three lines is active and its syllables start every 250 ms from 1 s.
    private static KaraokeCueTiming CreateTiming()
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
