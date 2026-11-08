using FatzBomb.MediaControl.Core.Abstractions;
using FatzBomb.MediaControl.Core.Configuration;
using FatzBomb.MediaControl.Core.Graphics;
using FatzBomb.MediaControl.Core.Graphics.Composition;
using FatzBomb.MediaControl.Core.Video;
using FatzBomb.MediaControl.Linux.Runtime;
using FFmpeg.AutoGen;

namespace FatzBomb.MediaControl.Linux.Tests;

/// <summary>
/// Stage 045A slice 2 with the real FFmpeg build: a decoder takes its hardware device from the
/// renderer (<see cref="IMediaRendererVideoDecodeDevice"/>), never from FFmpeg itself. Mesa
/// lavapipe has no video decode queue, so FFmpeg's Vulkan hwaccel declines the stream and the
/// decoder decodes in software, saying so. Needs <c>FATZBOMB_LINUX_FFMPEG_DIR</c> and
/// <c>FATZBOMB_LINUX_SAMPLE_VIDEO</c> (an H.264 clip); returns early without them or without a
/// Vulkan driver.
/// </summary>
public sealed unsafe class RendererDecodeDeviceNativeTests
{
    [Fact]
    public void DecoderUsesTheRenderersDevice_AndFallsBackToSoftwareWithoutAVideoQueue()
    {
        if (!TryLoadFfmpeg(out var sample))
            return;

        // FFmpeg's own Vulkan device on lavapipe stands in for the renderer's.
        AVBufferRef* device = null;
        if (ffmpeg.av_hwdevice_ctx_create(&device, AVHWDeviceType.AV_HWDEVICE_TYPE_VULKAN, null, null, 0) < 0 || device == null)
            return;

        try
        {
            var renderer = new SuppliedDevice(device);
            var services = new RendererVideoDecodeServices(SoftwareOnlyVideoHardwareAccelerationServices.Instance);
            services.Attach(renderer);
            using VideoDecoder decoder = VideoDecoder.FromConfiguration(new MediaPlayerConfiguration(VideoDecodingMethod.Auto), services);

            Assert.True(decoder.Open(sample));
            Assert.Equal("vulkan", decoder.HardwareAccelerationMethod);
            Assert.True(renderer.ContextsCreated >= 1, "the decoder did not ask the renderer for its device");

            VideoFrameData? frame = null;
            for (var attempt = 0; attempt < 100 && frame is null; attempt++)
            {
                _ = decoder.DecodeNextFrame();
                frame = decoder.GetNextFrame();
            }

            Assert.NotNull(frame);
            Assert.Equal(VideoFrameKind.CpuBgra, frame.Kind);
            decoder.ReturnFrame(frame);
            Assert.Contains("in software (vulkan configured but not used)", decoder.DescribeDecodePath(), StringComparison.Ordinal);
        }
        finally
        {
            ffmpeg.av_buffer_unref(&device);
        }
    }

    [Fact]
    public void RendererWithoutVideoDecoding_DecodesInSoftware()
    {
        if (!TryLoadFfmpeg(out var sample))
            return;

        var services = new RendererVideoDecodeServices(SoftwareOnlyVideoHardwareAccelerationServices.Instance);
        services.Attach(new NoDecodingDevice());
        using VideoDecoder decoder = VideoDecoder.FromConfiguration(new MediaPlayerConfiguration(VideoDecodingMethod.Auto), services);

        Assert.True(decoder.Open(sample));
        Assert.Null(decoder.HardwareAccelerationMethod);
        Assert.Contains("in software", decoder.DescribeDecodePath(), StringComparison.Ordinal);
    }

    private static bool TryLoadFfmpeg(out string sample)
    {
        var directory = Environment.GetEnvironmentVariable("FATZBOMB_LINUX_FFMPEG_DIR");
        sample = Environment.GetEnvironmentVariable("FATZBOMB_LINUX_SAMPLE_VIDEO") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(sample) || !OperatingSystem.IsLinux())
            return false;

        var operations = new LinuxNativeOperations();
        foreach (var fileName in LinuxNativeLibraryCatalog.FfmpegInLoadOrder)
        {
            var path = Path.Combine(directory, fileName);
            if (File.Exists(path))
                Assert.True(operations.TryLoad(path, out _));
        }

        Assert.True(operations.TryActivateFfmpeg(directory));
        return true;
    }

    /// <summary>Hands out references to one FFmpeg Vulkan device context, as the Vulkan renderer does.</summary>
    private sealed class SuppliedDevice(AVBufferRef* device) : IMediaRendererVideoDecodeDevice
    {
        public int ContextsCreated { get; private set; }

        public string? VideoDecodeDeviceType => "vulkan";

        public string? VideoDecodeUnavailableReason => null;

        public nint CreateVideoDecodeDeviceContext(out string? failureReason)
        {
            ContextsCreated++;
            failureReason = null;
            return (nint)ffmpeg.av_buffer_ref(device);
        }

        public NativeVideoDeviceResource? CreateVideoDecodeDeviceResource(nint ffmpegHardwareDeviceContext) =>
            new(MediaGraphicsBackendId.Vulkan, 1);

        public NativeVideoFrameResource? CreateVideoDecodeFrameResource(NativeVideoDeviceResource device, nint ffmpegFrame) =>
            new(device, ffmpegFrame, 0);

        public bool IsVideoDecodeDeviceLost(NativeVideoDeviceResource device) => false;
    }

    private sealed class NoDecodingDevice : IMediaRendererVideoDecodeDevice
    {
        public string? VideoDecodeDeviceType => null;

        public string? VideoDecodeUnavailableReason => "the Vulkan device has no video decode queue";

        public nint CreateVideoDecodeDeviceContext(out string? failureReason) =>
            throw new InvalidOperationException("A renderer that cannot decode is never asked for a device.");

        public NativeVideoDeviceResource? CreateVideoDecodeDeviceResource(nint ffmpegHardwareDeviceContext) => null;

        public NativeVideoFrameResource? CreateVideoDecodeFrameResource(NativeVideoDeviceResource device, nint ffmpegFrame) => null;

        public bool IsVideoDecodeDeviceLost(NativeVideoDeviceResource device) => false;
    }
}
