using System.Diagnostics;
using FatzMedia.Video.Core.Abstractions;
using FatzMedia.Video.Core.Configuration;
using FatzMedia.Video.Core.Graphics.Composition;
using FatzMedia.Video.Core.Media;
using FatzMedia.Video.Core.Threading;
using FatzMedia.Video.Core.Video;
using FatzMedia.Video.Pipeline;
using FFmpeg.AutoGen;
using Xunit.Abstractions;

namespace FatzMedia.Video.Linux.Tests;

/// <summary>
/// ENG-039 batch 5 (P5-5, P5-6) native measurements against a local video with audio. Opt-in like
/// the other native tests: set <c>FATZBOMB_LINUX_FFMPEG_DIR</c>; the media default to
/// <c>/opt/fatzbomb-native/media</c> (override with <c>FATZBOMB_LINUX_MEDIA_DIR</c>). The numbers
/// written to the test output are the batch's before/after measurements.
/// </summary>
public sealed class VideoPipelinePerformanceNativeTests(ITestOutputHelper output)
{
    const string Mp4 = "h264_1080p.mp4";
    const string DefaultMediaDirectory = "/opt/fatzbomb-native/media";

    /// <summary>
    /// P5-5: the video cursor of a local file (audio has its own cursor) discards the streams it
    /// does not play, so FFmpeg neither reads nor returns their packets.
    /// </summary>
    [Fact]
    public unsafe void LocalVideoCursor_DiscardsTheStreamsItDoesNotPlay()
    {
        if (!TryGetEnvironment(out var ffmpegDirectory, out var path))
            return;

        FfmpegAudioReaderNativeTests.ActivateFfmpeg(ffmpegDirectory);
        using var shared = new SharedDemuxer();
        Assert.True(shared.Open(path));
        using var decoder = VideoDecoder.FromConfiguration(
            new MediaPlayerConfiguration(VideoDecodingMethod.Software),
            shared,
            new RendererVideoDecodeServices(SoftwareOnlyVideoHardwareAccelerationServices.Instance));
        Assert.True(decoder.Open(path));

        // Bytes the process read (all threads; this test reads on one) while decoding 5 s of video.
        var readBefore = ReadProcessBytes();
        var stopwatch = Stopwatch.StartNew();
        var frames = 0;
        var last = 0.0;
        for (var attempt = 0; attempt < 100_000 && last < 5; attempt++)
        {
            _ = decoder.DecodeNextFrame();
            while (decoder.GetNextFrame() is { } frame)
            {
                last = frame.PresentationTime;
                frames++;
                decoder.ReturnFrame(frame);
            }
        }

        output.WriteLine($"P5-5 local video cursor: {frames} frames to {last:F2} s, {(ReadProcessBytes() - readBefore) / 1024.0:F0} KiB read, {stopwatch.Elapsed.TotalMilliseconds:F0} ms");

        for (var index = 0; index < (int)shared.StreamCount; index++)
        {
            var expected = index == decoder.VideoStreamIndex ? AVDiscard.AVDISCARD_DEFAULT : AVDiscard.AVDISCARD_ALL;
            Assert.Equal(expected, shared.GetStream(index)->discard);
        }

        // What the discard saves at the demuxer: the packets av_read_frame hands back for the
        // whole file, with every stream (before) and with the picture's only (after).
        (var allPackets, var allMs) = CountPackets(path, discardOthers: false);
        (var videoPackets, var videoMs) = CountPackets(path, discardOthers: true);
        output.WriteLine($"P5-5 demuxer packets for the whole file: {allPackets} without the discard ({allMs:F1} ms), {videoPackets} with it ({videoMs:F1} ms)");
        Assert.True(videoPackets < allPackets);
    }

    static unsafe (int Packets, double Milliseconds) CountPackets(string path, bool discardOthers)
    {
        using var demuxer = new MediaDemuxer();
        Assert.True(demuxer.Open(path));
        var video = demuxer.SelectVideoStream();
        if (discardOthers)
            demuxer.DiscardAllStreamsExcept([video]);

        var packet = ffmpeg.av_packet_alloc();
        var stopwatch = Stopwatch.StartNew();
        var packets = 0;
        try
        {
            while (demuxer.ReadPacket(packet) >= 0)
            {
                packets++;
                ffmpeg.av_packet_unref(packet);
            }
        }
        finally
        {
            ffmpeg.av_packet_free(&packet);
        }

        return (packets, stopwatch.Elapsed.TotalMilliseconds);
    }

