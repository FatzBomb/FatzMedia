using System.Runtime.InteropServices;
using FatzMedia.Video.Core.Graphics;
using FFmpeg.AutoGen;
using Silk.NET.Vulkan;
using Xunit.Abstractions;

namespace FatzMedia.Video.Vulkan.Tests;

/// <summary>
/// Stage 045A slice 2: FFmpeg Vulkan Video on the renderer's device. Mesa lavapipe has no video
/// decode queue, so decoding itself cannot run here. What can:
/// <list type="bullet">
/// <item>the probe's reason and the backend's refusal on a device without Vulkan Video;</item>
/// <item>FFmpeg accepting the renderer's device (the <c>AVVulkanDeviceContext</c> layout and
/// callbacks), and frames allocated and uploaded by FFmpeg on it, standing in for decoded ones;</item>
/// <item>the conversion of NV12 and P010 frames against swscale (the software path) and, for HDR10
/// and HLG, against a CPU port of the tone-map math, with FFmpeg's frame semaphores advanced.</item>
/// </list>
/// The FFmpeg tests need <c>FATZBOMB_LINUX_FFMPEG_DIR</c> (scripts/setup-linux-native.sh) and return
/// early without it.
/// </summary>
public sealed unsafe class VulkanVideoDecodeTests(ITestOutputHelper output)
{
    [Fact]
    public void FfmpegStructs_MatchHwcontextVulkanH()
    {
        // FFmpeg 9.0 (libavutil 61), 64-bit: offsets from hwcontext_vulkan.h.
        Assert.Equal(40, (int)Marshal.OffsetOf<AVVulkanDeviceContext>(nameof(AVVulkanDeviceContext.DeviceFeatures)));
        Assert.Equal(280, (int)Marshal.OffsetOf<AVVulkanDeviceContext>(nameof(AVVulkanDeviceContext.EnabledInstanceExtensions)));
        Assert.Equal(312, (int)Marshal.OffsetOf<AVVulkanDeviceContext>(nameof(AVVulkanDeviceContext.LockQueue)));
        Assert.Equal(328, (int)Marshal.OffsetOf<AVVulkanDeviceContext>(nameof(AVVulkanDeviceContext.QueueFamilies)));
        Assert.Equal(1352, (int)Marshal.OffsetOf<AVVulkanDeviceContext>(nameof(AVVulkanDeviceContext.QueueFamilyCount)));
        Assert.Equal(1360, sizeof(AVVulkanDeviceContext));

        Assert.Equal(88, (int)Marshal.OffsetOf<AVVulkanFramesContext>(nameof(AVVulkanFramesContext.Format)));
        Assert.Equal(128, (int)Marshal.OffsetOf<AVVulkanFramesContext>(nameof(AVVulkanFramesContext.LockFrame)));
        Assert.Equal(144, sizeof(AVVulkanFramesContext));

        Assert.Equal(208, (int)Marshal.OffsetOf<AVVkFrame>(nameof(AVVkFrame.Access)));
        Assert.Equal(272, (int)Marshal.OffsetOf<AVVkFrame>(nameof(AVVkFrame.Layout)));
        Assert.Equal(304, (int)Marshal.OffsetOf<AVVkFrame>(nameof(AVVkFrame.Semaphore)));
        Assert.Equal(368, (int)Marshal.OffsetOf<AVVkFrame>(nameof(AVVkFrame.SemaphoreValue)));
        Assert.Equal(496, (int)Marshal.OffsetOf<AVVkFrame>(nameof(AVVkFrame.QueueFamily)));
        Assert.Equal(536, sizeof(AVVkFrame));
    }

