using System.Collections.Concurrent;
using FatzBomb.MediaControl.Controllers;
using FatzBomb.MediaControl.Core.Abstractions;
using FatzBomb.MediaControl.Core.Configuration;
using FatzBomb.MediaControl.Core.Graphics;
using FatzBomb.MediaControl.Core.Graphics.Composition;
using FatzBomb.MediaControl.Core.Subtitle;
using FatzBomb.MediaControl.Core.Threading;
using FatzBomb.MediaControl.Core.Video;
using FatzBomb.MediaControl.Rendering.Software;
using Microsoft.Extensions.DependencyInjection;

namespace FatzBomb.MediaControl.Tests;

/// <summary>
/// Stage 045A slice 2: a renderer that decodes on its own device supplies every decoder's hardware
/// device (the Vulkan renderer with FFmpeg Vulkan Video); other renderers leave the platform's.
/// </summary>
[Collection(MediaPresentationCoordinatorCollection.Name)]
public sealed class RendererVideoDecodeServicesTests
{
    [Fact]
    public void WithoutARendererDevice_ThePlatformDecides()
    {
        var platform = new RecordingPlatformServices();
        var services = new RendererVideoDecodeServices(platform);

        Assert.Equal(["d3d11va"], services.PreferredDeviceTypeNames);
        Assert.False(services.TryCreateRendererDeviceContext("d3d11va", out _));
        Assert.True(services.IsPreferredPixelFormat("d3d11va", 7));
        Assert.True(services.IsDeviceLost(new NativeVideoDeviceResource(MediaGraphicsBackendId.Direct3D11, 1)));
    }

    [Fact]
    public void ARendererDevice_ReplacesThePlatformsDeviceTypes()
    {
        var platform = new RecordingPlatformServices();
        var services = new RendererVideoDecodeServices(platform);
        var renderer = new FakeRendererDevice("vulkan") { Context = 42, Lost = true };
        services.Attach(renderer);

        // Only the renderer's device type: D3D11VA frames would be on a device it cannot draw.
        Assert.Equal(["vulkan"], services.PreferredDeviceTypeNames);
        Assert.True(services.IsPreferredPixelFormat("vulkan", 1));
        Assert.True(services.TryCreateRendererDeviceContext("vulkan", out var context));
        Assert.Equal(42, context);
        Assert.False(services.TryCreateRendererDeviceContext("d3d11va", out _));

        NativeVideoDeviceResource device = services.CreateDeviceResource("vulkan", context)!;
        Assert.Equal(MediaGraphicsBackendId.Vulkan, device.BackendId);
        Assert.Same(renderer.FrameResource, services.CreateFrameResource("vulkan", device, 7));
        Assert.True(services.IsDeviceLost(device));
        Assert.Equal(0, platform.LostQueries);

        // Detaching another renderer leaves this one; detaching it returns to the platform.
        services.Detach(new FakeRendererDevice("vulkan"));
        Assert.Same(renderer, services.Renderer);
        services.Detach(renderer);
        Assert.Equal(["d3d11va"], services.PreferredDeviceTypeNames);
    }

