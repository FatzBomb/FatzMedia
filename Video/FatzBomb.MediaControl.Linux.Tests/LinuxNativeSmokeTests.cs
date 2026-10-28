using FatzBomb.MediaControl.Core.Abstractions;
using FatzBomb.MediaControl.Core.Configuration;
using FatzBomb.MediaControl.Core.Video;
using FatzBomb.MediaControl.Linux.Runtime;
using FatzBomb.MediaControl.Linux.Video;

namespace FatzBomb.MediaControl.Linux.Tests;

/// <summary>
/// Opt-in native smoke: set <c>FATZBOMB_LINUX_FFMPEG_DIR</c> to a folder of FFmpeg shared
/// libraries matching FFmpeg.AutoGen's ABI (avcodec major 63, FFmpeg 9). Without it the test returns
/// immediately; CI does not provide native binaries.
/// </summary>
public sealed class LinuxNativeSmokeTests
{
    [Fact]
    public void RealFfmpeg_PreloadsActivatesAndScansSoftwareDecoders()
    {
        var directory = Environment.GetEnvironmentVariable("FATZBOMB_LINUX_FFMPEG_DIR");
        if (string.IsNullOrWhiteSpace(directory) || !OperatingSystem.IsLinux())
            return;

        var operations = new LinuxNativeOperations();
        foreach (var fileName in LinuxNativeLibraryCatalog.FfmpegInLoadOrder)
        {
            var path = Path.Combine(directory, fileName);
            if (File.Exists(path))
                Assert.True(operations.TryLoad(path, out _), $"dlopen failed for {path}");
        }

        Assert.True(operations.TryActivateFfmpeg(directory));
        VideoDecodeCapabilities capabilities = LinuxVideoDecodeCapabilities.GetOrScan(ffmpegLoaded: true);

        Assert.True(capabilities.Codecs.TryGetValue("h264", out CodecCapability? h264), "h264 decoder missing");
        Assert.False(h264!.HardwareDecodeAvailable);
        Assert.True(capabilities.Containers.ContainsKey(".mp4"));
    }

    /// <summary>
    /// Also requires <c>FATZBOMB_LINUX_SAMPLE_VIDEO</c>: decodes the first frame of a real clip
    /// through Core's own software decode path on Linux.
    /// </summary>
    [Fact]
    public void RealFfmpeg_CoreDecoderProducesBgraFrame()
    {
        var directory = Environment.GetEnvironmentVariable("FATZBOMB_LINUX_FFMPEG_DIR");
        var sample = Environment.GetEnvironmentVariable("FATZBOMB_LINUX_SAMPLE_VIDEO");
        if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(sample) || !OperatingSystem.IsLinux())
            return;

        var operations = new LinuxNativeOperations();
        foreach (var fileName in LinuxNativeLibraryCatalog.FfmpegInLoadOrder)
        {
            var path = Path.Combine(directory, fileName);
            if (File.Exists(path))
                Assert.True(operations.TryLoad(path, out _));
        }

        Assert.True(operations.TryActivateFfmpeg(directory));
        var configuration = new MediaPlayerConfiguration(VideoDecodingMethod.Software);
        using VideoDecoder decoder = VideoDecoder.FromConfiguration(configuration, new RendererVideoDecodeServices(SoftwareOnlyVideoHardwareAccelerationServices.Instance));
        Assert.True(decoder.Open(sample), "Core VideoDecoder could not open the sample");

        VideoFrameData? frame = null;
        for (var attempt = 0; attempt < 50 && frame is null; attempt++)
        {
            _ = decoder.DecodeNextFrame();
            frame = decoder.GetNextFrame();
        }

        Assert.NotNull(frame);
        Assert.Equal(VideoFrameKind.CpuBgra, frame.Kind);
        Assert.True(frame.Width > 0 && frame.Height > 0);
        Assert.NotNull(frame.Data);
        Assert.True(frame.Data.Take(frame.Length).Distinct().Count() > 8, "decoded frame looks blank");
        decoder.ReturnFrame(frame);

        // 035A follow-up: the host log line names the stream and the decoder path.
        var description = decoder.DescribeDecodePath();
        Assert.Contains($"{frame.Width}x{frame.Height}", description);
        Assert.Contains($"decoder {decoder.DecoderName} in software", description);
        Assert.DoesNotContain("last FFmpeg error", decoder.DescribeHardwareStartupFailure());
    }

    /// <summary>
    /// ENG-027: a full queue at end of file must hold the flushed decoder's last frames back, not
    /// drop them. Keeps the queue full the whole way (frames are taken only when it is), so end of
    /// file is read with one free slot, then counts every frame against an eager decode.
    /// </summary>
    [Fact]
    public void RealFfmpeg_FullQueueAtEndOfFileKeepsTheLastFrames()
    {
        var directory = Environment.GetEnvironmentVariable("FATZBOMB_LINUX_FFMPEG_DIR");
        var sample = Environment.GetEnvironmentVariable("FATZBOMB_LINUX_SAMPLE_VIDEO");
        if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(sample) || !OperatingSystem.IsLinux())
            return;

        var operations = new LinuxNativeOperations();
        foreach (var fileName in LinuxNativeLibraryCatalog.FfmpegInLoadOrder)
        {
            var path = Path.Combine(directory, fileName);
            if (File.Exists(path))
                Assert.True(operations.TryLoad(path, out _));
        }

        Assert.True(operations.TryActivateFfmpeg(directory));
        var configuration = new MediaPlayerConfiguration(VideoDecodingMethod.Software);

        Assert.Equal(CountFrames(configuration, sample, keepQueueFull: false), CountFrames(configuration, sample, keepQueueFull: true));
    }

    private static int CountFrames(MediaPlayerConfiguration configuration, string sample, bool keepQueueFull)
    {
        using VideoDecoder decoder = VideoDecoder.FromConfiguration(configuration, new RendererVideoDecodeServices(SoftwareOnlyVideoHardwareAccelerationServices.Instance));
        Assert.True(decoder.Open(sample));

        var frames = 0;
        for (var guard = 0; guard < 100_000 && !(decoder.EndOfStream && decoder.FrameCount == 0); guard++)
        {
            _ = decoder.DecodeNextFrame();
            if (!keepQueueFull || decoder.FrameCount >= decoder.MaxFrames || decoder.EndOfStream)
            {
                if (decoder.GetNextFrame() is { } frame)
                {
                    frames++;
                    decoder.ReturnFrame(frame);
                }
            }
        }

        Assert.True(decoder.EndOfStream, "decoder never reached end of stream");
        return frames;
    }
}
