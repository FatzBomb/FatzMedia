using FatzBomb.MediaControl.Core.Graphics.Composition;
using FatzBomb.MediaControl.Rendering.Software;

namespace FatzBomb.MediaControl.Tests;

public sealed class SoftwareRendererBackendTests
{
    [Fact]
    public void AttachPresentResizeDetach_UsesManagedFrameSink()
    {
        var sink = new RecordingFrameSink();
        using var sinkHandle = new MediaPresentationFrameSinkHandle(sink);
        var backend = new SoftwareRendererBackend();
        MediaPresentationSurfaceDescriptor surface = CreateSurface(sinkHandle, 2, 1);
        var presentedCount = 0;
        backend.FramePresented += (_, _) => presentedCount++;

        Assert.True(backend.AttachSurface(surface));
        Assert.True(backend.IsInitialized);
        Assert.True(backend.PresentComposition(MediaCompositionFrame.Empty()));
        Assert.Equal(1, presentedCount);
        Assert.Equal(2, sink.Width);
        Assert.Equal(1, sink.Height);
        Assert.Equal(new byte[] { 0, 0, 0, 255, 0, 0, 0, 255 }, sink.Pixels);

        MediaPresentationSurfaceDescriptor resized = surface.WithSize(1, 2);
        Assert.True(backend.ResizeSurface(resized));
        Assert.True(backend.PresentComposition(MediaCompositionFrame.Empty()));
        Assert.Equal(1, sink.Width);
        Assert.Equal(2, sink.Height);

        Assert.True(backend.DetachSurface(resized));
        Assert.False(backend.PresentComposition(MediaCompositionFrame.Empty()));
        backend.Dispose();
    }

    [Fact]
    public void AttachSurface_RejectsSecondaryAndInvalidSink()
    {
        var backend = new SoftwareRendererBackend();
        var sink = new RecordingFrameSink();
        using var sinkHandle = new MediaPresentationFrameSinkHandle(sink);
        MediaPresentationSurfaceDescriptor secondary = CreateSurface(sinkHandle, 1, 1) with
        {
            Role = MediaPresentationSurfaceRole.Projector
        };
        var invalidNativeSurface = new MediaNativeSurfaceDescriptor(
            MediaNativeSurfaceTypeIds.ManagedBgraFrameSink,
            [new MediaNativeHandle(MediaNativeHandleIds.FrameSink, (nint)123456)]);
        var invalid = new MediaPresentationSurfaceDescriptor(
            "invalid",
            MediaPresentationSurfaceRole.Primary,
            invalidNativeSurface,
            1,
            1);

        Assert.False(backend.AttachSurface(secondary));
        Assert.False(backend.AttachSurface(invalid));
        backend.Dispose();
    }

    [Fact]
    public void CleanupResources_ClearsInitializationAndSurface()
    {
        var backend = new SoftwareRendererBackend();
        var sink = new RecordingFrameSink();
        using var sinkHandle = new MediaPresentationFrameSinkHandle(sink);

        Assert.True(backend.AttachSurface(CreateSurface(sinkHandle, 1, 1)));

        backend.CleanupResources();

        Assert.False(backend.IsInitialized);
        Assert.False(backend.PresentComposition(MediaCompositionFrame.Empty()));
        backend.Dispose();
    }

    private static MediaPresentationSurfaceDescriptor CreateSurface(
        MediaPresentationFrameSinkHandle sinkHandle,
        int width,
        int height) => new(
            MediaPresentationSurfaceDescriptor.PrimarySurfaceId,
            MediaPresentationSurfaceRole.Primary,
            sinkHandle.CreateSurfaceDescriptor(),
            width,
            height);

    private sealed class RecordingFrameSink : IMediaPresentationFrameSink
    {
        public byte[] Pixels { get; private set; } = [];
        public int Width { get; private set; }
        public int Height { get; private set; }
        public int Stride { get; private set; }

        public void PresentFrame(ReadOnlySpan<byte> bgraPixels, int width, int height, int stride)
        {
            Pixels = bgraPixels.ToArray();
            Width = width;
            Height = height;
            Stride = stride;
        }
    }
}
