namespace FatzMedia.Video.Tests;

public sealed class MediaNativeSurfaceDescriptorTests
{
    [Fact]
    public void NativeHandle_WithValidIdAndValue_IsValid()
    {
        var handle = new MediaNativeHandle(MediaNativeHandleIds.Window, 123);

        Assert.True(handle.IsValid);
    }

    [Fact]
    public void NativeHandle_WithZeroValue_IsInvalid()
    {
        var handle = new MediaNativeHandle(MediaNativeHandleIds.Window, 0);

        Assert.False(handle.IsValid);
    }

    [Fact]
    public void Win32Hwnd_CreatesValidBorrowedWindowHandle()
    {
        var surface = MediaNativeSurfaceDescriptor.Win32Hwnd(123);

        Assert.True(surface.IsValid);
        Assert.Equal(MediaNativeSurfaceTypeIds.Win32Hwnd, surface.SurfaceTypeId);
        Assert.True(surface.TryGetHandle(MediaNativeHandleIds.Window, out var hwnd));
        Assert.Equal(123, hwnd);
    }

    [Fact]
    public void MultiHandleSurface_SupportsX11DisplayAndWindow()
    {
        var surface = new MediaNativeSurfaceDescriptor(
            MediaNativeSurfaceTypeIds.X11Window,
            [
                new MediaNativeHandle(MediaNativeHandleIds.Display, 10),
                new MediaNativeHandle(MediaNativeHandleIds.Window, 20)
            ]);

        Assert.True(surface.IsValid);
        Assert.True(surface.TryGetHandle(MediaNativeHandleIds.Display, out var display));
        Assert.True(surface.TryGetHandle(MediaNativeHandleIds.Window, out var window));
        Assert.Equal(10, display);
        Assert.Equal(20, window);
    }

    [Fact]
    public void DuplicateHandleIds_AreInvalid()
    {
        var surface = new MediaNativeSurfaceDescriptor(
            MediaNativeSurfaceTypeIds.WaylandSurface,
            [
                new MediaNativeHandle(MediaNativeHandleIds.Surface, 10),
                new MediaNativeHandle(MediaNativeHandleIds.Surface, 20)
            ]);

        Assert.False(surface.IsValid);
    }

    [Fact]
    public void ZeroHandle_IsInvalid()
    {
        var surface = new MediaNativeSurfaceDescriptor(
            MediaNativeSurfaceTypeIds.AndroidNativeWindow,
            [new MediaNativeHandle(MediaNativeHandleIds.Window, 0)]);

        Assert.False(surface.IsValid);
    }

    [Fact]
    public void PresentationSurface_WithSize_PreservesNativePayload()
    {
        var nativeSurface = MediaNativeSurfaceDescriptor.Win32Hwnd(123);
        var surface = new MediaPresentationSurfaceDescriptor(
            MediaPresentationSurfaceDescriptor.PrimarySurfaceId,
            MediaPresentationSurfaceRole.Primary,
            nativeSurface,
            PixelWidth: 1280,
            PixelHeight: 720);

        var resized = surface.WithSize(1920, 1080);

        Assert.True(surface.IsValid);
        Assert.Same(nativeSurface, resized.NativeSurface);
        Assert.Equal(1920, resized.PixelWidth);
        Assert.Equal(1080, resized.PixelHeight);
    }

    [Fact]
    public void Identifier_RejectsWhitespace()
    {
        Assert.Throws<ArgumentException>(() => new MediaNativeSurfaceTypeId(" "));
        Assert.Throws<ArgumentException>(() => new MediaNativeHandleId(" "));
    }

    [Fact]
    public void ManagedFrameSinkHandle_CreatesResolvableBorrowedSurface()
    {
        var sink = new TestFrameSink();
        using var handle = new MediaPresentationFrameSinkHandle(sink);

        var surface = handle.CreateSurfaceDescriptor();

        Assert.True(surface.IsValid);
        Assert.Equal(MediaNativeSurfaceTypeIds.ManagedBgraFrameSink, surface.SurfaceTypeId);
        Assert.True(surface.TryGetHandle(MediaNativeHandleIds.FrameSink, out var value));
        Assert.True(MediaPresentationFrameSinkHandle.TryResolve(value, out var resolved));
        Assert.Same(sink, resolved);
    }