    [Fact]
    public void ARendererThatCannotDecode_GetsSoftwareFrames_AndSaysWhyOnce()
    {
        var services = new RendererVideoDecodeServices(new RecordingPlatformServices());
        services.Attach(new FakeRendererDevice(null) { Reason = "the device has no video decode queue" });

        Assert.Empty(services.PreferredDeviceTypeNames);
        IReadOnlyList<MediaDiagnosticInfo> diagnostics = Capture(() =>
        {
            services.NoteHardwareDecodingRequested();
            services.NoteHardwareDecodingRequested();
        });

        MediaDiagnosticInfo diagnostic = Assert.Single(diagnostics, static info => info.Category == "VideoDecoder");
        Assert.Equal(MediaDiagnosticLevel.Warning, diagnostic.Level);
        Assert.Contains("(the device has no video decode queue); decoding in software", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AFailedDeviceContext_IsReportedOncePerReason()
    {
        var services = new RendererVideoDecodeServices(new RecordingPlatformServices());
        services.Attach(new FakeRendererDevice("vulkan") { Reason = "FFmpeg refused the renderer's Vulkan device" });

        IReadOnlyList<MediaDiagnosticInfo> diagnostics = Capture(() =>
        {
            Assert.True(services.TryCreateRendererDeviceContext("vulkan", out var first));
            Assert.True(services.TryCreateRendererDeviceContext("vulkan", out var second));
            Assert.Equal((0, 0), (first, second));
        });

        Assert.Single(diagnostics, static info => info.Category == "VideoDecoder" && info.Message.Contains("FFmpeg refused", StringComparison.Ordinal));
    }

    [Fact]
    public void TheCoordinator_AttachesItsRenderersDevice_ToThePlayersDecoders()
    {
        using ServiceProvider provider = (ServiceProvider)MediaControlServiceProvider.Create(MediaPlayerConfiguration.Auto, [], new TestPlatformServices());
        IMediaEngine engine = provider.GetRequiredService<IMediaEngine>();
        RendererVideoDecodeServices services = provider.GetRequiredService<RendererVideoDecodeServices>();

        var decoding = new DecodingRendererBackend();
        MediaPresentationCoordinator coordinator = Create(decoding, engine);
        Assert.Same(decoding, services.Renderer);
        coordinator.Dispose();
        Assert.Null(services.Renderer);

        // A renderer that takes the platform's frames replaces any earlier renderer device.
        services.Attach(new FakeRendererDevice("vulkan"));
        using MediaPresentationCoordinator plain = Create(new DecodingRendererBackend.Plain(), engine);
        Assert.Null(services.Renderer);
    }

    [Fact]
    public void TheSoftwareRenderer_OffersNoHardwareDevice_AndSaysWhyOnce()
    {
        // ENG-037: the software renderer draws only CPU frames, so with the default (Auto)
        // decoding its players must decode in software instead of dropping hardware frames.
        using ServiceProvider provider = (ServiceProvider)MediaControlServiceProvider.Create(MediaPlayerConfiguration.Auto, [], new TestPlatformServices());
        IMediaEngine engine = provider.GetRequiredService<IMediaEngine>();
        RendererVideoDecodeServices services = provider.GetRequiredService<RendererVideoDecodeServices>();
        var software = new SoftwareRendererBackend();
        using MediaPresentationCoordinator coordinator = Create(software, engine);

        Assert.Same(software, services.Renderer);
        Assert.Empty(services.PreferredDeviceTypeNames);
        Assert.False(services.TryCreateRendererDeviceContext("d3d11va", out _));
        IReadOnlyList<MediaDiagnosticInfo> diagnostics = Capture(() =>
        {
            services.NoteHardwareDecodingRequested();
            services.NoteHardwareDecodingRequested();
        });

        MediaDiagnosticInfo diagnostic = Assert.Single(diagnostics, static info => info.Category == "VideoDecoder");
        Assert.Contains(SoftwareRendererBackend.NoVideoDecodeReason, diagnostic.Message, StringComparison.Ordinal);
    }

    private static MediaPresentationCoordinator Create(IMediaRendererBackend backend, IMediaEngine engine) =>
        new(backend, NullSubtitleRasterizer.Instance, engine, MediaPlayerConfiguration.Auto, new SystemPrecisionWaiterFactory());

    private static List<MediaDiagnosticInfo> Capture(Action action)
    {
        var captured = new ConcurrentQueue<MediaDiagnosticInfo>();
        void Record(MediaDiagnosticInfo info) => captured.Enqueue(info);
        DiagnosticRouter.DiagnosticOccurred += Record;
        try
        {
            action();
        }
        finally
        {
            DiagnosticRouter.DiagnosticOccurred -= Record;
        }

        return [.. captured];
    }

    private sealed class RecordingPlatformServices : IVideoHardwareAccelerationServices
    {
        public int LostQueries { get; private set; }

        public IReadOnlyList<string> PreferredDeviceTypeNames => ["d3d11va"];

        public bool IsPreferredPixelFormat(string deviceTypeName, int pixelFormat) => deviceTypeName == "d3d11va";

        public NativeVideoDeviceResource? CreateDeviceResource(string deviceTypeName, nint ffmpegHardwareDeviceContext) => null;

        public NativeVideoFrameResource? CreateFrameResource(string deviceTypeName, NativeVideoDeviceResource device, nint ffmpegFrame) => null;

        public bool IsDeviceLost(NativeVideoDeviceResource device)
        {
            LostQueries++;
            return true;
        }
    }

    private sealed class FakeRendererDevice(string? type) : IMediaRendererVideoDecodeDevice
    {
        public nint Context { get; init; }
        public bool Lost { get; init; }
        public string? Reason { get; init; }
        public NativeVideoFrameResource FrameResource { get; } = new(new NativeVideoDeviceResource(MediaGraphicsBackendId.Vulkan, 1), 7, 0);

        public string? VideoDecodeDeviceType => type;

        public string? VideoDecodeUnavailableReason => type is null ? Reason : null;

        public nint CreateVideoDecodeDeviceContext(out string? failureReason)
        {
            failureReason = Context == 0 ? Reason : null;
            return Context;
        }

        public NativeVideoDeviceResource? CreateVideoDecodeDeviceResource(nint ffmpegHardwareDeviceContext) =>
            new(MediaGraphicsBackendId.Vulkan, 1);

        public NativeVideoFrameResource? CreateVideoDecodeFrameResource(NativeVideoDeviceResource device, nint ffmpegFrame) => FrameResource;

        public bool IsVideoDecodeDeviceLost(NativeVideoDeviceResource device) => Lost;
    }

    private class DecodingRendererBackend : IMediaRendererBackend, IMediaRendererVideoDecodeDevice
    {
        public event EventHandler? FramePresented
        {
            add { }
            remove { }
        }

        public string BackendName => "Test";
        public string DeviceLostDiagnosticCategory => "Test.DeviceLost";
        public string DeviceLostRecoveryFailedDiagnosticCategory => "Test.DeviceLost.RecoveryFailed";
        public bool IsInitialized => true;
        public nint NativeDeviceHandle => 0;
        public string? VideoDecodeDeviceType => "vulkan";
        public string? VideoDecodeUnavailableReason => null;

        public bool Initialize() => true;
        public bool AttachSurface(MediaPresentationSurfaceDescriptor surface) => true;
        public bool ResizeSurface(MediaPresentationSurfaceDescriptor surface) => true;
        public bool SetSurfaceLiveResize(MediaPresentationSurfaceDescriptor surface, bool isResizing) => true;
        public bool DetachSurface(MediaPresentationSurfaceDescriptor surface) => true;
        public void SetPresentationStretch(MediaCompositionStretchMode stretchMode) { }
        public bool PresentNativeFrame(VideoFrameEventArgs frame) => false;
        public void ClearCompositionBackgroundFrame() { }
        public bool PresentComposition(MediaCompositionFrame frame) => true;
        public void CleanupResources() { }

        public bool EnsureNativeFrameDevice(VideoFrameEventArgs frame, out bool deviceAdopted)
        {
            deviceAdopted = false;
            return false;
        }

        public nint CreateVideoDecodeDeviceContext(out string? failureReason)
        {
            failureReason = "test";
            return 0;
        }

        public NativeVideoDeviceResource? CreateVideoDecodeDeviceResource(nint ffmpegHardwareDeviceContext) => null;
        public NativeVideoFrameResource? CreateVideoDecodeFrameResource(NativeVideoDeviceResource device, nint ffmpegFrame) => null;
        public bool IsVideoDecodeDeviceLost(NativeVideoDeviceResource device) => false;
        public void Dispose() { }

        /// <summary>The same backend without a decode device (as Direct3D11 is).</summary>
        public sealed class Plain : IMediaRendererBackend
        {
            private readonly DecodingRendererBackend _inner = new();

            public event EventHandler? FramePresented
            {
                add { }
                remove { }
            }

            public string BackendName => _inner.BackendName;
            public string DeviceLostDiagnosticCategory => _inner.DeviceLostDiagnosticCategory;
            public string DeviceLostRecoveryFailedDiagnosticCategory => _inner.DeviceLostRecoveryFailedDiagnosticCategory;
            public bool IsInitialized => true;
            public nint NativeDeviceHandle => 0;
            public bool Initialize() => true;
            public bool AttachSurface(MediaPresentationSurfaceDescriptor surface) => true;
            public bool ResizeSurface(MediaPresentationSurfaceDescriptor surface) => true;
            public bool SetSurfaceLiveResize(MediaPresentationSurfaceDescriptor surface, bool isResizing) => true;
            public bool DetachSurface(MediaPresentationSurfaceDescriptor surface) => true;
            public void SetPresentationStretch(MediaCompositionStretchMode stretchMode) { }
            public bool PresentNativeFrame(VideoFrameEventArgs frame) => false;
            public void ClearCompositionBackgroundFrame() { }
            public bool PresentComposition(MediaCompositionFrame frame) => true;
            public void CleanupResources() { }

            public bool EnsureNativeFrameDevice(VideoFrameEventArgs frame, out bool deviceAdopted)
            {
                deviceAdopted = false;
                return false;
            }

            public void Dispose() { }
        }
    }


    /// <summary>A platform with no native libraries: no audio, software decoding services.</summary>
    private sealed class TestPlatformServices : IMediaControlPlatformServices
    {
        public bool IsAudioAvailable => false;

        public void EnsureNativeLibrariesLoaded(IReadOnlyList<string> nativeLibraryPaths)
        {
        }

        public void AddPlatformServices(IServiceCollection services, MediaPlayerConfiguration configuration)
        {
            _ = services.AddSingleton<IPrecisionWaiterFactory>(new SystemPrecisionWaiterFactory());
            _ = services.AddSingleton<IVideoPipelinePlatformServices>(new FixedRefresh());
            _ = services.AddSingleton<IVideoHardwareAccelerationServices>(SoftwareOnlyVideoHardwareAccelerationServices.Instance);
        }
    }

    private sealed class FixedRefresh : IVideoPipelinePlatformServices
    {
        public double GetPresentationRefreshIntervalSeconds() => 1.0 / 60;
    }
}
