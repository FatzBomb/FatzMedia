using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace FatzMedia.Video.WinForms;

/// <summary>
/// Receives final premultiplied BGRA frames from the session's renderer on its presentation
/// thread (Stage 046A) and publishes the latest one to a GDI+ bitmap on the UI thread, where
/// <see cref="MediaSurface"/> draws it in <c>OnPaint</c>. Core's <see cref="LatestFrameMailbox"/>
/// hands the latest frame over (latest wins, two arrays alternating, so steady playback does not
/// allocate per frame), and the painted bitmap is only written on the UI thread: a paint never
/// shows a half-copied frame.
/// </summary>
internal sealed class WinFormsFrameSink : IMediaPresentationFrameSink, IDisposable
{
    private readonly LatestFrameMailbox _mailbox = new();
    private readonly Action _publishRequested;
    private readonly MediaPresentationFrameSinkHandle _handle;

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
    internal int BufferAllocations => _mailbox.BufferAllocations;

    public MediaNativeSurfaceDescriptor CreateSurfaceDescriptor() => _handle.CreateSurfaceDescriptor();

    public void PresentFrame(ReadOnlySpan<byte> bgraPixels, int width, int height, int stride)
    {
        if (_mailbox.Post(bgraPixels, width, height, stride))
            _publishRequested();
    }

    /// <summary>
    /// Copies the pending frame into <see cref="Bitmap"/> (UI thread). Returns whether a frame was
    /// published.
    /// </summary>
    public bool Publish()
    {
        if (!_mailbox.TryTake(out var pixels, out var width, out var height, out var stride))
            return false;

        if (Bitmap is null || Bitmap.Width != width || Bitmap.Height != height)
        {
            Bitmap?.Dispose();
            Bitmap = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
        }

        BitmapData data = Bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
        try
        {
            var rowBytes = width * 4;
            // Rows packed on both sides (the usual case): one copy instead of one per row.
            if (stride == rowBytes && data.Stride == rowBytes)
            {
                Marshal.Copy(pixels, 0, data.Scan0, rowBytes * height);
            }
            else
            {
                for (var y = 0; y < height; y++)
                    Marshal.Copy(pixels, y * stride, data.Scan0 + (y * data.Stride), rowBytes);
            }
        }
        finally
        {
            Bitmap.UnlockBits(data);
        }

        // Hand the copied-out buffer back for the next frame.
        _mailbox.Return(pixels);

        PublishedFrames++;
        return true;
    }

    /// <summary>
    /// Re-arms the publish request after the owner could not schedule it (no window handle):
    /// the next frame asks again.
    /// </summary>
    public void CancelPublishRequest() => _mailbox.CancelPublishRequest();

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
        if (!_mailbox.Close())
            return;

        _handle.Dispose();
        Bitmap?.Dispose();
        Bitmap = null;
    }
}
