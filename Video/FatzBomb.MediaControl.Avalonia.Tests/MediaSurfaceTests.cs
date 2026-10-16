using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;

namespace FatzBomb.MediaControl.Avalonia.Tests;

public sealed class MediaSurfaceTests
{
    [Theory]
    [InlineData(100, 50, 1, 100, 50)]
    [InlineData(100, 50, 1.5, 150, 75)]
    [InlineData(10.4, 10.5, 1, 10, 11)]
    [InlineData(0, 0, 2, 1, 1)]
    [InlineData(20, 20, double.NaN, 20, 20)]
    public void ToPixelSize_ScalesRoundsAndClamps(double width, double height, double scaling, int expectedWidth, int expectedHeight) =>
        Assert.Equal((expectedWidth, expectedHeight), MediaSurface.ToPixelSize(new Size(width, height), scaling));

    [AvaloniaFact]
    public void ShowingTheSurface_AttachesOnePrimaryFrameSinkSurface()
    {
        (IMediaControlSession session, RecordingSession recorder) = RecordingSession.Create();
        var surface = new MediaSurface { Player = session, VideoDisplayMode = VideoDisplayMode.Stretch };
        Window window = Show(surface, 320, 180);

        Assert.True(surface.IsPrimarySurfaceAttached);
        MediaPresentationSurfaceDescriptor attached = recorder.LastAttached!;
        Assert.Equal(MediaPresentationSurfaceDescriptor.PrimarySurfaceId, attached.Id);
        Assert.Equal(MediaPresentationSurfaceRole.Primary, attached.Role);
        Assert.Equal(MediaNativeSurfaceTypeIds.ManagedBgraFrameSink, attached.NativeSurface.SurfaceTypeId);
        Assert.Equal((320, 180), (attached.PixelWidth, attached.PixelHeight));
        Assert.Equal(VideoDisplayMode.Stretch, recorder.LastDisplayMode);
        window.Close();
    }

    [AvaloniaFact]
    public void ResizingTheWindow_ResizesThePresentationSurface()
    {
        (IMediaControlSession session, RecordingSession recorder) = RecordingSession.Create();
        var surface = new MediaSurface { Player = session };
        Window window = Show(surface, 320, 180);

        window.Width = 640;
        window.Height = 360;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal((640, 360), recorder.LastResize);
        Assert.Equal(new PixelSize(640, 360), surface.AttachedPixelSize);
        window.Close();
    }

    [AvaloniaFact]
    public void ChangingPlayer_DetachesOldAndAttachesNew()
    {
        (IMediaControlSession first, RecordingSession firstRecorder) = RecordingSession.Create();
        (IMediaControlSession second, RecordingSession secondRecorder) = RecordingSession.Create();
        var surface = new MediaSurface { Player = first };
        Window window = Show(surface, 200, 100);

        surface.Player = second;

        Assert.Equal(["attach 200x100", "detach"], firstRecorder.Calls);
        Assert.Equal(["attach 200x100"], secondRecorder.Calls);
        window.Close();
    }

    [AvaloniaFact]
    public void RemovingFromTree_DetachesAndClearingPlayerDetaches()
    {
        (IMediaControlSession session, RecordingSession recorder) = RecordingSession.Create();
        var surface = new MediaSurface { Player = session };
        var host = new ContentControl { Content = surface };
        var window = new Window { Width = 200, Height = 100, Content = host };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        host.Content = null;
        Dispatcher.UIThread.RunJobs();

        Assert.False(surface.IsPrimarySurfaceAttached);
        Assert.Equal(["attach 200x100", "detach"], recorder.Calls);
        window.Close();
    }

    [AvaloniaFact]
    public void NoPlayer_AttachesNothing()
    {
        var surface = new MediaSurface();
        Window window = Show(surface, 200, 100);

        Assert.False(surface.IsPrimarySurfaceAttached);
        window.Close();
    }

