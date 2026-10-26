using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace FatzBomb.MediaControl.WinForms;

/// <summary>
/// Receives final premultiplied BGRA frames from the session's renderer on its presentation
/// thread (Stage 046A) and publishes the latest one to a GDI+ bitmap on the UI thread, where
/// <see cref="MediaSurface"/> draws it in <c>OnPaint</c>. Frames that arrive while a publish is
/// queued replace the pending frame (latest wins). Two buffers alternate between the presentation
/// thread and the UI thread, so steady playback does not allocate per frame, and the painted
/// bitmap is only written on the UI thread: a paint never shows a half-copied frame.
/// </summary>
internal sealed class WinFormsFrameSink : IMediaPresentationFrameSink, IDisposable
{
    private readonly Lock _gate = new();
    private readonly Action _publishRequested;
    private readonly MediaPresentationFrameSinkHandle _handle;
    private byte[]? _pending;
    private byte[]? _spare;
    private int _pendingWidth;
    private int _pendingHeight;
    private int _pendingStride;
    private bool _publishQueued;
    private bool _disposed;

    /// <param name="publishRequested">
    /// Called on the presentation thread when a frame waits and no publish is queued: the owner
    /// schedules <see cref="Publish"/> on its UI thread.
    /// </param>
    public WinFormsFrameSink(Action publishRequested)
    {
        _publishRequested = publishRequested ?? throw new ArgumentNullException(nameof(publishRequested));
        _handle = new MediaPresentationFrameSinkHandle(this);
    }

    /// <summary>Gets the latest published frame (UI thread only).</summary>
    public Bitmap? Bitmap { get; private set; }

    /// <summary>Gets how many frames were published to the bitmap (UI thread only).</summary>
    public long PublishedFrames { get; private set; }

    /// <summary>Gets how many frame buffers were allocated (tests).</summary>
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

        _publishRequested();
    }

    /// <summary>
    /// Copies the pending frame into <see cref="Bitmap"/> (UI thread). Returns whether a frame was
    /// published.
    /// </summary>
    public bool Publish()
    {
        byte[]? pixels;
        int width;
        int height;
        int stride;
        lock (_gate)
        {
            _publishQueued = false;
            if (_disposed || _pending is null)
                return false;

            pixels = _pending;
            (width, height, stride) = (_pendingWidth, _pendingHeight, _pendingStride);
            _pending = null;
        }

        if (Bitmap is null || Bitmap.Width != width || Bitmap.Height != height)
        {
            Bitmap?.Dispose();
            Bitmap = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
        }

        BitmapData data = Bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
        try
        {
            var rowBytes = width * 4;
            for (var y = 0; y < height; y++)
                Marshal.Copy(pixels, y * stride, data.Scan0 + (y * data.Stride), rowBytes);
        }
        finally
        {
            Bitmap.UnlockBits(data);
        }

        lock (_gate)
        {
            // Hand the copied-out buffer back for the next frame.
            if (!_disposed && _spare is null)
                _spare = pixels;
        }

        PublishedFrames++;
        return true;
    }

    /// <summary>
    /// Re-arms the publish request after the owner could not schedule it (no window handle):
    /// the next frame asks again.
    /// </summary>
    public void CancelPublishRequest()
    {
        lock (_gate)
            _publishQueued = false;
    }

    /// <summary>
    /// Drops the displayed frame (UI thread). A frame still pending is kept: it may be the first
    /// frame of the next media (as in the Avalonia sink).
    /// </summary>
    public void Clear()
    {
        Bitmap?.Dispose();
        Bitmap = null;
    }

    /// <summary>Stops taking frames and frees the bitmap (UI thread, after the surface is detached).</summary>
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
}
