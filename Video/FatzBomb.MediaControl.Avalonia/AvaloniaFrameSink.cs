using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;

namespace FatzBomb.MediaControl.Avalonia;

/// <summary>
/// Receives final premultiplied BGRA frames from the selected renderer on its presentation
/// thread and publishes the latest one to a <see cref="WriteableBitmap"/> on the UI thread.
/// Frames that arrive while a publish is queued replace the pending frame (latest wins). Two
/// buffers alternate between the presentation thread and the UI thread, so steady playback
/// does not allocate per frame.
/// </summary>
internal sealed class AvaloniaFrameSink : IMediaPresentationFrameSink, IDisposable
{
    private readonly Lock _gate = new();
    private readonly Action _frameUpdated;
    private readonly MediaPresentationFrameSinkHandle _handle;
    private byte[]? _pending;
    private byte[]? _spare;
    private int _pendingWidth;
    private int _pendingHeight;
    private int _pendingStride;
    private bool _publishQueued;
    private bool _disposed;

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
    internal int BufferAllocations { get; private set; }

    public MediaNativeSurfaceDescriptor CreateSurfaceDescriptor() => _handle.CreateSurfaceDescriptor();

    public void PresentFrame(ReadOnlySpan<byte> bgraPixels, int width, int height, int stride)
    {
        if (width <= 0 || height <= 0 || stride < width * 4 || bgraPixels.Length < (long)stride * height)
            return;

        lock (_gate)
        {
            if (_disposed)
                return;

            var length = stride * height;
            if (_pending is null)
                (_pending, _spare) = (_spare, null);

            if (_pending is null || _pending.Length < length)
            {
                _pending = new byte[length];
                BufferAllocations++;
            }

            bgraPixels[..length].CopyTo(_pending);
            (_pendingWidth, _pendingHeight, _pendingStride) = (width, height, stride);
            if (_publishQueued)
                return;

            _publishQueued = true;
        }

        Dispatcher.UIThread.Post(Publish, DispatcherPriority.Render);
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
        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
            _pending = null;
            _spare = null;
        }

        _handle.Dispose();
        Bitmap?.Dispose();
        Bitmap = null;
    }

    private unsafe void Publish()
    {
        byte[]? pixels;
        int width;
        int height;
        int stride;
        lock (_gate)
        {
            _publishQueued = false;
            if (_disposed || _pending is null)
                return;

            pixels = _pending;
            (width, height, stride) = (_pendingWidth, _pendingHeight, _pendingStride);
            _pending = null;
        }

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

        lock (_gate)
        {
            // Hand the copied-out buffer back for the next frame.
            if (!_disposed && _spare is null)
                _spare = pixels;
        }

        PublishedFrames++;
        _frameUpdated();
    }
}
