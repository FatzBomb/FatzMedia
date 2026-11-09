using FatzMedia.Video.Core.Subtitle;
using FatzMedia.Video.Direct3D11.Subtitle;

namespace FatzMedia.Video.Tests;

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

        // ENG-042: every frame of the wipe after the warm-up is compared, not three samples, so a
        // rare mismatch has more chances to show and its message says which frame it was.
        for (var frame = 30; frame <= 90; frame++)
        {
            TimeSpan position = FramePosition(frame);
            SubtitleRasterResult cached = warm.RenderKaraoke(timing, position, CanvasWidth, CanvasHeight);
            AssertSameRaster(
                $"karaoke frame {frame} ({position.TotalSeconds:0.000} s)",
                () => RenderCold(renderer => renderer.RenderKaraoke(timing, position, CanvasWidth, CanvasHeight)),
                cached);
        }

        // The plain subtitle path shares the caches and the render target.
        SubtitleRasterResult text = warm.Render("A plain subtitle", CanvasWidth, CanvasHeight);
        AssertSameRaster(
            "plain subtitle",
            () => RenderCold(renderer => renderer.Render("A plain subtitle", CanvasWidth, CanvasHeight)),
            text);

        // The caller's buffer receives the same pixels.
        var buffer = new byte[CanvasWidth * CanvasHeight * 4];
        SubtitleRasterResult rented = warm.RenderKaraoke(timing, FramePosition(45), CanvasWidth, CanvasHeight, length => buffer.AsMemory(0, length));
        AssertSameRaster(
            "karaoke frame 45 into the caller's buffer",
            () => RenderCold(renderer => renderer.RenderKaraoke(timing, FramePosition(45), CanvasWidth, CanvasHeight)),
            rented);
    }

    private static SubtitleRasterResult RenderCold(Func<SubtitleTextRenderer, SubtitleRasterResult> render)
    {
        using var renderer = new SubtitleTextRenderer();
        return render(renderer);
    }

    /// <summary>
    /// Compares a cached raster with a fresh renderer's. On a mismatch the message names the frame,
    /// says how many pixels differ, where and by how much, and whether a second fresh renderer
    /// matches the first: if fresh renderers disagree with each other, Direct2D itself is not
    /// deterministic on this machine; if they agree, the cached path drew something else.
    /// </summary>
    private static void AssertSameRaster(string label, Func<SubtitleRasterResult> renderCold, SubtitleRasterResult actual)
    {
        SubtitleRasterResult expected = renderCold();
        var expectedBounds = (expected.Width, expected.Height, expected.Stride, expected.CanvasLeft, expected.CanvasTop);
        var actualBounds = (actual.Width, actual.Height, actual.Stride, actual.CanvasLeft, actual.CanvasTop);
        Assert.True(expectedBounds == actualBounds, $"Cached raster bounds {actualBounds} differ from uncached {expectedBounds} at {label}.");
        Assert.True(actual.Pixels.Span.IndexOfAnyExcept((byte)0) >= 0, $"Cached raster is empty at {label}.");
        if (expected.Pixels.Span.SequenceEqual(actual.Pixels.Span))
            return;

        SubtitleRasterResult secondCold = renderCold();
        var coldAgrees = expected.Pixels.Span.SequenceEqual(secondCold.Pixels.Span);
        Assert.Fail(
            $"Cached raster pixels differ from an uncached raster at {label}. "
            + $"Raster {actual.Width}x{actual.Height} at ({actual.CanvasLeft}, {actual.CanvasTop}). "
            + DescribeDifference(expected, actual)
            + (coldAgrees
                ? " A second uncached raster matches the first: the cached path drew different pixels."
                : " A second uncached raster also differs from the first ("
                    + DescribeDifference(expected, secondCold)
                    + "): Direct2D rasterization is not deterministic here."));
    }

    private static string DescribeDifference(SubtitleRasterResult expected, SubtitleRasterResult actual)
    {
        ReadOnlySpan<byte> left = expected.Pixels.Span;
        ReadOnlySpan<byte> right = actual.Pixels.Span;
        int count = 0, maxDelta = 0, minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        var samples = new List<string>();
        for (var y = 0; y < expected.Height; y++)
        {
            for (var x = 0; x < expected.Width; x++)
            {
                var offset = (y * expected.Stride) + (x * 4);
                var delta = 0;
                for (var channel = 0; channel < 4; channel++)
                    delta = Math.Max(delta, Math.Abs(left[offset + channel] - right[offset + channel]));
                if (delta == 0)
                    continue;

                count++;
                maxDelta = Math.Max(maxDelta, delta);
                (minX, minY, maxX, maxY) = (Math.Min(minX, x), Math.Min(minY, y), Math.Max(maxX, x), Math.Max(maxY, y));
                if (samples.Count < 8)
                {
                    samples.Add(
                        $"({x},{y}) BGRA {Convert.ToHexString(left.Slice(offset, 4))} vs {Convert.ToHexString(right.Slice(offset, 4))}");
                }
            }
        }

        return $"{count} pixel(s) differ, max channel delta {maxDelta}, "
            + $"within x {minX}..{maxX}, y {minY}..{maxY}; first: {string.Join("; ", samples)}.";
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
