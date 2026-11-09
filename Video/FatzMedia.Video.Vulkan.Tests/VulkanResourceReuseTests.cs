using FatzMedia.Video.Core.Graphics.Composition;
using Silk.NET.Vulkan;

namespace FatzMedia.Video.Vulkan.Tests;

/// <summary>
/// ENG-026: the background is uploaded once per change (keyed by Core's background revision), not
/// once per target per frame, and the readback buffer prefers host-cached memory. Device tests
/// run on lavapipe in CI and return early without a Vulkan device.
/// </summary>
public sealed class VulkanResourceReuseTests
{
    private const MemoryPropertyFlags Visible = MemoryPropertyFlags.HostVisibleBit;
    private const MemoryPropertyFlags Coherent = MemoryPropertyFlags.HostCoherentBit;
    private const MemoryPropertyFlags Cached = MemoryPropertyFlags.HostCachedBit;
    private const MemoryPropertyFlags Local = MemoryPropertyFlags.DeviceLocalBit;

    [Fact]
    public void SelectMemoryType_PrefersCachedCoherentThenCachedThenCoherentForReadback()
    {
        MemoryPropertyFlags[] discrete = [Local, Visible | Coherent, Visible | Coherent | Cached, Local | Visible | Coherent];
        Assert.Equal(2, VulkanOffscreenCompositor.SelectMemoryType(discrete, 0b1111, VulkanOffscreenCompositor.ReadbackMemoryPreferences));

        // The buffer's requirements rule the cached type out: plain coherent memory still works.
        Assert.Equal(1, VulkanOffscreenCompositor.SelectMemoryType(discrete, 0b1011, VulkanOffscreenCompositor.ReadbackMemoryPreferences));

        // Cached but not coherent beats uncached (the readback invalidates before reading).
        MemoryPropertyFlags[] nonCoherentCached = [Local, Visible | Coherent, Visible | Cached];
        Assert.Equal(2, VulkanOffscreenCompositor.SelectMemoryType(nonCoherentCached, 0b111, VulkanOffscreenCompositor.ReadbackMemoryPreferences));

        Assert.Equal(-1, VulkanOffscreenCompositor.SelectMemoryType([Local], 0b1, VulkanOffscreenCompositor.ReadbackMemoryPreferences));
        Assert.Equal(-1, VulkanOffscreenCompositor.SelectMemoryType(discrete, 0, VulkanOffscreenCompositor.ReadbackMemoryPreferences));
    }

    [Fact]
    public void Readback_UsesHostCachedMemoryOnLavapipe()
    {
        using VulkanOffscreenCompositor? compositor = VulkanOffscreenCompositor.TryCreate(out _);
        if (compositor is null)
            return;

        ReadOnlySpan<byte> pixels = compositor.Render(VulkanCompositionPlanner.Plan(MediaCompositionFrame.Empty(MediaCompositionClearMode.ConfiguredColor, 0xFF336699), 4, 4));

        Assert.Equal([0x99, 0x66, 0x33, 0xFF], pixels[..4].ToArray());
        // lavapipe's only memory type is device-local, host-visible, coherent and cached.
        if (compositor.DeviceType == PhysicalDeviceType.Cpu)
            Assert.True(compositor.ReadbackIsHostCached);
    }

    [Fact]
    public void UnchangedBackgroundRevision_IsUploadedOnceForEveryTargetSize()
    {
        using VulkanOffscreenCompositor? compositor = VulkanOffscreenCompositor.TryCreate(out _);
        using VulkanOffscreenCompositor? reference = VulkanOffscreenCompositor.TryCreate(out _);
        if (compositor is null || reference is null)
            return;

        MediaCompositionFrame frame = BackgroundFrame(seed: 1, revision: 7);
        (int Width, int Height)[] targets = [(64, 36), (32, 32), (64, 36)];
        foreach ((int width, int height) in targets)
            Assert.Equal(RenderFresh(reference, frame, width, height), compositor.Render(VulkanCompositionPlanner.Plan(frame, width, height)).ToArray());

        Assert.Equal(1, compositor.BackgroundUploadCount);

        // A frame without a background in between leaves the uploaded one in place.
        _ = compositor.Render(VulkanCompositionPlanner.Plan(MediaCompositionFrame.Empty(), 16, 16));
        Assert.Equal(RenderFresh(reference, frame, 32, 32), compositor.Render(VulkanCompositionPlanner.Plan(frame, 32, 32)).ToArray());
        Assert.Equal(1, compositor.BackgroundUploadCount);

        // A new revision (the next video frame) is uploaded, once.
        MediaCompositionFrame next = BackgroundFrame(seed: 2, revision: 8);
        var nextPixels = compositor.Render(VulkanCompositionPlanner.Plan(next, 64, 36)).ToArray();
        Assert.Equal(RenderFresh(reference, next, 64, 36), nextPixels);
        Assert.Equal(nextPixels, compositor.Render(VulkanCompositionPlanner.Plan(next, 64, 36)).ToArray());
        Assert.Equal(2, compositor.BackgroundUploadCount);
    }