    [Theory]
    [InlineData(8, VideoColorRange.Limited, 16.0 / 255, 1.0, 235.0 / 255)]
    [InlineData(8, VideoColorRange.Full, 0.0, 1.0, 1.0)]
    [InlineData(10, VideoColorRange.Limited, 64 * 64 / 65535.0, 1.0, 940 * 64 / 65535.0)]
    public void Conversion_MapsBlackAndWhiteCodesAcrossRangesAndDepths(int bitDepth, VideoColorRange range, double black, double one, double white)
    {
        var constants = VulkanVideoConversion.Create(new VideoColorSpace(VideoColorMatrix.Bt709, range), null, bitDepth, 1920, 1080, 1920, 1088);
        var neutral = range == VideoColorRange.Full
            ? (bitDepth == 8 ? 128.0 / 255 : 512 * 64 / 65535.0) * one
            : (bitDepth == 8 ? 128.0 / 255 : 512 * 64 / 65535.0);
        (var r, var g, var b) = VulkanVideoConversion.ConvertSdr(constants, black, neutral, neutral);
        Assert.Equal((0.0, 0.0, 0.0), (Math.Round(r, 4), Math.Round(g, 4), Math.Round(b, 4)));
        (r, g, b) = VulkanVideoConversion.ConvertSdr(constants, white, neutral, neutral);
        Assert.Equal((1.0, 1.0, 1.0), (Math.Round(r, 3), Math.Round(g, 3), Math.Round(b, 3)));
        // The display is the top 1080 rows of the 1088 decoded.
        Assert.Equal(1080 / 1088f, constants.TextureScale.Y, 5);
        Assert.Equal(0f, constants.Transfer.W);
    }

    [Fact]
    public void Conversion_ToneMapsOnlyWithARequest()
    {
        var request = new HdrToneMapRequest(1000, 203) { Transfer = HdrTransferFunction.Hlg };
        var constants = VulkanVideoConversion.Create(new VideoColorSpace(VideoColorMatrix.Bt2020, VideoColorRange.Limited), request, 10, 3840, 2160, 3840, 2160);
        Assert.Equal(1f, constants.Transfer.W);
        Assert.Equal(1f, constants.Transfer.X);
        Assert.Equal(1000f, constants.Transfer.Y);
        Assert.Equal((float)(request.HlgSystemGamma - 1), constants.Transfer.Z, 5);
        Assert.Equal(1f, constants.Curve.W);
        Assert.Equal(1 / 203f, constants.Output.X, 6);
    }

