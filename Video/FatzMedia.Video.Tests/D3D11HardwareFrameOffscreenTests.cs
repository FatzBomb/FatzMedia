using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using FatzMedia.Video.Core.Abstractions;
using FatzMedia.Video.Core.Configuration;
using FatzMedia.Video.Core.Graphics;
using FatzMedia.Video.Core.Graphics.D3D.Device;
using FatzMedia.Video.Core.Graphics.D3D.Presentation.Shared;
using FatzMedia.Video.Direct3D11;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Xunit.Abstractions;

namespace FatzMedia.Video.Tests;

/// <summary>
/// Stage 045A slice 1: hardware-decoded video on the offscreen targets the Avalonia surface uses.
/// A frame on the decoder's device is adopted the way Core does it (the renderer moves to that
/// device and every surface is attached again), then drawn on the GPU into the exported keyed-mutex
/// textures, which a third device opens like a compositor, or read back into a frame sink. WARP
/// has no video decoder, so a texture array on a second WARP device stands in for FFmpeg's D3D11VA
/// pool (the frame is its second slice). They need WARP's video processor and NV12/P010 support,
/// which the GitHub Windows runners' WARP does not offer: there the renderer cannot take hardware
/// frames at all, the tests stop after their setup, and <see cref="Warp_CanStandInForADecoder"/>
/// writes which capability is missing to the test output. Real D3D11VA frames are a manual smoke
/// check (docs/roadmap/updates/045A_1).
/// </summary>
public sealed class D3D11HardwareFrameOffscreenTests(ITestOutputHelper output)
{
    const int Width = 16;
    const int Height = 8;

    static readonly VideoColorSpace Bt709 = new(VideoColorMatrix.Bt709, VideoColorRange.Limited);
    static readonly VideoColorSpace Bt2020 = new(VideoColorMatrix.Bt2020, VideoColorRange.Limited);

    // Limited-range BT.709 YCbCr of pure red.
    const byte RedY = 63;
    const byte RedCb = 102;
    const byte RedCr = 240;

    [Fact]
    public void Warp_CanStandInForADecoder()
    {
        // Says which tests below actually ran on this machine: without these capabilities they stop
        // after their setup (a machine limit, not a renderer failure; see the class summary).
        using var decoder = CreateWarpDevice();
        output.WriteLine(decoder.HasVideoProcessorSupport
            ? $"WARP video processor: yes; NV12 input: {CanDecodeInto(decoder, Format.NV12)}; P010 sampling: {CanDecodeInto(decoder, Format.P010)}."
            : "WARP offers no Direct3D11 video processor on this machine: the hardware-frame tests in this class stop after their setup.");
        Assert.False(D3D11DeviceResources.IsDeviceRemoved(decoder.Device!.NativePointer));
        Assert.False(D3D11DeviceResources.IsDeviceRemoved(IntPtr.Zero));
    }

    [Fact]
    public void AHardwareFrame_OnTheDecoderDevice_IsComposedIntoTheSharedTextures()
    {
        using var bridge = D3D11MultiTargetTests.CreateWarpBridge();
        using var compositor = CreateWarpDevice();
        using var consumer = new ImportingConsumer(compositor);
        using var handle = new MediaSharedGpuImageConsumerHandle(consumer);
        var primary = Primary(handle.CreateSurfaceDescriptor());
        Assert.True(bridge.AttachSurface(primary), string.Join("; ", consumer.Unavailable));
        var onOwnDevice = Assert.Single(consumer.Sets);

        using var decoder = CreateWarpDevice();
        if (!CanDecodeInto(decoder, Format.NV12))
            return;

        using var pool = CreatePoolSlice(decoder, Format.NV12, Nv12(RedY, RedCb, RedCr));
        var frame = HardwareFrame(decoder, pool, Bt709, toneMap: null);
        AdoptAndReattach(bridge, frame, decoder, primary);

        // The set exported on the renderer's own device is retired; the new one lives on the decoder's.
        Assert.True(onOwnDevice.IsRetired);
        var onDecoderDevice = consumer.Sets.Last();
        Assert.NotEqual(onOwnDevice.Generation, onDecoderDevice.Generation);
        Assert.Empty(consumer.Unavailable);

        Assert.True(bridge.PresentNativeFrame(frame));
        (var shown, var pixels) = consumer.LastFrame;
        Assert.Equal(onDecoderDevice.Generation, shown.Generation);
        AssertRed(pixels);
    }

