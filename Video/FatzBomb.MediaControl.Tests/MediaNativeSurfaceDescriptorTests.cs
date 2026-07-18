namespace FatzBomb.MediaControl.Tests;

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
        MediaNativeSurfaceDescriptor surface = MediaNativeSurfaceDescriptor.Win32Hwnd(123);

        Assert.True(surface.IsValid);
        Assert.Equal(MediaNativeSurfaceTypeIds.Win32Hwnd, surface.SurfaceTypeId);
        Assert.True(surface.TryGetHandle(MediaNativeHandleIds.Window, out nint hwnd));
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
        Assert.True(surface.TryGetHandle(MediaNativeHandleIds.Display, out nint display));
        Assert.True(surface.TryGetHandle(MediaNativeHandleIds.Window, out nint window));
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
        MediaNativeSurfaceDescriptor nativeSurface = MediaNativeSurfaceDescriptor.Win32Hwnd(123);
        var surface = new MediaPresentationSurfaceDescriptor(
            MediaPresentationSurfaceDescriptor.PrimarySurfaceId,
            MediaPresentationSurfaceRole.Primary,
            nativeSurface,
            PixelWidth: 1280,
            PixelHeight: 720);

        MediaPresentationSurfaceDescriptor resized = surface.WithSize(1920, 1080);

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
}