    [Fact]
    public void ManagedFrameSinkHandle_ResolvesFromPresentationSurfaceUntilDisposed()
    {
        var sink = new TestFrameSink();
        var handle = new MediaPresentationFrameSinkHandle(sink);
        var surface = new MediaPresentationSurfaceDescriptor("sink", MediaPresentationSurfaceRole.Primary, handle.CreateSurfaceDescriptor(), 4, 4);
        var wrongKind = new MediaPresentationSurfaceDescriptor("hwnd", MediaPresentationSurfaceRole.Primary, MediaNativeSurfaceDescriptor.Win32Hwnd(handle.Value), 4, 4);

        Assert.True(MediaPresentationFrameSinkHandle.TryResolve(surface, out var resolved));
        Assert.Same(sink, resolved);
        Assert.False(MediaPresentationFrameSinkHandle.TryResolve(wrongKind, out resolved));
        Assert.Null(resolved);
        Assert.False(MediaPresentationFrameSinkHandle.TryResolve(null, out resolved));

        handle.Dispose();

        Assert.False(MediaPresentationFrameSinkHandle.TryResolve(surface, out resolved));
        Assert.Null(resolved);
    }

    [Fact]
    public void ManagedFrameSinkHandle_AfterDispose_CannotCreateSurface()
    {
        var handle = new MediaPresentationFrameSinkHandle(new TestFrameSink());
        handle.Dispose();

        Assert.False(handle.IsAllocated);
        Assert.Equal(0, handle.Value);
        Assert.Throws<ObjectDisposedException>(handle.CreateSurfaceDescriptor);
    }

    [Fact]
    public void ManagedFrameSinkHandle_StaleToken_DoesNotResolveAfterDispose()
    {
        var sink = new TestFrameSink();
        var handle = new MediaPresentationFrameSinkHandle(sink);
        var token = handle.Value;
        Assert.NotEqual(0, token);

        handle.Dispose();

        // After dispose, the stale token must not resolve.
        Assert.False(MediaPresentationFrameSinkHandle.TryResolve(token, out var resolved));
        Assert.Null(resolved);
    }

    [Fact]
    public void ManagedFrameSinkHandle_RepeatedDispose_IsSafe()
    {
        var handle = new MediaPresentationFrameSinkHandle(new TestFrameSink());
        handle.Dispose();

        // Second dispose must not throw and must leave the handle idempotently disposed.
        var ex = Record.Exception(handle.Dispose);
        Assert.Null(ex);

        Assert.False(handle.IsAllocated);
        Assert.Equal(0, handle.Value);
    }

    [Fact]
    public void ManagedFrameSinkHandle_LaterHandle_DoesNotMakeOldTokenResolve()
    {
        var sinkA = new TestFrameSink();
        var handleA = new MediaPresentationFrameSinkHandle(sinkA);
        var tokenA = handleA.Value;
        Assert.NotEqual(0, tokenA);

        handleA.Dispose();

        // Create a second handle for a different sink.
        var sinkB = new TestFrameSink();
        using var handleB = new MediaPresentationFrameSinkHandle(sinkB);
        var tokenB = handleB.Value;
        Assert.NotEqual(0, tokenB);
        Assert.NotEqual(tokenA, tokenB);

        // The old token must not resolve — and must not resolve to sinkB.
        Assert.False(MediaPresentationFrameSinkHandle.TryResolve(tokenA, out var resolvedA));
        Assert.Null(resolvedA);

        // The new token must resolve to sinkB, not sinkA.
        Assert.True(MediaPresentationFrameSinkHandle.TryResolve(tokenB, out var resolvedB));
        Assert.Same(sinkB, resolvedB);
    }

    sealed class TestFrameSink : IMediaPresentationFrameSink
    {
        public void PresentFrame(ReadOnlySpan<byte> bgraPixels, int width, int height, int stride)
        {
        }
    }
}
