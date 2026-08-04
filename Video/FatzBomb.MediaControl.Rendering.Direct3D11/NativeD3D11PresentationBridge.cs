using FatzBomb.MediaControl.Core.Abstractions;
using FatzBomb.MediaControl.Core.Configuration;
using FatzBomb.MediaControl.Core.Graphics;
using FatzBomb.MediaControl.Core.Graphics.Composition;
using FatzBomb.MediaControl.Core.Graphics.D3D.Device;
using FatzBomb.MediaControl.Core.Graphics.D3D.Presentation;
using System.Diagnostics;

namespace FatzBomb.MediaControl.Rendering.Direct3D11;

internal sealed class NativeD3D11PresentationBridge : IMediaRendererBackend
{
    private readonly D3D11DeviceResources _deviceResources = new();
    private readonly D3D11RendererPresentationServices _presentation;
    private readonly Lock _lock = new();
    private readonly MediaPlayerConfiguration _configuration;
    private bool _disposed;

    public NativeD3D11PresentationBridge(
        MediaPlayerConfiguration configuration,
        Direct3D11PresentationOptions options)
    {
        _configuration = configuration;
        _presentation = new D3D11RendererPresentationServices(
            configuration,
            options,
            _deviceResources);
    }

    public event EventHandler? FramePresented;

    public nint NativeDeviceHandle => _deviceResources.NativeDevicePointer;

    public string BackendName => _presentation.PresentationBackend;

    public string DeviceLostDiagnosticCategory => "D3D11.DeviceLost";

    public string DeviceLostRecoveryFailedDiagnosticCategory => "D3D11.DeviceLost.RecoveryFailed";

    public bool IsInitialized => NativeDeviceHandle != 0;

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

    private bool InitializeFromNativeDevice(nint devicePointer, nint deviceContextPointer)
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

    private bool CreateNativeHwndSurface(nint hwnd, int width, int height)
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

    private bool CreateDirectCompositionSurface(nint hwnd, int width, int height)
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

    private bool CreateSecondaryNativeHwndSurface(string targetId, nint hwnd, int width, int height)
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

    private bool RemoveSecondaryPresentationTarget(string targetId)
    {
        lock (_lock)
        {
            return _presentation.RemoveSecondaryPresentationTarget(targetId);
        }
    }

    private void SetNativeLiveResizeInProgress(bool isResizing)
    {
        lock (_lock)
        {
            _presentation.SetNativeLiveResizeInProgress(isResizing);
        }
    }

    public void SetPresentationStretch(MediaCompositionStretchMode stretchMode)
    {
        lock (_lock)
        {
            _presentation.SetNativePresentationStretch(stretchMode);
        }
    }

    private void QueueNativeHwndPresentationResize(int width, int height)
    {
        lock (_lock)
        {
            _presentation.QueueNativeHwndResize(_disposed, width, height);
        }
    }

    private void QueueDirectCompositionPresentationResize(int width, int height)
    {
        lock (_lock)
        {
            _presentation.QueueDirectCompositionResize(_disposed, width, height);
        }
    }

    public bool AttachSurface(MediaPresentationSurfaceDescriptor surface)
    {
        if (_disposed || !TryGetWin32Hwnd(surface, out nint hwnd))
        {
            return false;
        }

        return surface.Role == MediaPresentationSurfaceRole.Primary
            ? CreateDirectCompositionSurface(hwnd, surface.PixelWidth, surface.PixelHeight)
                || CreateNativeHwndSurface(hwnd, surface.PixelWidth, surface.PixelHeight)
            : CreateSecondaryNativeHwndSurface(surface.Id, hwnd, surface.PixelWidth, surface.PixelHeight);
    }

    public bool ResizeSurface(MediaPresentationSurfaceDescriptor surface)
    {
        if (_disposed || !TryGetWin32Hwnd(surface, out nint hwnd))
        {
            return false;
        }

        if (surface.Role != MediaPresentationSurfaceRole.Primary)
        {
            return CreateSecondaryNativeHwndSurface(
                surface.Id,
                hwnd,
                surface.PixelWidth,
                surface.PixelHeight);
        }

        if (_presentation.IsDirectCompositionActive)
        {
            QueueDirectCompositionPresentationResize(surface.PixelWidth, surface.PixelHeight);
        }
        else
        {
            QueueNativeHwndPresentationResize(surface.PixelWidth, surface.PixelHeight);
        }

        return true;
    }

    public bool SetSurfaceLiveResize(MediaPresentationSurfaceDescriptor surface, bool isResizing)
    {
        if (_disposed || !TryGetWin32Hwnd(surface, out _))
        {
            return false;
        }

        if (surface.Role == MediaPresentationSurfaceRole.Primary)
        {
            SetNativeLiveResizeInProgress(isResizing);
        }

        return true;
    }

    private static bool TryGetWin32Hwnd(
        MediaPresentationSurfaceDescriptor? surface,
        out nint hwnd)
    {
        hwnd = 0;
        return surface is { IsValid: true }
            && surface.NativeSurface.SurfaceTypeId == MediaNativeSurfaceTypeIds.Win32Hwnd
            && surface.NativeSurface.TryGetHandle(MediaNativeHandleIds.Window, out hwnd);
    }

    public bool DetachSurface(MediaPresentationSurfaceDescriptor surface)
    {
        if (_disposed || surface is null)
        {
            return false;
        }

        if (surface.Role != MediaPresentationSurfaceRole.Primary)
        {
            return RemoveSecondaryPresentationTarget(surface.Id);
        }

        CleanupResources();
        return true;
    }

    public bool PresentNativeFrame(VideoFrameEventArgs frame)
    {
        if (frame?.NativeResource is not { IsValid: true } resource
            || resource.BackendId != MediaGraphicsBackendId.Direct3D11)
        {
            return false;
        }

        lock (_lock)
        {
            return _presentation.PresentHardwareFrame(
                _disposed,
                resource.ResourceHandle,
                resource.SubresourceIndex,
                frame.Width,
                frame.Height,
                frame.FramesPerSecond,
                NotifyPresented);
        }
    }

    public bool EnsureNativeFrameDevice(VideoFrameEventArgs frame, out bool deviceAdopted)
    {
        deviceAdopted = false;

        if (frame?.NativeResource is not { IsValid: true } resource
            || resource.BackendId != MediaGraphicsBackendId.Direct3D11)
        {
            return false;
        }

        if (NativeDeviceHandle == resource.Device.DeviceHandle)
            return true;

        if (!InitializeFromNativeDevice(resource.Device.DeviceHandle, resource.Device.DeviceContextHandle))
            return false;

        deviceAdopted = true;
        return true;
    }

    public void ClearCompositionBackgroundFrame()
    {
        lock (_lock)
        {
            _presentation.ClearCompositionBackgroundFrame();
        }
    }

    public bool PresentComposition(MediaCompositionFrame frame)
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
