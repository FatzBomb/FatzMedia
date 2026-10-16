using FatzBomb.MediaControl.Controllers;
using FatzBomb.MediaControl.Core.Abstractions;
using FatzBomb.MediaControl.Core.Configuration;
using FatzBomb.MediaControl.Core.Graphics.Composition;
using FatzBomb.MediaControl.Core.Subtitle;
using FatzBomb.MediaControl.Core.Threading;
using System.Runtime.CompilerServices;

namespace FatzBomb.MediaControl.Tests;

/// <summary>
/// Stage 019A: conformance of the Core frame consumer to the <see cref="VideoFrameEventArgs"/>
/// lifetime contract. Payloads are valid only during the callback unless
/// <see cref="VideoFrameEventArgs.PixelDataCanBeRetained"/>; the consumer copies what it keeps
/// and never retains the event object.
/// </summary>
[Collection(MediaPresentationCoordinatorCollection.Name)]
public sealed class VideoFrameLifetimeTests
{
    [Fact]
    public void EphemeralBackgroundFrame_ProducerReuseAfterCallback_DoesNotChangeRetainedFrame()
    {
        var backend = new CapturingBackend();
        var engine = new TestMediaEngine { Content = MediaContent.Audio };
        using MediaPresentationCoordinator coordinator = Create(backend, engine);
        Assert.True(coordinator.AttachPresentationSurface(Surface()));

        var pooled = Pixels(0x5A);
        engine.RaiseBackgroundFrame(Frame(pooled, canBeRetained: false));

        // The producer returns the buffer to its pool and the decoder writes the next frame.
        Array.Fill(pooled, (byte)0xEE);
        backend.Frames.Clear();
        coordinator.RefreshCurrentPosition();

        byte[] refreshed = Assert.Single(backend.Frames, static f => f.Length > 0);
        Assert.All(refreshed, static b => Assert.Equal(0x5A, b));
    }

    [Fact]
    public void RetainableImageFrame_IsStillCopiedByTheConsumer()
    {
        var backend = new CapturingBackend();
        var engine = new TestMediaEngine { Content = MediaContent.Image };
        using MediaPresentationCoordinator coordinator = Create(backend, engine);
        Assert.True(coordinator.AttachPresentationSurface(Surface()));

        var stable = Pixels(0x21);
        engine.RaiseImageFrame(Frame(stable, canBeRetained: true));

        byte[] presented = Assert.Single(backend.Frames, static f => f.Length > 0);
        Assert.All(presented, static b => Assert.Equal(0x21, b));
        Assert.DoesNotContain(backend.Arrays, a => ReferenceEquals(a, stable));
    }

    [Fact]
    public void Consumer_DoesNotRetainEventObjectOrEphemeralPayload()
    {
        var backend = new CapturingBackend();
        var engine = new TestMediaEngine { Content = MediaContent.Audio };
        using MediaPresentationCoordinator coordinator = Create(backend, engine);
        Assert.True(coordinator.AttachPresentationSurface(Surface()));

        (WeakReference args, WeakReference payload) = RaiseUntracked(engine);
        backend.Arrays.Clear();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.False(args.IsAlive);
        Assert.False(payload.IsAlive);
        GC.KeepAlive(coordinator);
    }

    [Fact]
    public void InvalidFrame_ClearsRetainedBackgroundInsteadOfKeepingStalePixels()
    {
        var backend = new CapturingBackend();
        var engine = new TestMediaEngine { Content = MediaContent.Audio };
        using MediaPresentationCoordinator coordinator = Create(backend, engine);
        Assert.True(coordinator.AttachPresentationSurface(Surface()));

        engine.RaiseBackgroundFrame(Frame(Pixels(0x10), canBeRetained: false));
        engine.RaiseBackgroundFrame(new VideoFrameEventArgs { Width = 8, Height = 4, Stride = 32, PtsSeconds = 0 });
        backend.Frames.Clear();
        coordinator.RefreshCurrentPosition();

        Assert.DoesNotContain(backend.Frames, static f => f.Length > 0);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference Args, WeakReference Payload) RaiseUntracked(TestMediaEngine engine)
    {
        var pixels = Pixels(0x7C);
        VideoFrameEventArgs args = Frame(pixels, canBeRetained: false);
        engine.RaiseBackgroundFrame(args);
        return (new WeakReference(args), new WeakReference(pixels));
    }

    private static MediaPresentationCoordinator Create(CapturingBackend backend, TestMediaEngine engine) => new(
        backend,
        new NullRasterizer(),
        engine,
        MediaPlayerConfiguration.Auto,
        new SystemPrecisionWaiterFactory());

    private static MediaPresentationSurfaceDescriptor Surface() => new(
        MediaPresentationSurfaceDescriptor.PrimarySurfaceId,
        MediaPresentationSurfaceRole.Primary,
        MediaNativeSurfaceDescriptor.Win32Hwnd((nint)0x1234),
        64,
        36);

    private static byte[] Pixels(byte fill)
    {
        var pixels = new byte[8 * 4 * 4];
        Array.Fill(pixels, fill);
        return pixels;
    }

    private static VideoFrameEventArgs Frame(byte[] pixels, bool canBeRetained) => new()
    {
        PixelData = pixels,
        PixelDataCanBeRetained = canBeRetained,
        Width = 8,
        Height = 4,
        Stride = 32,
        PtsSeconds = 0
    };

    private sealed class CapturingBackend : IMediaRendererBackend
    {
        public List<byte[]> Frames { get; } = [];
        public List<byte[]?> Arrays { get; } = [];

        public event EventHandler? FramePresented { add { } remove { } }

        public string BackendName => "Capturing";
        public string DeviceLostDiagnosticCategory => "Capturing.DeviceLost";
        public string DeviceLostRecoveryFailedDiagnosticCategory => "Capturing.DeviceLost.Failed";
        public bool IsInitialized { get; private set; }
        public nint NativeDeviceHandle => 0;

        public bool Initialize() => IsInitialized = true;
        public bool AttachSurface(MediaPresentationSurfaceDescriptor surface) => true;
        public bool ResizeSurface(MediaPresentationSurfaceDescriptor surface) => true;
        public bool SetSurfaceLiveResize(MediaPresentationSurfaceDescriptor surface, bool isResizing) => true;
        public bool DetachSurface(MediaPresentationSurfaceDescriptor surface) => true;
        public void SetPresentationStretch(MediaCompositionStretchMode stretchMode) { }
        public bool PresentNativeFrame(VideoFrameEventArgs frame) => false;
        public void ClearCompositionBackgroundFrame() { }

        public bool PresentComposition(MediaCompositionFrame frame)
        {
            Frames.Add(frame.BackgroundBgra.ToArray());
            Arrays.Add(System.Runtime.InteropServices.MemoryMarshal.TryGetArray(frame.BackgroundBgra, out ArraySegment<byte> segment) ? segment.Array : null);
            return true;
        }

        public void CleanupResources() => IsInitialized = false;
        public bool EnsureNativeFrameDevice(VideoFrameEventArgs frame, out bool deviceAdopted)
        {
            deviceAdopted = false;
            return true;
        }

        public void Dispose() => IsInitialized = false;
    }

    private sealed class NullRasterizer : ISubtitleRasterizer
    {
        public SubtitleRasterResult Render(string text, int width, int height) => default;
        public SubtitleRasterResult RenderKaraoke(KaraokeCueTiming timing, TimeSpan position, int width, int height) => default;
        public void Dispose() { }
    }
}
