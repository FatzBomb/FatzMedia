using System.Buffers;
using System.Diagnostics;
using FatzMedia.Video.Core.Abstractions;
using FatzMedia.Video.Core.Configuration;
using FatzMedia.Video.Core.Video;
using FatzMedia.Video.Linux.Runtime;
using Xunit.Abstractions;

namespace FatzMedia.Video.Linux.Tests;

/// <summary>
/// Stage 010A repeatable benchmark for the software video path. Opt-in: set
/// <c>FATZBOMB_LINUX_FFMPEG_DIR</c> and <c>FATZBOMB_BENCHMARK_VIDEOS</c> (paths separated by
/// <c>;</c>). For each clip it decodes up to 120 frames through Core's software
/// <see cref="VideoDecoder"/> (FFmpeg decode + sws_scale into a pooled BGRA buffer) and times
/// the full-frame copies of the presentation coordinator: (1) frame -> retained composition
/// buffer (still performed), (2) retained buffer -> per-composition snapshot (removed in
/// Stage 010A slice 1 by sharing the retained buffer; still measured as the saved cost).
/// </summary>
public sealed class SoftwareVideoCopyBenchmark(ITestOutputHelper output)
{
    const int MaxFrames = 120;

    [Fact]
    public void MeasureSoftwareFrameCopies()
    {
        var directory = Environment.GetEnvironmentVariable("FATZBOMB_LINUX_FFMPEG_DIR");
        var videos = Environment.GetEnvironmentVariable("FATZBOMB_BENCHMARK_VIDEOS");
        if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(videos) || !OperatingSystem.IsLinux())
            return;

        var operations = new LinuxNativeOperations();
        foreach (var fileName in LinuxNativeLibraryCatalog.FfmpegInLoadOrder)
        {
            var path = Path.Combine(directory, fileName);
            if (File.Exists(path))
                Assert.True(operations.TryLoad(path, out _));
        }

        Assert.True(operations.TryActivateFfmpeg(directory));
        output.WriteLine("| Clip | Frame | Bytes/frame | Decode+convert ms | Managed alloc/frame | Copy 1 ms (GB/s) | Saved copy 2 ms (GB/s) |");
        output.WriteLine("|---|---|---|---|---|---|---|");
        foreach (var video in videos.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            Measure(video);
    }

    void Measure(string path)
    {
        using var decoder = VideoDecoder.FromConfiguration(
            new MediaPlayerConfiguration(VideoDecodingMethod.Software),
            new RendererVideoDecodeServices(SoftwareOnlyVideoHardwareAccelerationServices.Instance));
        Assert.True(decoder.Open(path), $"cannot open {path}");

        var decodeTicks = 0L;
        var allocated = 0L;
        var copy1Ticks = 0L;
        var copy2Ticks = 0L;
        var frames = 0;
        var frameBytes = 0;
        var width = 0;
        var height = 0;
        byte[]? retained = null;
        var snapshotPool = ArrayPool<byte>.Create(64 * 1024 * 1024, 4);

        while (frames < MaxFrames)
        {
            var allocationsBefore = GC.GetAllocatedBytesForCurrentThread();
            var start = Stopwatch.GetTimestamp();
            VideoFrameData? frame = null;
            // DecodeNextFrame can return false while the decoder needs more input; 64 empty
            // attempts in a row means end of stream.
            for (var attempt = 0; attempt < 64 && frame is null; attempt++)
            {
                _ = decoder.DecodeNextFrame();
                frame = decoder.GetNextFrame();
            }

            decodeTicks += Stopwatch.GetTimestamp() - start;
            allocated += GC.GetAllocatedBytesForCurrentThread() - allocationsBefore;
            if (frame?.Data is null)
                break;

            frameBytes = frame.Length;
            (width, height) = (frame.Width, frame.Height);

            // Copy 1: SetCompositionLayer0Frame retains the frame in a reusable buffer.
            retained ??= new byte[frame.Length];
            start = Stopwatch.GetTimestamp();
            frame.Data.AsSpan(0, frame.Length).CopyTo(retained);
            copy1Ticks += Stopwatch.GetTimestamp() - start;

            // Copy 2: each composition snapshot rents and copies the retained buffer.
            start = Stopwatch.GetTimestamp();
            var snapshot = snapshotPool.Rent(frame.Length);
            retained.AsSpan(0, frame.Length).CopyTo(snapshot);
            copy2Ticks += Stopwatch.GetTimestamp() - start;
            snapshotPool.Return(snapshot);

            decoder.ReturnFrame(frame);
            frames++;
        }

        Assert.True(frames > 0, $"no frames decoded from {path}");
        static double Ms(long ticks, int count) => ticks * 1000.0 / Stopwatch.Frequency / count;
        double GbPerSecond(long ticks) => (double)frameBytes * frames / (ticks / (double)Stopwatch.Frequency) / 1e9;
        output.WriteLine(
            $"| {Path.GetFileName(path)} | {width}x{height} | {frameBytes:N0} | {Ms(decodeTicks, frames):F2} | {allocated / frames:N0} B | " +
            $"{Ms(copy1Ticks, frames):F3} ({GbPerSecond(copy1Ticks):F1}) | {Ms(copy2Ticks, frames):F3} ({GbPerSecond(copy2Ticks):F1}) |");
    }
}
