using FatzMedia.Video.Core.Video;
using FatzMedia.Video.Linux.Runtime;
using FFmpeg.AutoGen;

namespace FatzMedia.Video.Linux.Tests;

/// <summary>
/// Opt-in native check (set <c>FATZBOMB_LINUX_FFMPEG_DIR</c>, as for
/// <see cref="LinuxNativeSmokeTests"/>): FFmpeg's own error lines are kept for a captured codec
/// context, which the hardware-fallback warning quotes (ENG-011).
/// </summary>
public sealed unsafe class FfmpegLogCaptureNativeTests
{
    [Fact]
    public void CapturedContext_KeepsFfmpegErrorLines()
    {
        var directory = Environment.GetEnvironmentVariable("FATZBOMB_LINUX_FFMPEG_DIR");
        if (string.IsNullOrWhiteSpace(directory) || !OperatingSystem.IsLinux())
            return;

        ActivateFfmpeg(directory);
        var codec = ffmpeg.avcodec_find_decoder(AVCodecID.AV_CODEC_ID_H264);
        Assert.True(codec != null);
        var context = ffmpeg.avcodec_alloc_context3(codec);
        var packet = ffmpeg.av_packet_alloc();
        try
        {
            FfmpegLogCapture.Begin(context);
            Assert.True(ffmpeg.avcodec_open2(context, codec, null) >= 0);

            // A NAL unit with the forbidden-zero bit set makes the H.264 parser log an error.
            byte[] garbage = [0, 0, 0, 1, 0xFF, 0xFF, 0xFF, 0xFF, 0, 0, 0, 1, 0xE5, 0x12, 0x34, 0x56];
            Assert.True(ffmpeg.av_new_packet(packet, garbage.Length) >= 0);
            for (var index = 0; index < garbage.Length; index++)
                packet->data[index] = garbage[index];

            _ = ffmpeg.avcodec_send_packet(context, packet);
            _ = ffmpeg.avcodec_send_packet(context, null);
            var frame = ffmpeg.av_frame_alloc();
            _ = ffmpeg.avcodec_receive_frame(context, frame);
            ffmpeg.av_frame_free(&frame);

            var lines = FfmpegLogCapture.Peek(context);
            Assert.NotEmpty(lines);
            Assert.All(lines, line => Assert.False(string.IsNullOrWhiteSpace(line)));

            FfmpegLogCapture.End(context);
            Assert.Empty(FfmpegLogCapture.Peek(context));
        }
        finally
        {
            FfmpegLogCapture.End(context);
            ffmpeg.av_packet_free(&packet);
            ffmpeg.avcodec_free_context(&context);
        }
    }

    static void ActivateFfmpeg(string directory)
    {
        var operations = new LinuxNativeOperations();
        foreach (var fileName in LinuxNativeLibraryCatalog.FfmpegInLoadOrder)
        {
            var path = Path.Combine(directory, fileName);
            if (File.Exists(path))
                Assert.True(operations.TryLoad(path, out _), $"dlopen failed for {path}");
        }

        Assert.True(operations.TryActivateFfmpeg(directory));
    }
}