    [AvaloniaFact]
    public void PresentedFrame_IsDrawnByTheSurface()
    {
        (IMediaControlSession session, RecordingSession recorder) = RecordingSession.Create();
        var surface = new MediaSurface { Player = session };
        Window window = Show(surface, 40, 20);
        Assert.True(recorder.LastAttached!.NativeSurface.TryGetHandle(MediaNativeHandleIds.FrameSink, out nint handle));
        Assert.True(MediaPresentationFrameSinkHandle.TryResolve(handle, out IMediaPresentationFrameSink? sink));

        // Premultiplied opaque red, presented from a background thread like a renderer would.
        var pixels = new byte[40 * 20 * 4];
        for (var offset = 0; offset < pixels.Length; offset += 4)
            (pixels[offset], pixels[offset + 1], pixels[offset + 2], pixels[offset + 3]) = ((byte)0, (byte)0, (byte)255, (byte)255);
        Task.Run(() => sink!.PresentFrame(pixels, 40, 20, 160)).Wait();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, surface.FrameSink!.PublishedFrames);
        WriteableBitmap? frame = window.CaptureRenderedFrame() as WriteableBitmap ?? ToWriteable(window.CaptureRenderedFrame());
        Assert.NotNull(frame);
        using ILockedFramebuffer framebuffer = frame.Lock();
        var center = ReadPixel(framebuffer, 20, 10);
        Assert.Equal((0, 0, 255, 255), center);
        window.Close();
    }

    [AvaloniaFact]
    public void FrameBursts_PublishOnlyTheLatestFrame()
    {
        (IMediaControlSession session, RecordingSession recorder) = RecordingSession.Create();
        var surface = new MediaSurface { Player = session };
        Window window = Show(surface, 4, 4);
        _ = recorder.LastAttached!.NativeSurface.TryGetHandle(MediaNativeHandleIds.FrameSink, out nint handle);
        _ = MediaPresentationFrameSinkHandle.TryResolve(handle, out IMediaPresentationFrameSink? sink);

        var frame = new byte[4 * 4 * 4];
        for (var i = 0; i < 5; i++)
            sink!.PresentFrame(frame, 4, 4, 16);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, surface.FrameSink!.PublishedFrames);
        window.Close();
    }

    [AvaloniaFact]
    public void SteadyFrames_ReuseBuffersInsteadOfAllocating()
    {
        (IMediaControlSession session, RecordingSession recorder) = RecordingSession.Create();
        var surface = new MediaSurface { Player = session };
        Window window = Show(surface, 4, 4);
        _ = recorder.LastAttached!.NativeSurface.TryGetHandle(MediaNativeHandleIds.FrameSink, out nint handle);
        _ = MediaPresentationFrameSinkHandle.TryResolve(handle, out IMediaPresentationFrameSink? sink);

        var frame = new byte[4 * 4 * 4];
        for (var i = 0; i < 10; i++)
        {
            sink!.PresentFrame(frame, 4, 4, 16);
            Dispatcher.UIThread.RunJobs();
        }

        Assert.Equal(10, surface.FrameSink!.PublishedFrames);
        Assert.Equal(1, surface.FrameSink.BufferAllocations);
        window.Close();
    }

    private static Window Show(MediaSurface surface, double width, double height)
    {
        var window = new Window { Width = width, Height = height, Content = surface };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static WriteableBitmap? ToWriteable(Bitmap? bitmap)
    {
        if (bitmap is null)
            return null;

        var copy = new WriteableBitmap(bitmap.PixelSize, bitmap.Dpi, PixelFormat.Bgra8888, AlphaFormat.Premul);
        using (ILockedFramebuffer framebuffer = copy.Lock())
            bitmap.CopyPixels(framebuffer);
        return copy;
    }

    private static unsafe (int B, int G, int R, int A) ReadPixel(ILockedFramebuffer framebuffer, int x, int y)
    {
        var row = (byte*)framebuffer.Address + (y * framebuffer.RowBytes) + (x * 4);
        return framebuffer.Format == PixelFormat.Rgba8888
            ? (row[2], row[1], row[0], row[3])
            : (row[0], row[1], row[2], row[3]);
    }
}

public sealed class AvaloniaMediaInputSourcesTests
{
    [AvaloniaFact]
    public unsafe void FromBitmap_ProducesStraightAlphaBgra()
    {
        using var bitmap = new WriteableBitmap(new PixelSize(2, 1), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);
        using (ILockedFramebuffer framebuffer = bitmap.Lock())
        {
            var pixels = (byte*)framebuffer.Address;
            (pixels[0], pixels[1], pixels[2], pixels[3]) = ((byte)10, (byte)20, (byte)30, (byte)255);
            (pixels[4], pixels[5], pixels[6], pixels[7]) = ((byte)200, (byte)100, (byte)50, (byte)128);
        }

        MediaInputSource source = AvaloniaMediaInputSources.FromBitmap(bitmap, "test");

        Assert.True(source.IsBgraImage);
        Assert.Equal((2, 1, 8), (source.Width, source.Height, source.Stride));
        Assert.Equal(new byte[] { 10, 20, 30, 255 }, source.BgraPixels![..4]);
        Assert.Equal(128, source.BgraPixels[7]);
        Assert.InRange(source.BgraPixels[4], 198, 202);
        Assert.Equal("test", source.DisplayName);
    }
}
