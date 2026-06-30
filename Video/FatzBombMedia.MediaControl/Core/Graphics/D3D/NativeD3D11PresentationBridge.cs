using FatzBombMedia.MediaControl.Core.Abstractions;
using FatzBombMedia.MediaControl.Core.Configuration;
using FatzBombMedia.MediaControl.Core.Graphics.Composition;
using FatzBombMedia.MediaControl.Core.Graphics.D3D.Device;
using FatzBombMedia.MediaControl.Core.Graphics.D3D.Presentation;
using System.Diagnostics;

namespace FatzBombMedia.MediaControl.Core.Graphics.D3D;

internal sealed class NativeD3D11PresentationBridge : IDisposable
{
    private readonly D3D11DeviceResources _deviceResources = new();
    private readonly D3D11RendererPresentationServices _presentation;
    private readonly Lock _lock = new();
    private readonly MediaPlayerConfiguration _configuration;
    private bool _disposed;

    public NativeD3D11PresentationBridge(MediaPlayerConfiguration configuration)
    {
        _configuration = configuration;
        _presentation = new D3D11RendererPresentationServices(
            configuration,
            _deviceResources,
            new NullD3D11UiSharedSurfacePresentationBackend());
    }

    public event EventHandler? FramePresented;

    public IntPtr NativeDevicePointer => _deviceResources.NativeDevicePointer;

    public string PresentationBackend => _presentation.PresentationBackend;

    public bool IsDirectCompositionActive => _presentation.IsDirectCompositionActive;

    public bool Initialize()
    {
        if (_disposed)
        {
            return false;
        }

        lock (_lock)
        {
            return _presentation.InitializeDevice(_disposed);
        }
    }

    public bool InitializeFromNativeDevice(IntPtr devicePointer, IntPtr deviceContextPointer)
    {
        if (_disposed || devicePointer == IntPtr.Zero)
        {
            return false;
        }

        lock (_lock)
        {
            return _presentation.InitializeFromNativeDevice(_disposed, devicePointer, deviceContextPointer);
        }
    }

    public bool CreateNativeHwndSurface(IntPtr hwnd, int width, int height)
    {
        if (_disposed)
        {
            return false;
        }

        lock (_lock)
        {
            return _presentation.CreateNativeHwndSurface(_disposed, hwnd, width, height, NotifyPresentedWithSync);
        }
    }

    public bool CreateDirectCompositionSurface(IntPtr hwnd, int width, int height)
    {
        if (_disposed)
        {
            return false;
        }

        lock (_lock)
        {
            return _presentation.CreateDirectCompositionSurface(_disposed, hwnd, width, height, NotifyPresentedWithSync);
        }
    }

    public bool CreateSecondaryNativeHwndSurface(string targetId, IntPtr hwnd, int width, int height)
    {
        if (_disposed)
        {
            return false;
        }

        lock (_lock)
        {
            return _presentation.CreateSecondaryNativeHwndSurface(_disposed, targetId, hwnd, width, height);
        }
    }

    public bool RemoveSecondaryPresentationTarget(string targetId)
    {
        lock (_lock)
        {
            return _presentation.RemoveSecondaryPresentationTarget(targetId);
        }
    }

    public void SetNativeLiveResizeInProgress(bool isResizing)
    {
        lock (_lock)
        {
            _presentation.SetNativeLiveResizeInProgress(isResizing);
        }
    }

    public void SetNativePresentationStretch(MediaCompositionStretchMode stretchMode)
    {
        lock (_lock)
        {
            _presentation.SetNativePresentationStretch(stretchMode);
        }
    }

    public void QueueNativeHwndPresentationResize(int width, int height)
    {
        lock (_lock)
        {
            _presentation.QueueNativeHwndResize(_disposed, width, height);
        }
    }

    public void QueueDirectCompositionPresentationResize(int width, int height)
    {
        lock (_lock)
        {
            _presentation.QueueDirectCompositionResize(_disposed, width, height);
        }
    }

    public bool PresentHardwareFrame(VideoFrameEventArgs frame)
    {
        if (frame == null || !frame.IsD3D11HardwareFrame)
        {
            return false;
        }

        lock (_lock)
        {
            return _presentation.PresentHardwareFrame(
                _disposed,
                frame.D3D11TexturePointer,
                frame.D3D11ArraySlice,
                frame.Width,
                frame.Height,
                frame.FramesPerSecond,
                NotifyPresented);
        }
    }

    public void ClearCompositionBackgroundFrame()
    {
        lock (_lock)
        {
            _presentation.ClearCompositionBackgroundFrame();
        }
    }

    public bool PresentMediaCompositionVisible(MediaCompositionFrame frame)
    {
        lock (_lock)
        {
            return _presentation.PresentMediaCompositionVisible(_disposed, frame, RaisePresentedEvent);
        }
    }

    public void CleanupResources()
    {
        lock (_lock)
        {
            _presentation.CleanupResources();
        }
    }

    private void RaisePresentedEvent(int _width, int _height) => RaiseFramePresented();

    private void NotifyPresented(int _width, int _height) => NotifyPresented();

    private void NotifyPresentedWithSync(int _width, int _height, bool syncImmediate) => NotifyPresented(syncImmediate);

    private void NotifyPresented(bool syncImmediate = false) => _presentation.NotifyFramePresented(_disposed, syncImmediate, RaiseFramePresented);

    private void RaiseFramePresented() => FramePresented?.Invoke(this, EventArgs.Empty);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        lock (_lock)
        {
            _presentation.CleanupResources();
            _disposed = true;
        }

        if (_configuration.EnableLogging)
        {
            Debug.WriteLine("[NativeD3D11PresentationBridge] Disposed");
        }
    }
}