    /// <summary>
    /// P5-6: a display-locked presentation pass allocates nothing once it has settled. The timing
    /// source is called once per pass on the presentation thread, so it measures what each pass
    /// allocated on that thread.
    /// </summary>
    [Fact]
    public void DisplayLockedPresentation_AllocatesNothingPerRefresh()
    {
        if (!TryGetEnvironment(out var ffmpegDirectory, out var path))
            return;

        FfmpegAudioReaderNativeTests.ActivateFfmpeg(ffmpegDirectory);
        using var pipeline = new VideoPipeline(
            new PlaybackClock(),
            new MediaState(),
            new MediaPlayerConfiguration(VideoDecodingMethod.Software),
            0,
            new PlaybackSyncState(),
            new SleepWaiterFactory(),
            new FixedRefresh(),
            new RendererVideoDecodeServices(SoftwareOnlyVideoHardwareAccelerationServices.Instance),
            usesAudio: false);
        Assert.True(pipeline.Open(path));
        pipeline.FrameTiming = VideoFrameTiming.DisplayLocked;

        var period = Stopwatch.Frequency / 60;
        var origin = Stopwatch.GetTimestamp();
        IReadOnlyList<string> surfaces = [MediaPresentationSurfaceDescriptor.PrimarySurfaceId];
        var perPass = new long[300];
        var passes = 0;
        var previous = -1L;
        ((IPresentationTimingSink)pipeline).SetPresentationTimingSource(destination =>
        {
            var allocated = GC.GetAllocatedBytesForCurrentThread();
            if (previous >= 0 && passes < perPass.Length)
                perPass[passes++] = allocated - previous;

            var now = Stopwatch.GetTimestamp();
            destination.Add(new PresentationTimingGroup(
                new PresentationVsyncTiming(1, origin + ((now - origin) / period * period), period),
                IncludesPrimary: true,
                surfaces));
            previous = GC.GetAllocatedBytesForCurrentThread();
        });

        Assert.True(pipeline.Play());
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref passes) >= perPass.Length, TimeSpan.FromSeconds(15)), $"P5-6 passes {passes}");
        _ = pipeline.Stop();

        // The first second settles (first frames, the display-locked state report).
        var settled = perPass[60..];
        var allocating = settled.Count(static bytes => bytes > 0);
        output.WriteLine($"P5-6 display-locked: {settled.Sum()} bytes over {settled.Length} passes, {allocating} passes allocated, max {settled.Max()} bytes");
        Assert.Equal(0, settled.Sum());
    }

    /// <summary>
    /// P5-6: the seek loop's look at the decoded frames (whether one is acceptable, whether the head
    /// can be dropped) allocates nothing.
    /// </summary>
    [Fact]
    public void QueuedFrameInspection_AllocatesNothing()
    {
        if (!TryGetEnvironment(out var ffmpegDirectory, out var path))
            return;

        FfmpegAudioReaderNativeTests.ActivateFfmpeg(ffmpegDirectory);
        using var decoder = VideoDecoder.FromConfiguration(
            new MediaPlayerConfiguration(VideoDecodingMethod.Software),
            new RendererVideoDecodeServices(SoftwareOnlyVideoHardwareAccelerationServices.Instance));
        Assert.True(decoder.Open(path));
        for (var attempt = 0; attempt < 10_000 && decoder.FrameCount < 4; attempt++)
            _ = decoder.DecodeNextFrame();

        Assert.True(decoder.FrameCount >= 4, $"frames {decoder.FrameCount}");
        Func<double, double, bool> earlier = static (time, target) => time < target;
        // Two frames are inspected and kept: the predicate rejects them.
        Func<double, double, double, bool> keep = static (head, next, target) => next > head + target;
        _ = decoder.AnyQueuedFrame(10.0, earlier);
        _ = decoder.TryTakeHeadFrame(1e9, keep);
        var before = GC.GetAllocatedBytesForCurrentThread();
        var found = 0;
        for (var call = 0; call < 1000; call++)
        {
            if (decoder.AnyQueuedFrame(10.0, earlier))
                found++;
            if (decoder.TryTakeHeadFrame(1e9, keep) is null)
                found++;
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        output.WriteLine($"P5-6 queued-frame inspection: {allocated} bytes over 1000 seek-loop checks ({decoder.FrameCount} frames queued)");
        Assert.Equal(2000, found);
        Assert.Equal(0, allocated);
    }

    static long ReadProcessBytes()
    {
        foreach (var line in File.ReadLines("/proc/self/io"))
            if (line.StartsWith("rchar:", StringComparison.Ordinal))
                return long.Parse(line.AsSpan("rchar:".Length).Trim(), System.Globalization.CultureInfo.InvariantCulture);

        return 0;
    }

    static bool TryGetEnvironment(out string ffmpeg, out string path)
    {
        ffmpeg = Environment.GetEnvironmentVariable("FATZBOMB_LINUX_FFMPEG_DIR") ?? string.Empty;
        var media = Environment.GetEnvironmentVariable("FATZBOMB_LINUX_MEDIA_DIR") is { Length: > 0 } configured ? configured : DefaultMediaDirectory;
        path = Path.Combine(media, Mp4);
        return OperatingSystem.IsLinux() && !string.IsNullOrWhiteSpace(ffmpeg) && File.Exists(path);
    }

    /// <summary>
    /// Sleeps instead of waiting on the cancellation handle: the runtime's wait on Linux allocates
    /// now and then, and that is the platform waiter's cost, not the pipeline's. Short waits keep
    /// Stop prompt.
    /// </summary>
    sealed class SleepWaiterFactory : IPrecisionWaiterFactory
    {
        public IPrecisionWaiter Create() => new SleepWaiter();

        sealed class SleepWaiter : IPrecisionWaiter
        {
            public bool IsHighResolution => false;

            public void Wait(TimeSpan duration, WaitHandle cancellationHandle)
            {
                if (duration > TimeSpan.Zero)
                    Thread.Sleep(duration);
            }

            public void Dispose()
            {
            }
        }
    }

    sealed class FixedRefresh : IVideoPipelinePlatformServices
    {
        public double GetPresentationRefreshIntervalSeconds() => 1.0 / 60;
    }
}
