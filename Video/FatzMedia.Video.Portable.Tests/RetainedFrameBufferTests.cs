using System.Buffers;
using System.Runtime.InteropServices;
using FatzMedia.Video.Controllers;
using FatzMedia.Video.Core.Abstractions;
using FatzMedia.Video.Core.Configuration;
using FatzMedia.Video.Core.Graphics.Composition;
using FatzMedia.Video.Core.Subtitle;
using FatzMedia.Video.Core.Threading;

namespace FatzMedia.Video.Tests;

public sealed class RetainedFrameBufferTests
{
    [Fact]
    public void NewBuffer_IsExclusiveAndSized()
    {
        var buffer = RetainedFrameBuffer.Rent(ArrayPool<byte>.Create(), 16);

        Assert.True(buffer.IsExclusive);
        Assert.Equal(16, buffer.Memory.Length);
    }

    [Fact]
    public void Reference_KeepsArrayAliveUntilLastRelease()
    {
        var pool = new CountingPool();
        var buffer = RetainedFrameBuffer.Rent(pool, 8);

        _ = buffer.AddReference();
        Assert.False(buffer.IsExclusive);
        buffer.Release();
        Assert.True(buffer.IsExclusive);
        Assert.Equal(0, pool.Returned);

        buffer.Release();
        Assert.True(buffer.IsReleased);
        Assert.Equal(1, pool.Returned);
    }

    [Fact]
    public void ReleasedBuffer_RejectsNewReferencesAndExtraReleases()
    {
        var buffer = RetainedFrameBuffer.Rent(ArrayPool<byte>.Create(), 8);
        buffer.Release();

        Assert.Throws<ObjectDisposedException>(() => buffer.AddReference());
        Assert.Throws<InvalidOperationException>(buffer.Release);
    }

    sealed class CountingPool : ArrayPool<byte>
    {
        public int Returned { get; private set; }

        public override byte[] Rent(int minimumLength) => new byte[minimumLength];

        public override void Return(byte[] array, bool clearArray = false) => Returned++;
    }
}

/// <summary>
/// Stage 010A: compositions present the retained background without a per-frame snapshot copy,
/// and a new background frame never overwrites pixels a composition is still presenting.
/// </summary>
[Collection(MediaPresentationCoordinatorGroup.Name)]
public sealed class CompositionBackgroundSharingTests
{
    [Fact]
    public void Composition_PresentsRetainedBufferWithoutSnapshotCopy()
    {
        var backend = new CapturingBackend();
        var engine = new TestMediaEngine { Content = MediaContent.Audio };
        using var coordinator = Create(backend, engine);
        Assert.True(coordinator.AttachPresentationSurface(Surface()));

        engine.RaiseBackgroundFrame(Frame(fill: 0x11));
        engine.RaiseBackgroundFrame(Frame(fill: 0x22));

        var last = backend.Frames.Last(static f => f.Pixels.Length > 0);
        Assert.All(last.Pixels, static b => Assert.Equal(0x22, b));
        // Both background presents saw the same underlying array: the retained buffer was reused
        // in place once the first composition released it, and nothing was copied per present.
        CapturedFrame[] withBackground = [.. backend.Frames.Where(static f => f.Pixels.Length > 0)];
        Assert.True(withBackground.Length >= 2);
        Assert.Same(withBackground[^2].Array, withBackground[^1].Array);
    }

    [Fact]
    public void NewFrameDuringPresent_DoesNotTearThePresentedPixels()
    {
        var engine = new TestMediaEngine { Content = MediaContent.Audio };
        var backend = new CapturingBackend();
        backend.DuringPresent = frame =>
        {
            // A new background frame arrives while this composition is still presenting.
            if (frame.BackgroundBgra.Length > 0 && frame.BackgroundBgra.Span[0] == 0x33)
            {
                backend.DuringPresent = null;
                engine.RaiseBackgroundFrame(Frame(fill: 0x44));
            }
        };
        using var coordinator = Create(backend, engine);
        Assert.True(coordinator.AttachPresentationSurface(Surface()));

        engine.RaiseBackgroundFrame(Frame(fill: 0x33));

        var first = backend.Frames.First(static f => f.Pixels.Length > 0 && f.Pixels[0] == 0x33);
        Assert.All(first.PixelsAfterPresent, static b => Assert.Equal(0x33, b));
        // The nested present completes (and is recorded) before the outer one returns.
        var second = backend.Frames.Single(static f => f.Pixels.Length > 0 && f.Pixels[0] == 0x44);
        Assert.All(second.Pixels, static b => Assert.Equal(0x44, b));
        Assert.NotSame(first.Array, second.Array);
    }

    static MediaPresentationCoordinator Create(CapturingBackend backend, TestMediaEngine engine) => new(
        backend,
        NullSubtitleRasterizer.Instance,
        engine,
        MediaPlayerConfiguration.Auto,
        new SystemPrecisionWaiterFactory());

    static MediaPresentationSurfaceDescriptor Surface() => new(
        MediaPresentationSurfaceDescriptor.PrimarySurfaceId,
        MediaPresentationSurfaceRole.Primary,
        MediaNativeSurfaceDescriptor.Win32Hwnd(0x1234),
        64,
        36);

    static VideoFrameEventArgs Frame(byte fill)
    {
        var pixels = new byte[8 * 4 * 4];
        Array.Fill(pixels, fill);
        return new VideoFrameEventArgs { PixelData = pixels, Width = 8, Height = 4, Stride = 32, PtsSeconds = 0 };
    }

    sealed record CapturedFrame(byte[] Pixels, byte[] PixelsAfterPresent, byte[]? Array);

    sealed class CapturingBackend : IMediaRendererBackend
    {
        public List<CapturedFrame> Frames { get; } = [];
        public Action<MediaCompositionFrame>? DuringPresent { get; set; }

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
            var before = frame.BackgroundBgra.ToArray();
            DuringPresent?.Invoke(frame);
            var after = frame.BackgroundBgra.ToArray();
            var array = MemoryMarshal.TryGetArray(frame.BackgroundBgra, out var segment) ? segment.Array : null;
            Frames.Add(new CapturedFrame(before, after, array));
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

}
