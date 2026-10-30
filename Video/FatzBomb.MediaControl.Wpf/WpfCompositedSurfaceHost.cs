using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace FatzBomb.MediaControl.Wpf;

internal sealed class WpfCompositedSurfaceHost : Grid, IMediaPresentationFrameSink, IDisposable
{
    private readonly Image _image = new()
    {
        Stretch = Stretch.Fill,
        SnapsToDevicePixels = true
    };
    private readonly MediaPresentationFrameSinkHandle _sinkHandle;
    // Core's latest-frame hand-off: the array the UI thread last published goes back for the
    // next frame, so presenting does not allocate a surface-sized array per frame (Stage 022A:
    // about 55 MB/s in software mode at normal window size, several hundred when maximized).
    private readonly LatestFrameMailbox _mailbox = new();
    private bool _disposed;
    private WriteableBitmap? _bitmap;

    public WpfCompositedSurfaceHost()
    {
        Background = Brushes.Black;
        ClipToBounds = true;
        _sinkHandle = new MediaPresentationFrameSinkHandle(this);
        _ = Children.Add(_image);
    }

    public MediaNativeSurfaceDescriptor CreateSurfaceDescriptor() => _sinkHandle.CreateSurfaceDescriptor();

    /// <summary>Gets how many frame buffers were allocated (diagnostics and tests).</summary>
    internal int BufferAllocations => _mailbox.BufferAllocations;

    public void PresentFrame(ReadOnlySpan<byte> bgraPixels, int width, int height, int stride)
    {
        if (_mailbox.Post(bgraPixels, width, height, stride))
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Render, PublishPendingFrame);
    }

    public void Clear()
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(Clear);
            return;
        }

        // A frame presented before the clear and not yet published would bring the old picture
        // back after it (ENG-039).
        _mailbox.DropPending();

        _bitmap = null;
        _image.Source = null;
    }

    /// <summary>Gets whether a frame is shown (tests).</summary>
    internal bool HasFrame => _image.Source != null;

    private void PublishPendingFrame()
    {
        if (_disposed || !_mailbox.TryTake(out var pixels, out var width, out var height, out var stride))
            return;

        if (_bitmap == null || _bitmap.PixelWidth != width || _bitmap.PixelHeight != height)
        {
            _bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Pbgra32, null);
            _image.Source = _bitmap;
        }

        _bitmap.WritePixels(new Int32Rect(0, 0, width, height), pixels, stride, 0);
        _mailbox.Return(pixels);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _sinkHandle.Dispose();
        _ = _mailbox.Close();

        Clear();
    }
}
