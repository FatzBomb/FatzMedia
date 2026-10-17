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
        using VideoDecoder decoder = VideoDecoder.FromConfiguration(configuration, SoftwareOnlyVideoHardwareAccelerationServices.Instance);
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
    }
}
