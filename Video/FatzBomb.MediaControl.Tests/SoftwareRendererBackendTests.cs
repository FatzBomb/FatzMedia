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
    public void AttachSurface_RejectsInvalidSink()
    {
        var backend = new SoftwareRendererBackend();
        var invalidNativeSurface = new MediaNativeSurfaceDescriptor(
            MediaNativeSurfaceTypeIds.ManagedBgraFrameSink,
            [new MediaNativeHandle(MediaNativeHandleIds.FrameSink, (nint)123456)]);
        var invalid = new MediaPresentationSurfaceDescriptor(
            "invalid",
            MediaPresentationSurfaceRole.Primary,
            invalidNativeSurface,
            1,
            1);

        Assert.False(backend.AttachSurface(invalid));
        backend.Dispose();
    }

    [Fact]
    public void SecondaryWindow_ReceivesEachCompositionAtItsOwnSize()
    {
        // ENG-016: in software mode the secondary/projector window is a managed frame sink too,
        // and the backend renders every composition for each attached target.
        var backend = new SoftwareRendererBackend();
        var primarySink = new RecordingFrameSink();
        var projectorSink = new RecordingFrameSink();
        using var primaryHandle = new MediaPresentationFrameSinkHandle(primarySink);
        using var projectorHandle = new MediaPresentationFrameSinkHandle(projectorSink);
        MediaPresentationSurfaceDescriptor projector = CreateSurface(projectorHandle, 3, 2) with
        {
            Id = "secondary",
            Role = MediaPresentationSurfaceRole.Projector
        };
        var presentedCount = 0;
        backend.FramePresented += (_, _) => presentedCount++;

        Assert.True(backend.AttachSurface(CreateSurface(primaryHandle, 2, 1)));
        Assert.True(backend.AttachSurface(projector));
        Assert.True(backend.PresentComposition(MediaCompositionFrame.Empty()));

        Assert.Equal(1, presentedCount);
        Assert.Equal((2, 1), (primarySink.Width, primarySink.Height));
        Assert.Equal((3, 2), (projectorSink.Width, projectorSink.Height));
        Assert.Equal(Enumerable.Repeat(new byte[] { 0, 0, 0, 255 }, 6).SelectMany(static pixel => pixel), projectorSink.Pixels);

        Assert.True(backend.ResizeSurface(projector.WithSize(4, 1)));
        Assert.True(backend.PresentComposition(MediaCompositionFrame.Empty()));
        Assert.Equal((4, 1), (projectorSink.Width, projectorSink.Height));

        Assert.True(backend.DetachSurface(projector));
        projectorSink.Width = 0;
        Assert.True(backend.PresentComposition(MediaCompositionFrame.Empty()));
        Assert.Equal(0, projectorSink.Width);
        Assert.Equal((2, 1), (primarySink.Width, primarySink.Height));
        backend.Dispose();
    }

    [Fact]
    public void ACompositionForSomeTargets_ReachesThoseTargetsOnly()
    {
        // Stage 017A slice 5: display-locked video on two monitors presents each one's targets.
        var backend = new SoftwareRendererBackend();
        var primarySink = new RecordingFrameSink();
        var projectorSink = new RecordingFrameSink();
        using var primaryHandle = new MediaPresentationFrameSinkHandle(primarySink);
        using var projectorHandle = new MediaPresentationFrameSinkHandle(projectorSink);
        MediaPresentationSurfaceDescriptor projector = CreateSurface(projectorHandle, 3, 2) with
        {
            Id = "secondary",
            Role = MediaPresentationSurfaceRole.Projector
        };
        Assert.True(backend.AttachSurface(CreateSurface(primaryHandle, 2, 1)));
        Assert.True(backend.AttachSurface(projector));

        Assert.True(backend.PresentComposition(MediaCompositionFrame.Empty() with
        {
            Targets = new MediaCompositionTargetFilter(includesPrimary: false, ["secondary"])
        }));
        Assert.Equal(0, primarySink.Width);
        Assert.Equal((3, 2), (projectorSink.Width, projectorSink.Height));

        projectorSink.Width = 0;
        Assert.True(backend.PresentComposition(MediaCompositionFrame.Empty() with
        {
            Targets = new MediaCompositionTargetFilter(includesPrimary: true, [])
        }));
        Assert.Equal((2, 1), (primarySink.Width, primarySink.Height));
        Assert.Equal(0, projectorSink.Width);

        // A filter whose surfaces are gone is nothing to do, not a failure.
        Assert.True(backend.PresentComposition(MediaCompositionFrame.Empty() with
        {
            Targets = new MediaCompositionTargetFilter(includesPrimary: false, ["detached"])
        }));
        backend.Dispose();
    }

    [Fact]
    public void PresentComposition_ReportsThePrimaryResult_WhenAPrimaryIsAttached()
    {
        var backend = new SoftwareRendererBackend();
        var primarySink = new RecordingFrameSink();
        var projectorSink = new RecordingFrameSink();
        using var primaryHandle = new MediaPresentationFrameSinkHandle(primarySink);
        using var projectorHandle = new MediaPresentationFrameSinkHandle(projectorSink);
        MediaPresentationSurfaceDescriptor primary = CreateSurface(primaryHandle, 2, 1);
        MediaPresentationSurfaceDescriptor projector = CreateSurface(projectorHandle, 2, 1) with
        {
            Id = "secondary",
            Role = MediaPresentationSurfaceRole.Projector
        };
        var presentedCount = 0;
        backend.FramePresented += (_, _) => presentedCount++;

        Assert.True(backend.AttachSurface(primary));
        Assert.True(backend.AttachSurface(projector));
        // A primary too large to render fails even though the projector presents.
        Assert.True(backend.ResizeSurface(primary.WithSize(int.MaxValue, int.MaxValue)));

        Assert.False(backend.PresentComposition(MediaCompositionFrame.Empty()));
        Assert.Equal(0, presentedCount);
        Assert.Equal(2, projectorSink.Width);

        // With only secondaries attached, any presented target is a presented frame.
        Assert.True(backend.DetachSurface(primary));
        Assert.True(backend.PresentComposition(MediaCompositionFrame.Empty()));
        Assert.Equal(1, presentedCount);
        backend.Dispose();
    }

    [Fact]
    public void TwoTargetsOfDifferentShapes_AreEachComposedForTheirOwnSize()
    {
        // Stage 027A slice 5: a karaoke singer screen beside the operator view. Each target is
        // composed at its own size (letterboxing included), never a scaled copy of the other.
        var backend = new SoftwareRendererBackend();
        var primarySink = new RecordingFrameSink();
        var projectorSink = new RecordingFrameSink();
        using var primaryHandle = new MediaPresentationFrameSinkHandle(primarySink);
        using var projectorHandle = new MediaPresentationFrameSinkHandle(projectorSink);
        MediaPresentationSurfaceDescriptor projector = CreateSurface(projectorHandle, 8, 2) with
        {
            Id = "singer",
            Role = MediaPresentationSurfaceRole.Projector
        };
        // A 2x1 red|blue picture, fitted (letterboxed) into a 4x4 and an 8x2 target.
        MediaCompositionFrame frame = MediaCompositionFrame.Empty() with
        {
            BackgroundBgra = new byte[] { 0, 0, 255, 255, 255, 0, 0, 255 },
            BackgroundWidth = 2,
            BackgroundHeight = 1,
            BackgroundStride = 8,
            BackgroundStretchMode = MediaCompositionStretchMode.UniformFit,
        };

        Assert.True(backend.AttachSurface(CreateSurface(primaryHandle, 4, 4)));
        Assert.True(backend.AttachSurface(projector));
        Assert.True(backend.PresentComposition(frame));

        var renderer = new SoftwareCompositionRenderer();
        Assert.Equal((4, 4, 16), (primarySink.Width, primarySink.Height, primarySink.Stride));
        Assert.Equal(renderer.Render(frame, 4, 4).Pixels.ToArray(), primarySink.Pixels);
        Assert.Equal((8, 2, 32), (projectorSink.Width, projectorSink.Height, projectorSink.Stride));
        Assert.Equal(renderer.Render(frame, 8, 2).Pixels.ToArray(), projectorSink.Pixels);
        // The 4x4 target letterboxes (black rows), the 2:1-wider 8x2 target pillarboxes.
        Assert.Equal([0, 0, 0, 255], primarySink.Pixels[..4]);
        Assert.Equal([0, 0, 0, 255], projectorSink.Pixels[..4]);
        Assert.Equal([0, 0, 255, 255], projectorSink.Pixels[(2 * 4)..(3 * 4)]);
        Assert.Equal([255, 0, 0, 255], projectorSink.Pixels[(4 * 4)..(5 * 4)]);
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
        public int Width { get; set; }
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