    [Fact]
    public void Background_IsUploadedAgainForANewBufferSizeOrUnknownRevision()
    {
        using VulkanOffscreenCompositor? compositor = VulkanOffscreenCompositor.TryCreate(out _);
        using VulkanOffscreenCompositor? reference = VulkanOffscreenCompositor.TryCreate(out _);
        if (compositor is null || reference is null)
            return;

        _ = compositor.Render(VulkanCompositionPlanner.Plan(BackgroundFrame(seed: 1, revision: 3), 32, 32));
        Assert.Equal(1, compositor.BackgroundUploadCount);

        // Same revision, other pixel buffer: never trusted to hold the same pixels.
        MediaCompositionFrame otherBuffer = BackgroundFrame(seed: 4, revision: 3);
        Assert.Equal(RenderFresh(reference, otherBuffer, 32, 32), compositor.Render(VulkanCompositionPlanner.Plan(otherBuffer, 32, 32)).ToArray());
        Assert.Equal(2, compositor.BackgroundUploadCount);

        // Same revision, other size: the source image is re-created and filled again.
        MediaCompositionFrame larger = BackgroundFrame(seed: 4, revision: 3, width: 24, height: 12);
        Assert.Equal(RenderFresh(reference, larger, 32, 32), compositor.Render(VulkanCompositionPlanner.Plan(larger, 32, 32)).ToArray());
        Assert.Equal(3, compositor.BackgroundUploadCount);

        // An unknown revision always uploads.
        MediaCompositionFrame unknown = BackgroundFrame(seed: 5, revision: -1);
        _ = compositor.Render(VulkanCompositionPlanner.Plan(unknown, 32, 32));
        _ = compositor.Render(VulkanCompositionPlanner.Plan(unknown, 32, 32));
        Assert.Equal(5, compositor.BackgroundUploadCount);
    }

    [Fact]
    public void PrimaryAndSecondaryFrameSinks_ShareOneBackgroundUploadPerFrame()
    {
        if (!VulkanPresentationCoordinatorFactory.IsSupported(out _))
            return;

        VulkanOffscreenCompositor? created = null;
        var primarySink = new CountingFrameSink();
        var secondarySink = new CountingFrameSink();
        using var primaryHandle = new MediaPresentationFrameSinkHandle(primarySink);
        using var secondaryHandle = new MediaPresentationFrameSinkHandle(secondarySink);
        using var backend = new VulkanRendererBackend(() => created = VulkanOffscreenCompositor.TryCreate(out _));
        Assert.True(backend.AttachSurface(new MediaPresentationSurfaceDescriptor(
            MediaPresentationSurfaceDescriptor.PrimarySurfaceId, MediaPresentationSurfaceRole.Primary, primaryHandle.CreateSurfaceDescriptor(), 48, 27)));
        Assert.True(backend.AttachSurface(new MediaPresentationSurfaceDescriptor(
            "secondary", MediaPresentationSurfaceRole.Projector, secondaryHandle.CreateSurfaceDescriptor(), 32, 18)));

        Assert.True(backend.PresentComposition(BackgroundFrame(seed: 1, revision: 1)));
        Assert.True(backend.PresentComposition(BackgroundFrame(seed: 2, revision: 2)));

        Assert.Equal(2, primarySink.Frames);
        Assert.Equal(2, secondarySink.Frames);
        Assert.Equal(2, created!.BackgroundUploadCount);
    }

    [Fact]
    public void RepeatedAndUploadedFrames_AllocateNoManagedMemory()
    {
        // ENG-039 P5-10: buffers stay mapped, so neither a repeated frame nor a new background
        // upload maps memory or allocates (measured 0 bytes per frame on lavapipe).
        using VulkanOffscreenCompositor? compositor = VulkanOffscreenCompositor.TryCreate(out _);
        if (compositor is null)
            return;

        MediaCompositionFrame frame = BackgroundFrame(seed: 3, revision: 1) with { WashArgb = 0x40FF0000, CdgOverlay = CdgFixtures.Snapshot() };
        var plans = new VulkanFramePlan[30];
        for (var i = 0; i < plans.Length; i++)
            plans[i] = VulkanCompositionPlanner.Plan(frame with { BackgroundRevision = 10 + (i / 2) }, 64, 36);

        byte[] expected = compositor.Render(plans[^1]).ToArray();
        for (var i = 0; i < 4; i++)
            _ = compositor.Render(plans[i]);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 4; i < plans.Length; i++)
            _ = compositor.Render(plans[i]);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(allocated < 1024, $"{allocated} bytes for {plans.Length - 4} frames");
        Assert.Equal(expected, compositor.Render(plans[^1]).ToArray());
    }

    private static byte[] RenderFresh(VulkanOffscreenCompositor reference, MediaCompositionFrame frame, int width, int height) =>
        reference.Render(VulkanCompositionPlanner.Plan(frame with { BackgroundRevision = -1 }, width, height)).ToArray();

    private static MediaCompositionFrame BackgroundFrame(int seed, long revision, int width = 16, int height = 9)
    {
        var stride = width * 4;
        var pixels = new byte[stride * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var offset = (y * stride) + (x * 4);
                pixels[offset] = (byte)((x * 37) + seed);
                pixels[offset + 1] = (byte)((y * 53) + (seed * 7));
                pixels[offset + 2] = (byte)((x + y) * 29);
                pixels[offset + 3] = 255;
            }
        }

        return MediaCompositionFrame.Empty() with
        {
            BackgroundBgra = pixels,
            BackgroundWidth = width,
            BackgroundHeight = height,
            BackgroundStride = stride,
            BackgroundStretchMode = MediaCompositionStretchMode.UniformFit,
            BackgroundRevision = revision,
        };
    }

    private sealed class CountingFrameSink : IMediaPresentationFrameSink
    {
        public int Frames { get; private set; }

        public void PresentFrame(ReadOnlySpan<byte> bgraPixels, int width, int height, int stride) => Frames++;
    }
}
