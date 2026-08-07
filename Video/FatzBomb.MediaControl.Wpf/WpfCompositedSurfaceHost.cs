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
    private readonly Lock _frameGate = new();
    private byte[]? _pendingPixels;
    private int _pendingWidth;
    private int _pendingHeight;
    private int _pendingStride;
    private bool _updateQueued;
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

    public void PresentFrame(ReadOnlySpan<byte> bgraPixels, int width, int height, int stride)
    {
        if (_disposed
            || width <= 0
            || height <= 0
            || stride < width * 4
            || bgraPixels.Length < checked(stride * height))
        {
            return;
        }

        lock (_frameGate)
        {
            var requiredLength = checked(stride * height);
            if (_pendingPixels == null || _pendingPixels.Length < requiredLength)
                _pendingPixels = new byte[requiredLength];

            bgraPixels[..requiredLength].CopyTo(_pendingPixels);
            _pendingWidth = width;
            _pendingHeight = height;
            _pendingStride = stride;
            if (_updateQueued)
                return;

            _updateQueued = true;
        }

        _ = Dispatcher.BeginInvoke(DispatcherPriority.Render, PublishPendingFrame);
    }

    public void Clear()
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(Clear);
            return;
        }

        _bitmap = null;
        _image.Source = null;
    }

    private void PublishPendingFrame()
    {
        byte[]? pixels;
        int width;
        int height;
        int stride;
        lock (_frameGate)
        {
            pixels = _pendingPixels;
            width = _pendingWidth;
            height = _pendingHeight;
            stride = _pendingStride;
            _pendingPixels = null;
            _updateQueued = false;
        }

        if (_disposed || pixels == null || width <= 0 || height <= 0)
            return;

        if (_bitmap == null || _bitmap.PixelWidth != width || _bitmap.PixelHeight != height)
        {
            _bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Pbgra32, null);
            _image.Source = _bitmap;
        }

        _bitmap.WritePixels(new Int32Rect(0, 0, width, height), pixels, stride, 0);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _sinkHandle.Dispose();
        lock (_frameGate)
        {
            _pendingPixels = null;
            _updateQueued = false;
        }

        Clear();
    }
}