    [Fact]
    public void DeviceWithoutVideoQueue_SaysWhyAndOffersNoDecoding()
    {
        using var compositor = VulkanOffscreenCompositor.TryCreate(out _);
        if (compositor is null || compositor.VideoDecode is not null)
            return;

        Assert.False(string.IsNullOrEmpty(compositor.VideoDecodeUnavailableReason));
        Assert.Equal(0, compositor.CreateVideoDecodeDeviceContext(out var failure));
        Assert.Equal(compositor.VideoDecodeUnavailableReason, failure);

        using var backend = new VulkanRendererBackend(() => VulkanOffscreenCompositor.TryCreate(out _));
        Assert.Null(backend.VideoDecodeDeviceType);
        Assert.Equal(compositor.VideoDecodeUnavailableReason, backend.VideoDecodeUnavailableReason);
        Assert.Equal(0, backend.CreateVideoDecodeDeviceContext(out failure));
        Assert.NotNull(failure);

        var availability = new VulkanPresentationCoordinatorFactory().ProbeAvailability();
        Assert.True(availability.RequiresSoftwareDecoding);
        Assert.Contains("no Vulkan Video decoding: " + compositor.VideoDecodeUnavailableReason, availability.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Nv12Frame_ConvertsLikeSwscale_AndAdvancesFfmpegsSemaphore(bool imagePerPlane)
    {
        using var fixture = VideoFrameFixture.Create(AVPixelFormat.AV_PIX_FMT_NV12, 64, 48, imagePerPlane);
        if (fixture is null)
            return;

        // Decoders output one multi-planar image; FFmpeg's other layout is one image per plane.
        Assert.Equal(imagePerPlane ? 2 : 1, fixture.ImageCount);

        fixture.Fill(static (x, y) => (16 + (x * 2) + y, 64 + ((x / 16) * 40), 200 - ((y / 16) * 50)));
        VideoColorSpace colorSpace = new(VideoColorMatrix.Bt709, VideoColorRange.Limited);
        (var valueBefore, _) = fixture.FrameState();

        // Shown at 60x46 of the 64x48 decoded: the padding stays out of the picture.
        var gpu = fixture.Render(colorSpace, null, 60, 46, revision: 1);
        var cpu = fixture.Swscale(AVPixelFormat.AV_PIX_FMT_BGRA, ffmpeg.SWS_CS_ITU709);

        AssertClose(gpu, 60, cpu, 64, 60, 46, tolerance: 3, skipChromaEdges: true);
        (var valueAfter, var layout) = fixture.FrameState();
        Assert.Equal(valueBefore + 1, valueAfter);
        Assert.Equal((int)ImageLayout.ShaderReadOnlyOptimal, layout);
        Assert.Equal(1, fixture.Compositor.VideoConversionCount);
    }

    [Fact]
    public void Frame_ConvertsOncePerRevision_AndTheLayerIsRetained()
    {
        using var fixture = VideoFrameFixture.Create(AVPixelFormat.AV_PIX_FMT_NV12, 32, 32);
        if (fixture is null)
            return;

        fixture.Fill(static (x, y) => (40 + (x * 4), 128, 128));
        VideoColorSpace colorSpace = new(VideoColorMatrix.Bt709, VideoColorRange.Limited);
        var first = fixture.Render(colorSpace, null, 32, 32, revision: 7);
        // Another target of the same composition: the layer already holds the frame.
        var second = fixture.Render(colorSpace, null, 32, 32, revision: 7);
        Assert.Equal(first, second);
        Assert.Equal(1, fixture.Compositor.VideoConversionCount);

        // A refresh with the retained background draws the kept layer.
        var retained = fixture.RenderRetained(32, 32);
        Assert.Equal(first, retained);

        // Once Core clears the background there is nothing to keep.
        fixture.Compositor.ClearVideoLayer();
        var cleared = fixture.RenderRetained(32, 32);
        Assert.All(Enumerable.Range(0, 32 * 32), pixel => Assert.Equal(0, cleared[(pixel * 4) + 1]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NewFramesOfOnePool_ReusePlaneViews_AndAllocateNothing(bool imagePerPlane)
    {
        // ENG-039 P5-10: FFmpeg's pool hands the same images out again; their plane views are
        // created once, and a conversion allocates no holder objects (376 bytes and two
        // vkCreateImageView per decoded frame before).
        using var fixture = VideoFrameFixture.Create(AVPixelFormat.AV_PIX_FMT_NV12, 64, 48, imagePerPlane);
        if (fixture is null)
            return;

        fixture.Fill(static (x, y) => (40 + x, 128, 128));
        VideoColorSpace colorSpace = new(VideoColorMatrix.Bt709, VideoColorRange.Limited);
        var plans = new VulkanFramePlan[40];
        for (var i = 0; i < plans.Length; i++)
            plans[i] = fixture.Plan(colorSpace, 64, 48, revision: i + 1);

        var first = fixture.Compositor.Render(plans[0]).ToArray();
        for (var i = 1; i < 10; i++)
            _ = fixture.Compositor.Render(plans[i]);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 10; i < plans.Length; i++)
            _ = fixture.Compositor.Render(plans[i]);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(plans.Length, fixture.Compositor.VideoConversionCount);
        Assert.Equal(2, fixture.Compositor.FrameViewCreateCount);
        Assert.Equal(2, fixture.Compositor.PlaneViewCount);
        Assert.True(allocated < 1024, $"{allocated} bytes for {plans.Length - 10} conversions");
        Assert.Equal(first, fixture.Compositor.Render(plans[^1] with { Background = plans[^1].Background!.Value with { Revision = 1000 } }).ToArray());

        // Core cleared the background (the decoder may go): the views and the pool's reference go too.
        fixture.Compositor.ClearVideoLayer();
        Assert.Equal(0, fixture.Compositor.PlaneViewCount);
        Assert.Equal(first, fixture.Compositor.Render(plans[0]).ToArray());
        Assert.Equal(4, fixture.Compositor.FrameViewCreateCount);
    }

    [Fact]
    public void P010SdrFrame_ConvertsLikeSwscale()
    {
        using var fixture = VideoFrameFixture.Create(AVPixelFormat.AV_PIX_FMT_P010LE, 64, 32);
        if (fixture is null)
            return;

        fixture.Fill(static (x, y) => (64 + (x * 12) + (y * 3), 300 + ((x / 16) * 120), 700 - ((y / 16) * 200)));
        var gpu = fixture.Render(new VideoColorSpace(VideoColorMatrix.Bt709, VideoColorRange.Limited), null, 64, 32, revision: 1);
        var cpu = fixture.Swscale(AVPixelFormat.AV_PIX_FMT_BGRA, ffmpeg.SWS_CS_ITU709);
        AssertClose(gpu, 64, cpu, 64, 64, 32, tolerance: 3, skipChromaEdges: true);
        Assert.Equal(1, fixture.ImageCount);
        output.WriteLine($"P010 frame image format: {fixture.ImageFormat}");
    }

    [Theory]
    [InlineData(HdrTransferFunction.Pq, 1000.0)]
    [InlineData(HdrTransferFunction.Hlg, 1000.0)]
    public void HdrP010Frame_IsToneMappedLikeTheCpuMath(HdrTransferFunction transfer, double sourcePeakNits)
    {
        using var fixture = VideoFrameFixture.Create(AVPixelFormat.AV_PIX_FMT_P010LE, 64, 32);
        if (fixture is null)
            return;

        // Uniform chroma per 16x16 block, a luma ramp across the HDR range.
        fixture.Fill(static (x, y) => (64 + (x * 13 % 877), 420 + ((y / 16) * 160), 460 + ((x / 16) * 60)));
        var request = new HdrToneMapRequest(sourcePeakNits, 203) { Transfer = transfer };
        VideoColorSpace colorSpace = new(VideoColorMatrix.Bt2020, VideoColorRange.Limited);
        var gpu = fixture.Render(colorSpace, request, 64, 32, revision: 1);

        var mismatches = 0;
        for (var y = 0; y < 32; y++)
        {
            for (var x = 0; x < 64; x++)
            {
                if (x % 16 is 0 or 15 || y % 16 is 0 or 15)
                    continue;

                (var luma, var cb, var cr) = fixture.Codes(x, y);
                (var r, var g, var b) = ToneMapReference.ToSdr(luma, cb, cr, request);
                var at = ((y * 64) + x) * 4;
                if (Math.Abs(gpu[at + 2] - r) > 3 || Math.Abs(gpu[at + 1] - g) > 3 || Math.Abs(gpu[at] - b) > 3)
                    mismatches++;
            }
        }

        Assert.Equal(0, mismatches);
        // The ramp spans the range, from shadows to highlights compressed towards SDR white.
        Assert.InRange(gpu.Where((_, index) => index % 4 != 3).Max(), (byte)150, (byte)255);
        Assert.InRange(gpu.Where((_, index) => index % 4 != 3).Min(), (byte)0, (byte)40);
    }

    static void AssertClose(byte[] actual, int actualWidth, byte[] expected, int expectedWidth, int width, int height, int tolerance, bool skipChromaEdges)
    {
        var worst = 0;
        (int X, int Y) worstAt = default;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                // Chroma is constant per 16x16 block; the two renderers interpolate across block
                // edges differently (bilinear vs swscale's siting), so only block interiors compare.
                // The last row and column are sampled one luma texel inside the picture (as the
                // Direct3D11 shader does), blending them with their neighbours.
                if ((skipChromaEdges && (x % 16 is 0 or 15 || y % 16 is 0 or 15)) || x == width - 1 || y == height - 1)
                    continue;

                for (var c = 0; c < 3; c++)
                {
                    var difference = Math.Abs(actual[(((y * actualWidth) + x) * 4) + c] - expected[(((y * expectedWidth) + x) * 4) + c]);
                    if (difference > worst)
                    {
                        worst = difference;
                        worstAt = (x, y);
                    }
                }
                Assert.Equal(255, actual[(((y * actualWidth) + x) * 4) + 3]);
            }
        }

        Assert.True(worst <= tolerance, $"largest difference {worst} > {tolerance} at {worstAt}; first pixels {string.Join(",", actual.Take(16))} vs {string.Join(",", expected.Take(16))}; row 20 {string.Join(",", actual.Skip(((20 * actualWidth) + 20) * 4).Take(8))} vs {string.Join(",", expected.Skip(((20 * expectedWidth) + 20) * 4).Take(8))}");
    }

    /// <summary>
    /// FFmpeg on the renderer's device: a Vulkan frames context and one frame FFmpeg allocated and
    /// uploaded there (as a decoder's output would be), with its software copy for references.
    /// </summary>
    sealed class VideoFrameFixture : IDisposable
    {
        readonly AVPixelFormat _format;
        AVBufferRef* _device;
        AVBufferRef* _frames;
        AVFrame* _hardware;
        AVFrame* _software;

        VideoFrameFixture(VulkanOffscreenCompositor compositor, AVPixelFormat format, int width, int height)
        {
            Compositor = compositor;
            _format = format;
            Width = width;
            Height = height;
        }

        public VulkanOffscreenCompositor Compositor { get; }
        public int Width { get; }
        public int Height { get; }

        public static VideoFrameFixture? Create(AVPixelFormat format, int width, int height, bool imagePerPlane = false)
        {
            if (!FfmpegForTests.TryLoad())
                return null;

            var compositor = VulkanOffscreenCompositor.TryCreate(out _, shareWithFfmpegWithoutDecode: true);
            if (compositor?.SharedDevice?.VideoDecode is null)
            {
                compositor?.Dispose();
                return null;
            }

            var fixture = new VideoFrameFixture(compositor, format, width, height);
            try
            {
                fixture.Allocate(imagePerPlane);
                return fixture;
            }
            catch
            {
                fixture.Dispose();
                throw;
            }
        }

        void Allocate(bool imagePerPlane)
        {
            var context = Compositor.SharedDevice!.CreateFfmpegDeviceContextReference(out var failure);
            Assert.True(context != 0, $"FFmpeg refused the renderer's device: {failure}");
            _device = (AVBufferRef*)context;

            _frames = ffmpeg.av_hwframe_ctx_alloc(_device);
            var frames = (AVHWFramesContext*)_frames->data;
            frames->format = AVPixelFormat.AV_PIX_FMT_VULKAN;
            frames->sw_format = _format;
            frames->width = Width;
            frames->height = Height;
            if (imagePerPlane)
            {
                // AV_VK_FRAME_FLAG_DISABLE_MULTIPLANE.
                ((AVVulkanFramesContext*)frames->hwctx)->Flags = 1 << 2;
            }

            Check(ffmpeg.av_hwframe_ctx_init(_frames), "av_hwframe_ctx_init");

            _hardware = ffmpeg.av_frame_alloc();
            Check(ffmpeg.av_hwframe_get_buffer(_frames, _hardware, 0), "av_hwframe_get_buffer");
            _software = ffmpeg.av_frame_alloc();
            _software->format = (int)_format;
            _software->width = Width;
            _software->height = Height;
            Check(ffmpeg.av_frame_get_buffer(_software, 0), "av_frame_get_buffer");
        }

        /// <summary>Writes luma, Cb and Cr codes (8 or 10 bits) and uploads them to the GPU frame.</summary>
        public void Fill(Func<int, int, (int Y, int Cb, int Cr)> codes)
        {
            var tenBit = _format != AVPixelFormat.AV_PIX_FMT_NV12;
            for (var y = 0; y < Height; y++)
            {
                for (var x = 0; x < Width; x++)
                {
                    var value = codes(x, y).Y;
                    if (tenBit)
                        ((ushort*)(_software->data[0] + (y * _software->linesize[0])))[x] = (ushort)(value << 6);
                    else
                        _software->data[0][(y * _software->linesize[0]) + x] = (byte)value;
                }
            }

            for (var y = 0; y < Height / 2; y++)
            {
                for (var x = 0; x < Width / 2; x++)
                {
                    (_, var cb, var cr) = codes(x * 2, y * 2);
                    if (tenBit)
                    {
                        var row = (ushort*)(_software->data[1] + (y * _software->linesize[1]));
                        row[x * 2] = (ushort)(cb << 6);
                        row[(x * 2) + 1] = (ushort)(cr << 6);
                    }
                    else
                    {
                        _software->data[1][(y * _software->linesize[1]) + (x * 2)] = (byte)cb;
                        _software->data[1][(y * _software->linesize[1]) + (x * 2) + 1] = (byte)cr;
                    }
                }
            }

            Check(ffmpeg.av_hwframe_transfer_data(_hardware, _software, 0), "av_hwframe_transfer_data (upload)");
        }

        /// <summary>The codes at a luma position (chroma of its 2x2 block).</summary>
        public (int Y, int Cb, int Cr) Codes(int x, int y)
        {
            if (_format == AVPixelFormat.AV_PIX_FMT_NV12)
            {
                return (
                    _software->data[0][(y * _software->linesize[0]) + x],
                    _software->data[1][((y / 2) * _software->linesize[1]) + ((x / 2) * 2)],
                    _software->data[1][((y / 2) * _software->linesize[1]) + ((x / 2) * 2) + 1]);
            }

            var chroma = (ushort*)(_software->data[1] + ((y / 2) * _software->linesize[1]));
            return (
                ((ushort*)(_software->data[0] + (y * _software->linesize[0])))[x] >> 6,
                chroma[(x / 2) * 2] >> 6,
                chroma[((x / 2) * 2) + 1] >> 6);
        }

        /// <summary>Gets how many images FFmpeg allocated per frame (1: multi-planar, 2: one per plane).</summary>
        public int ImageCount => ((AVVkFrame*)_hardware->data[0])->Image[1] != 0 ? 2 : 1;

        /// <summary>Gets the first image's format.</summary>
        public Format ImageFormat => (Format)((AVVulkanFramesContext*)((AVHWFramesContext*)_frames->data)->hwctx)->Format[0];

        /// <summary>The first image's semaphore value and layout.</summary>
        public (ulong Value, int Layout) FrameState()
        {
            var frame = (AVVkFrame*)_hardware->data[0];
            return (frame->SemaphoreValue[0], frame->Layout[0]);
        }

        public VulkanFramePlan Plan(VideoColorSpace colorSpace, int width, int height, long revision)
        {
            var device = VulkanOffscreenCompositor.CreateDeviceResource((nint)_device)!;
            var resource = VulkanOffscreenCompositor.CreateFrameResource(device, (nint)_hardware)! with { ColorSpace = colorSpace };
            var blit = new VulkanBlit(new PixelRect(0, 0, width, height), new PixelRect(0, 0, width, height));
            var layer = new VulkanBackgroundLayer(ReadOnlyMemory<byte>.Empty, width, height, width * 4, blit, revision, Native: resource);
            return new VulkanFramePlan(width, height, (0, 0, 0, 255), layer, null, null);
        }

        public byte[] Render(VideoColorSpace colorSpace, HdrToneMapRequest? toneMap, int width, int height, long revision)
        {
            var device = VulkanOffscreenCompositor.CreateDeviceResource((nint)_device)!;
            var resource = VulkanOffscreenCompositor.CreateFrameResource(device, (nint)_hardware)! with
            {
                ColorSpace = colorSpace,
                HdrToneMap = toneMap,
            };
            Assert.True(Compositor.IsOnThisDevice(resource));
            var blit = new VulkanBlit(new PixelRect(0, 0, width, height), new PixelRect(0, 0, width, height));
            var layer = new VulkanBackgroundLayer(ReadOnlyMemory<byte>.Empty, width, height, width * 4, blit, revision, Native: resource);
            var plan = new VulkanFramePlan(width, height, (0, 0, 0, 255), layer, null, null);
            return Compositor.Render(plan).ToArray();
        }

        public byte[] RenderRetained(int width, int height)
        {
            var blit = new VulkanBlit(new PixelRect(0, 0, width, height), new PixelRect(0, 0, width, height));
            var layer = new VulkanBackgroundLayer(ReadOnlyMemory<byte>.Empty, width, height, width * 4, blit, 8, Retained: true);
            return Compositor.Render(new VulkanFramePlan(width, height, (0, 0, 0, 255), layer, null, null)).ToArray();
        }

        /// <summary>The software path's conversion (swscale, limited-range source, full-range RGB), at the full size.</summary>
        public byte[] Swscale(AVPixelFormat target, int colorspace)
        {
            // As Core's VideoDecoder converts software frames.
            var scaler = ffmpeg.sws_getContext(Width, Height, _format, Width, Height, target, (int)SwsFlags.SWS_BILINEAR, null, null, null);
            Assert.True(scaler != null);
            try
            {
                var coefficients = ffmpeg.sws_getCoefficients(colorspace);
                var table = new int_array4();
                for (uint i = 0; i < 4; i++)
                    table[i] = coefficients[i];
                _ = ffmpeg.sws_setColorspaceDetails(scaler, in table, 0, in table, 1, 0, 1 << 16, 1 << 16);
                var output = new byte[Width * Height * 4];
                fixed (byte* pOutput = output)
                {
                    var data = new byte_ptrArray4 { [0] = pOutput };
                    var lines = new int_array4 { [0] = Width * 4 };
                    var source = new byte_ptrArray4 { [0] = _software->data[0], [1] = _software->data[1] };
                    var sourceLines = new int_array4 { [0] = _software->linesize[0], [1] = _software->linesize[1] };
                    _ = ffmpeg.sws_scale(scaler, source, sourceLines, 0, Height, data, lines);
                }

                return output;
            }
            finally
            {
                ffmpeg.sws_freeContext(scaler);
            }
        }

        public void Dispose()
        {
            fixed (AVFrame** hardware = &_hardware)
                ffmpeg.av_frame_free(hardware);
            fixed (AVFrame** software = &_software)
                ffmpeg.av_frame_free(software);
            fixed (AVBufferRef** frames = &_frames)
                ffmpeg.av_buffer_unref(frames);
            fixed (AVBufferRef** device = &_device)
                ffmpeg.av_buffer_unref(device);
            Compositor.Dispose();
        }

        static void Check(int result, string operation) => Assert.True(result >= 0, $"{operation} failed: {result}");
    }

    /// <summary>
    /// The HDR-to-SDR math the shader ports from the Direct3D11 one and Core's CPU tone mapper
    /// (Stages 038A, 042A), in double precision: BT.2020 limited-range 10-bit codes to R'G'B',
    /// PQ or HLG (with the BT.2100 OOTF) to nits, the BT.2390 curve on the brightest channel,
    /// BT.2020 to BT.709 primaries, gamma 2.4.
    /// </summary>
    static class ToneMapReference
    {
        const double M1 = 2610.0 / 16384;
        const double M2 = 2523.0 / 4096 * 128;
        const double C1 = 3424.0 / 4096;
        const double C2 = 2413.0 / 4096 * 32;
        const double C3 = 2392.0 / 4096 * 32;

        public static (byte R, byte G, byte B) ToSdr(int luma, int cb, int cr, HdrToneMapRequest request)
        {
            const double kr = 0.2627;
            const double kb = 0.0593;
            const double kg = 1 - kr - kb;
            var y = (luma - 64) / 876.0;
            var u = (cb - 512) / 896.0;
            var v = (cr - 512) / 896.0;
            double[] signal = [y + (2 * (1 - kr) * v), y - (2 * kb * (1 - kb) / kg * u) - (2 * kr * (1 - kr) / kg * v), y + (2 * (1 - kb) * u)];
            var nits = request.Transfer == HdrTransferFunction.Hlg ? HlgNits(signal, request) : [.. signal.Select(PqToNits)];
            var brightest = nits.Max();
            var gain = brightest > 0 ? Curve(brightest, request) / brightest : 1;
            double[] linear = [.. nits.Select(n => n * gain / request.TargetPeakNits)];
            double[] bt709 =
            [
                (1.660491 * linear[0]) - (0.587641 * linear[1]) - (0.072850 * linear[2]),
                (-0.124550 * linear[0]) + (1.132900 * linear[1]) - (0.008349 * linear[2]),
                (-0.018151 * linear[0]) - (0.100579 * linear[1]) + (1.118730 * linear[2]),
            ];
            byte Encode(double value) => (byte)Math.Round(Math.Pow(Math.Clamp(value, 0, 1), 1 / HdrToneMapRequest.SdrGamma) * 255);
            return (Encode(bt709[0]), Encode(bt709[1]), Encode(bt709[2]));
        }

        static double PqToNits(double signal)
        {
            var e = Math.Pow(Math.Clamp(signal, 0, 1), 1 / M2);
            return 10000 * Math.Pow(Math.Max(e - C1, 0) / (C2 - (C3 * e)), 1 / M1);
        }

        static double NitsToPq(double nits)
        {
            var y = Math.Pow(Math.Clamp(nits / 10000, 0, 1), M1);
            return Math.Pow((C1 + (C2 * y)) / (1 + (C3 * y)), M2);
        }

        static double[] HlgNits(double[] signal, HdrToneMapRequest request)
        {
            static double Scene(double s) => s <= 0.5 ? s * s / 3 : (Math.Exp((s - 0.55991073) / 0.17883277) + 0.28466892) / 12;
            double[] scene = [.. signal.Select(s => Scene(Math.Clamp(s, 0, 1)))];
            var luminance = (0.2627 * scene[0]) + (0.6780 * scene[1]) + (0.0593 * scene[2]);
            var ootf = luminance > 0 ? request.SourcePeakNits * Math.Pow(luminance, request.HlgSystemGamma - 1) : 0;
            return [.. scene.Select(s => s * ootf)];
        }

        static double Curve(double nits, HdrToneMapRequest request)
        {
            if (request.SourcePeakNits <= request.TargetPeakNits)
                return Math.Min(nits, request.TargetPeakNits);

            var sourcePeak = request.SourcePeakSignal;
            var maxLum = request.TargetPeakSignal / sourcePeak;
            var knee = Math.Max((1.5 * maxLum) - 0.5, 0);
            var e1 = Math.Min(NitsToPq(nits) / sourcePeak, 1);
            var e2 = e1;
            if (e1 > knee)
            {
                var t = (e1 - knee) / (1 - knee);
                e2 = (((2 * t * t * t) - (3 * t * t) + 1) * knee) + (((t * t * t) - (2 * t * t) + t) * (1 - knee)) + (((-2 * t * t * t) + (3 * t * t)) * maxLum);
            }

            return Math.Min(PqToNits(e2 * sourcePeak), request.TargetPeakNits);
        }
    }
}

/// <summary>Loads FFmpeg from <c>FATZBOMB_LINUX_FFMPEG_DIR</c> for the tests that need it.</summary>
static class FfmpegForTests
{
    static readonly Lock Gate = new();
    static bool? _loaded;

    public static bool TryLoad()
    {
        lock (Gate)
        {
            if (_loaded is { } loaded)
                return loaded;

            var directory = Environment.GetEnvironmentVariable("FATZBOMB_LINUX_FFMPEG_DIR");
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory) || OperatingSystem.IsWindows())
            {
                _loaded = false;
                return false;
            }

            ffmpeg.RootPath = directory;
            _loaded = ffmpeg.avutil_version() > 0;
            return _loaded.Value;
        }
    }
}
