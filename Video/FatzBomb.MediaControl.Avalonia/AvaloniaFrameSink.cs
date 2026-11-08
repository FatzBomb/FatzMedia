using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;

namespace FatzBomb.MediaControl.Avalonia;

/// <summary>
/// Receives final premultiplied BGRA frames from the selected renderer on its presentation
/// thread and publishes the latest one to a <see cref="WriteableBitmap"/> on the UI thread.
/// Core's <see cref="LatestFrameMailbox"/> hands the latest frame over (latest wins, two arrays
/// alternating, so steady playback does not allocate per frame); this sink only marshals the
/// publish to the UI thread and owns the bitmap.
/// </summary>
internal sealed class AvaloniaFrameSink : IMediaPresentationFrameSink, IDisposable
{
    private readonly LatestFrameMailbox _mailbox = new();
    private readonly Action _frameUpdated;
    private readonly MediaPresentationFrameSinkHandle _handle;

    public AvaloniaFrameSink(Action frameUpdated)
    {
        _frameUpdated = frameUpdated ?? throw new ArgumentNullException(nameof(frameUpdated));
        _handle = new MediaPresentationFrameSinkHandle(this);
    }

    /// <summary>Gets the latest published frame (UI thread only).</summary>
    public WriteableBitmap? Bitmap { get; private set; }

    /// <summary>Gets how many frames were published to the bitmap (UI thread only).</summary>
    public long PublishedFrames { get; private set; }

    /// <summary>Gets how many frame buffers were allocated (diagnostics and tests).</summary>
    internal int BufferAllocations => _mailbox.BufferAllocations;

    public MediaNativeSurfaceDescriptor CreateSurfaceDescriptor() => _handle.CreateSurfaceDescriptor();

    public void PresentFrame(ReadOnlySpan<byte> bgraPixels, int width, int height, int stride)
    {
        if (_mailbox.Post(bgraPixels, width, height, stride))
            Dispatcher.UIThread.Post(PublishScheduled, DispatcherPriority.Render);
    }

    /// <summary>Drops the displayed frame (UI thread).</summary>
    public void Clear()
    {
        Bitmap?.Dispose();
        Bitmap = null;
        _frameUpdated();
    }

    public void Dispose()
    {
        if (!_mailbox.Close())
            return;

        _handle.Dispose();
        Bitmap?.Dispose();
        Bitmap = null;
    }

    private void PublishScheduled()
    {
        if (PublishPending())
            _frameUpdated();
    }

    /// <summary>
    /// Copies the frame waiting in the mailbox into the bitmap (UI thread); false when none waits.
    /// The scheduled publish then reports the update; an inline caller (the surface's first-frame
    /// check, Stage 051A) invalidates itself.
    /// </summary>
    public unsafe bool PublishPending()
    {
        if (!_mailbox.TryTake(out var pixels, out var width, out var height, out var stride))
            return false;

        if (Bitmap is null || Bitmap.PixelSize.Width != width || Bitmap.PixelSize.Height != height)
        {
            Bitmap?.Dispose();
            Bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        }

        using (ILockedFramebuffer framebuffer = Bitmap.Lock())
        {
            var rowBytes = width * 4;
            var destination = new Span<byte>((void*)framebuffer.Address, framebuffer.RowBytes * height);
            for (var y = 0; y < height; y++)
                pixels.AsSpan(y * stride, rowBytes).CopyTo(destination.Slice(y * framebuffer.RowBytes, rowBytes));
        }

        // Hand the copied-out buffer back for the next frame.
        _mailbox.Return(pixels);

        PublishedFrames++;
        return true;
    }
}