    [Fact]
    public void AHardwareFrame_OnTheDecoderDevice_IsReadBackIntoTheFrameSink()
    {
        using var bridge = D3D11MultiTargetTests.CreateWarpBridge();
        var sink = new D3D11MultiTargetTests.CopyingFrameSink();
        using var handle = new MediaPresentationFrameSinkHandle(sink);
        var primary = Primary(handle.CreateSurfaceDescriptor());
        Assert.True(bridge.AttachSurface(primary));

        using var decoder = CreateWarpDevice();
        if (!CanDecodeInto(decoder, Format.NV12))
            return;

        using var pool = CreatePoolSlice(decoder, Format.NV12, Nv12(RedY, RedCb, RedCr));
        var frame = HardwareFrame(decoder, pool, Bt709, toneMap: null);
        AdoptAndReattach(bridge, frame, decoder, primary);

        var frames = sink.Frames;
        Assert.True(bridge.PresentNativeFrame(frame));
        Assert.Equal(frames + 1, sink.Frames);
        Assert.Equal((Width, Height), (sink.Width, sink.Height));
        AssertRed(sink.Pixels);
    }

    [Fact]
    public void AnHdr10HardwareFrame_IsToneMappedOnTheGpu_IntoTheSharedTextures()
    {
        // HDR tone mapping (Stages 038A, 042A) on the shared-texture path: the shader writes SDR
        // into the private target, and the compositor's copy shows the same values as the CPU mapper.
        using var bridge = D3D11MultiTargetTests.CreateWarpBridge();
        using var compositor = CreateWarpDevice();
        using var consumer = new ImportingConsumer(compositor);
        using var handle = new MediaSharedGpuImageConsumerHandle(consumer);
        var primary = Primary(handle.CreateSurfaceDescriptor());
        Assert.True(bridge.AttachSurface(primary), string.Join("; ", consumer.Unavailable));

        using var decoder = CreateWarpDevice();
        if (!CanDecodeInto(decoder, Format.P010))
            return;

        const int Y = 600, Cb = 400, Cr = 700;
        var fallback = new HdrToneMapFallback();
        var request = new HdrToneMapRequest(1000, 203) { Fallback = fallback };
        using var pool = CreatePoolSlice(decoder, Format.P010, P010(Y, Cb, Cr));
        var frame = HardwareFrame(decoder, pool, Bt2020, request);
        AdoptAndReattach(bridge, frame, decoder, primary);

        Assert.True(bridge.PresentNativeFrame(frame));
        Assert.False(fallback.IsRequested, fallback.Reason);
        // What the engine's CPU path produces for the same YCbCr (R, G, B), against BGRA pixels.
        var rgb = D3D11HdrToneMapTests.Cpu(Y, Cb, Cr, request);
        var pixels = consumer.LastFrame.Pixels;
        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            for (var channel = 0; channel < 3; channel++)
                Assert.InRange(pixels[offset + 2 - channel], rgb[channel] - 3, rgb[channel] + 3);
        }
    }

    // --- Helpers ---------------------------------------------------------------------------

    /// <summary>What Core does with a hardware frame from a new decoder device (MediaPresentationCoordinator.EnsureNativeFrameDevice).</summary>
    static void AdoptAndReattach(NativeD3D11PresentationBridge bridge, VideoFrameEventArgs frame, D3D11DeviceResources decoder, MediaPresentationSurfaceDescriptor primary)
    {
        Assert.True(bridge.EnsureNativeFrameDevice(frame, out var adopted));
        Assert.True(adopted);
        Assert.Equal(decoder.Device!.NativePointer, bridge.NativeDeviceHandle);
        Assert.True(bridge.AttachSurface(primary));

        // The same device again: nothing to adopt.
        Assert.True(bridge.EnsureNativeFrameDevice(frame, out adopted));
        Assert.False(adopted);
    }

    /// <summary>
    /// Whether this WARP device can stand in for a decoder of <paramref name="format"/>: the
    /// renderer needs the video processor on the adopted device and the format as a texture; NV12
    /// goes through the video processor, P010 through the tone-map shader, which samples it.
    /// </summary>
    static bool CanDecodeInto(D3D11DeviceResources decoder, Format format)
    {
        if (!decoder.HasVideoProcessorSupport)
            return false;

        var support = decoder.Device!.CheckFormatSupport(format);
        var needed = FormatSupport.Texture2D | (format == Format.P010 ? FormatSupport.ShaderSample : FormatSupport.VideoProcessorInput);
        return (support & needed) == needed;
    }

    /// <summary>A two-slice texture array like a decoder's pool, with <paramref name="planes"/> in slice 1.</summary>
    static ID3D11Texture2D CreatePoolSlice(D3D11DeviceResources decoder, Format format, byte[] planes)
    {
        var device = decoder.Device!;
        var context = decoder.Context!;
        ID3D11Texture2D pool;
        try
        {
            pool = device.CreateTexture2D(new Texture2DDescription(format, Width, Height, 2, 1, BindFlags.ShaderResource));
        }
        catch (SharpGen.Runtime.SharpGenException)
        {
            // Not shader-readable on this device; the renderer copies the slice before sampling anyway.
            pool = device.CreateTexture2D(new Texture2DDescription(format, Width, Height, 2, 1, BindFlags.None));
        }

        // Planar data goes through a staging texture, as FFmpeg's D3D11VA upload does: the luma
        // rows, then the interleaved chroma rows, at one pitch.
        using var staging = device.CreateTexture2D(new Texture2DDescription(
            format, Width, Height, 1, 1, BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Write));
        var mapped = context.Map(staging, 0, MapMode.Write, Vortice.Direct3D11.MapFlags.None);
        try
        {
            var rowBytes = planes.Length / (Height * 3 / 2);
            for (var row = 0; row < Height * 3 / 2; row++)
                Marshal.Copy(planes, row * rowBytes, mapped.DataPointer + (row * (nint)mapped.RowPitch), rowBytes);
        }
        finally
        {
            context.Unmap(staging, 0);
        }

        context.CopySubresourceRegion(pool, 1, 0, 0, 0, staging, 0);
        return pool;
    }

    static byte[] Nv12(byte y, byte cb, byte cr)
    {
        var planes = new byte[Width * Height * 3 / 2];
        planes.AsSpan(0, Width * Height).Fill(y);
        for (var i = Width * Height; i < planes.Length; i += 2)
            (planes[i], planes[i + 1]) = (cb, cr);
        return planes;
    }

    // P010: 10-bit codes in the high bits of little-endian 16-bit samples.
    static byte[] P010(int y, int cb, int cr)
    {
        var samples = new ushort[Width * Height * 3 / 2];
        samples.AsSpan(0, Width * Height).Fill((ushort)(y << 6));
        for (var i = Width * Height; i < samples.Length; i += 2)
            (samples[i], samples[i + 1]) = ((ushort)(cb << 6), (ushort)(cr << 6));
        return MemoryMarshal.AsBytes(samples.AsSpan()).ToArray();
    }

    static VideoFrameEventArgs HardwareFrame(D3D11DeviceResources decoder, ID3D11Texture2D pool, VideoColorSpace colorSpace, HdrToneMapRequest? toneMap) => new()
    {
        Width = Width,
        Height = Height,
        Stride = 0,
        PtsSeconds = 0,
        FramesPerSecond = 30,
        NativeResource = new NativeVideoFrameResource(
            new NativeVideoDeviceResource(MediaGraphicsBackendId.Direct3D11, decoder.Device!.NativePointer, decoder.Context!.NativePointer),
            pool.NativePointer,
            SubresourceIndex: 1)
        {
            ColorSpace = colorSpace,
            HdrToneMap = toneMap
        }
    };

    static void AssertRed(byte[] bgra)
    {
        Assert.Equal(Width * Height * 4, bgra.Length);
        for (var offset = 0; offset < bgra.Length; offset += 4)
        {
            Assert.InRange(bgra[offset + 2], 200, 255); // red
            Assert.InRange(bgra[offset + 1], 0, 60);    // green
            Assert.InRange(bgra[offset], 0, 60);        // blue
        }
    }

    static D3D11DeviceResources CreateWarpDevice()
    {
        var device = new D3D11DeviceResources(Direct3D11DeviceSelection.Warp);
        Assert.True(device.Initialize(new MediaPlayerConfiguration()));
        return device;
    }

    static MediaPresentationSurfaceDescriptor Primary(MediaNativeSurfaceDescriptor native) =>
        new(MediaPresentationSurfaceDescriptor.PrimarySurfaceId, MediaPresentationSurfaceRole.Primary, native, Width, Height);

    /// <summary>
    /// Opens every exported texture on its own device, like Avalonia's compositor, and reads each
    /// announced frame under its keyed mutex (acquire the consumer key, release the producer key).
    /// </summary>
    sealed class ImportingConsumer(D3D11DeviceResources device) : IMediaSharedGpuImageConsumer, IDisposable
    {
        readonly Dictionary<long, (ID3D11Texture2D Texture, IDXGIKeyedMutex Mutex)[]> _imports = [];
        readonly Dictionary<long, MediaSharedGpuImageSet> _sets = [];

        public ConcurrentQueue<MediaSharedGpuImageSet> Sets { get; } = new();
        public ConcurrentQueue<string> Unavailable { get; } = new();
        public (MediaSharedGpuImageFrame Frame, byte[] Pixels) LastFrame { get; private set; }

        public MediaSharedGpuImageRequest Request { get; } = new(
        [
            new MediaSharedGpuImageCapability(MediaSharedGpuImageHandleKind.D3D11TextureNtHandle, MediaSharedGpuImageSyncKinds.KeyedMutex),
            new MediaSharedGpuImageCapability(MediaSharedGpuImageHandleKind.D3D11TextureGlobalSharedHandle, MediaSharedGpuImageSyncKinds.KeyedMutex)
        ]);

        public void OnImagesCreated(MediaSharedGpuImageSet images)
        {
            var d3d = device.Device!;
            var imports = new (ID3D11Texture2D, IDXGIKeyedMutex)[images.Buffers.Count];
            for (var i = 0; i < imports.Length; i++)
            {
                var shared = images.Buffers[i].ImageHandle;
                ID3D11Texture2D texture;
                if (images.HandleKind == MediaSharedGpuImageHandleKind.D3D11TextureNtHandle)
                {
                    using var d3d1 = d3d.QueryInterface<ID3D11Device1>();
                    texture = d3d1.OpenSharedResource1<ID3D11Texture2D>(shared);
                }
                else
                {
                    texture = d3d.OpenSharedResource<ID3D11Texture2D>(shared);
                }

                imports[i] = (texture, texture.QueryInterface<IDXGIKeyedMutex>());
            }

            _imports[images.Generation] = imports;
            _sets[images.Generation] = images;
            images.CompleteImport(handlesConsumed: true);
            Sets.Enqueue(images);
        }

        public void OnFrameReady(MediaSharedGpuImageFrame frame)
        {
            (var texture, var mutex) = _imports[frame.Generation][frame.BufferIndex];
            var context = device.Context!;
            using var staging = device.Device!.CreateTexture2D(new Texture2DDescription(
                Format.B8G8R8A8_UNorm, Width, Height, 1, 1, BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read));
            // Through the vtable: Vortice's wrapper hides WAIT_TIMEOUT and WAIT_ABANDONED.
            Assert.Equal(0, D3D11SharedTextureBackend.AcquireSync(mutex, frame.AcquireKey, 1000));
            context.CopyResource(staging, texture);
            mutex.ReleaseSync(frame.ReleaseKey);

            var pixels = new byte[Width * Height * 4];
            var mapped = context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try
            {
                for (var row = 0; row < Height; row++)
                    Marshal.Copy(mapped.DataPointer + (row * (nint)mapped.RowPitch), pixels, row * Width * 4, Width * 4);
            }
            finally
            {
                context.Unmap(staging, 0);
            }

            LastFrame = (frame, pixels);
            _sets[frame.Generation].ReleaseFrame(frame, consumed: true);
        }

        public void OnUnavailable(string reason) => Unavailable.Enqueue(reason);

        public void Dispose()
        {
            foreach ((var texture, var mutex) in _imports.Values.SelectMany(static imports => imports))
            {
                mutex.Dispose();
                texture.Dispose();
            }

            _imports.Clear();
        }
    }
}
