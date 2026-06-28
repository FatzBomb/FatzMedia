using System.Windows.Media;
using FatzBombMedia.MediaControl.Core.Configuration;
using FatzBombMedia.MediaControl.Core.DxCdg;
using System.Diagnostics;
using System.Threading;
using FatzBombMedia.MediaControl.Core.Graphics.D3D.Device;
using FatzBombMedia.MediaControl.Core.Graphics.D3D.Presentation;

namespace FatzBombMedia.MediaControl.Core.Graphics.D3D;

/// <summary>
/// Thin compatibility facade over the D3D11 presentation services.
/// </summary>
internal sealed class VorticeD3D11Renderer : IDisposable
{
    private readonly D3D11DeviceResources _deviceResources;
    private readonly D3D11RendererPresentationServices _presentation;
    private bool _disposed;
    private readonly Lock _lock = new();
    private readonly MediaPlayerConfiguration _configuration;
    public VorticeD3D11Renderer()
        : this(MediaPlayerConfiguration.Auto)
    {
    }

    public VorticeD3D11Renderer(MediaPlayerConfiguration configuration)
    {
        _configuration = configuration;
        _deviceResources = new D3D11DeviceResources();
        _presentation = new D3D11RendererPresentationServices(configuration, _deviceResources);
    }

    /// <summary>
    /// Raised on the UI dispatcher thread immediately after a frame's dirty rect has been
    /// applied to the D3DImage. WPF's CompositionTarget.Rendering scheduler throttles down
    /// when it doesn't detect tracked visual-tree changes; D3DImage pixel updates alone are
    /// invisible to that scheduler, so subscribers should use this to invalidate the hosting
    /// element and keep the render tick running at the true display refresh rate.
    /// </summary>
    public event EventHandler? FramePresented;

    /// <summary>
    /// Gets the WPF ImageSource backed by a D3DImage shared surface.
    /// </summary>
    public ImageSource? ImageSource => _presentation.ImageSource;

    /// <summary>
    /// Gets whether the D3D11 device and active presentation target are ready.
    /// </summary>
    public bool IsReady => _presentation.IsReady;

    public IntPtr NativeDevicePointer => _deviceResources.NativeDevicePointer;

    public bool IsNativeSwapChainActive => _presentation.IsNativeHwndActive;


    public void SetNativeLiveResizeInProgress(bool isResizing)
    {
        _presentation.SetNativeLiveResizeInProgress(isResizing);
    }

    public bool IsDirectCompositionActive => _presentation.IsDirectCompositionActive;

    public string PresentationBackend => _presentation.PresentationBackend;

    public void SetNativePresentationStretch(Stretch stretch)
    {
        lock (_lock)
        {
            _presentation.SetNativePresentationStretch(stretch);
        }
    }

    public void RefreshNativePresentation()
    {
        if (_disposed)
        {
            return;
        }

        lock (_lock)
        {
            if (_presentation.TryGetActiveTargetSnapshot(
                    applyPendingNativeResize: true,
                    reason: "native presentation refresh",
                    out _))
            {
                NotifyPresented(syncImmediate: true);
            }
        }
    }

    /// <summary>
    /// Initialize D3D11 device for GPU-accelerated pixel operations.
    /// </summary>
    /// <returns>True if initialization succeeded.</returns>
    public bool Initialize()
    {
        if (_disposed) return false;

        lock (_lock)
        {
            return _presentation.InitializeDevice(_disposed);
        }
    }

    public bool InitializeFromNativeDevice(IntPtr devicePointer, IntPtr deviceContextPointer)
    {
        if (_disposed || devicePointer == IntPtr.Zero) return false;

        lock (_lock)
        {
            return _presentation.InitializeFromNativeDevice(_disposed, devicePointer, deviceContextPointer);
        }
    }

    /// <summary>
    /// Creates or recreates surfaces for the given dimensions.
    /// </summary>
    /// <param name="width">Surface width in pixels</param>
    /// <param name="height">Surface height in pixels</param>
    /// <returns>True if surfaces were created successfully.</returns>
    public bool CreateSurfaces(int width, int height)
    {
        lock (_lock)
        {
            return _presentation.CreateD3DImageSurface(_disposed, width, height, NotifyPresentedWithSync);
        }
    }

    public bool CreateNativeHwndSurfaces(IntPtr hwnd, int width, int height)
    {
        lock (_lock)
        {
            return _presentation.CreateNativeHwndSurface(_disposed, hwnd, width, height, NotifyPresentedWithSync);
        }
    }

    /// <summary>
    /// Creates the current DirectComposition presentation backend.  This remains the existing
    /// single-target/base+overlay implementation; future true layered composition work should
    /// happen inside the backend rather than adding more DirectComposition conditionals here.
    /// </summary>
    public bool CreateDirectCompositionHwndSurfaces(IntPtr hwnd, int width, int height)
    {
        lock (_lock)
        {
            return _presentation.CreateDirectCompositionSurface(_disposed, hwnd, width, height, NotifyPresentedWithSync);
        }
    }

    /// <summary>
    /// Updates the DirectComposition visual output size without recreating the composition swap-chains.
    /// </summary>
    public void SetDirectCompositionOutputSize(int width, int height)
    {
        lock (_lock)
        {
            _presentation.SetDirectCompositionOutputSize(_disposed, width, height);
        }
    }

    /// <summary>
    /// Updates the DirectComposition overlay swap-chain. Pixel data must be BGRA with
    /// premultiplied alpha at the current presentation size. This method presents only the
    /// overlay swap-chain; the base video swap-chain keeps its own native cadence.
    /// </summary>
    public bool UploadDirectCompositionOverlayFrame(ReadOnlySpan<byte> pixelData, int width, int height, int stride)
    {
        lock (_lock)
        {
            return _presentation.UploadDirectCompositionOverlayFrame(_disposed, pixelData, width, height, stride);
        }
    }

    /// <summary>
    /// Uploads a retained CDG overlay texture for the native single-swap-chain compositor.
    /// The texture is source-sized to the current video frame before it gets here, so the final
    /// video-processor pass can sample video + CDG in one coordinate system and present once.
    /// Pixel data is straight-alpha BGRA.
    /// </summary>

    /// <summary>
    /// Clears the currently presented frame and immediately presents/invalidates the active
    /// presentation target.  Call before switching to audio-only media so native swap-chain,
    /// DirectComposition, and D3DImage targets do not retain the previous video's last frame.
    /// </summary>
    public void ClearPresentedFrame()
    {
        lock (_lock)
        {
            _presentation.ClearPresentedFrame(_disposed, RaiseFramePresented);
        }
    }

    public void QueueNativeHwndPresentationResize(int width, int height)
    {
        lock (_lock)
        {
            _presentation.QueueNativeHwndResize(_disposed, width, height);
        }
    }

    /// <summary>
    /// Resizes the stable native HWND swap-chain to the current host/client size without
    /// recreating the renderer. The video source texture remains media-sized; the video
    /// processor then draws it into an aspect-correct rectangle inside this host-sized
    /// backbuffer.
    /// </summary>
    public bool ResizeNativeHwndPresentation(int width, int height)
    {
        lock (_lock)
        {
            return _presentation.ResizeNativeHwndPresentation(_disposed, width, height);
        }
    }

    /// <summary>
    /// Presents a BGRA video frame through the normal video fast path, then asks the retained
    /// layer compositor to draw wash/overlay layers before the active backend is presented.
    /// </summary>

    public void UploadFrame(ReadOnlySpan<byte> pixelData, int width, int height, int stride)
    {
        lock (_lock)
        {
            _presentation.UploadCpuFrame(
                _disposed,
                pixelData,
                width,
                height,
                stride,
                NotifyPresented);
        }
    }

    public bool PresentHardwareFrame(
        IntPtr sourceTexturePointer,
        int sourceArraySlice,
        int width,
        int height,
        double framesPerSecond)
    {
        lock (_lock)
        {
            return _presentation.PresentHardwareFrame(
                _disposed,
                sourceTexturePointer,
                sourceArraySlice,
                width,
                height,
                framesPerSecond,
                NotifyPresented);
        }
    }

    public void SetDxCdgBackgroundFrame(ReadOnlySpan<byte> bgraPixels, int width, int height, int stride, bool forceZoomToFill)
    {
        lock (_lock)
        {
            _presentation.SetDxCdgBackgroundFrame(_disposed, bgraPixels, width, height, stride, forceZoomToFill);
        }
    }

    public void ClearDxCdgBackgroundFrame()
    {
        lock (_lock)
        {
            _presentation.ClearDxCdgBackgroundFrame();
        }
    }

    public bool PresentDxCdgSnapshotVisible(DxCdgRenderSnapshot snapshot, int outputWidth, int outputHeight, bool retainedRefresh = false)
    {
        lock (_lock)
        {
            // DXCDG already notifies/presents the active backend inside the presentation services.
            // The callback passed here must only raise the managed FramePresented event; passing
            // NotifyPresented would cause a second native swap-chain Present with no redraw, which
            // flickers/blanks alternating buffers.
            return _presentation.PresentDxCdgSnapshotVisible(_disposed, snapshot, outputWidth, outputHeight, RaisePresentedEvent, retainedRefresh);
        }
    }

    private void RaisePresentedEvent(int _width, int _height) => RaiseFramePresented();

    private void NotifyPresented(int _width, int _height) => NotifyPresented();

    private void NotifyPresentedWithSync(int _width, int _height, bool syncImmediate) => NotifyPresented(syncImmediate);

    private void NotifyPresented(bool syncImmediate = false)
    {
        _presentation.NotifyFramePresented(_disposed, syncImmediate, RaiseFramePresented);
    }

    private void RaiseFramePresented()
    {
        FramePresented?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (_disposed) return;

        lock (_lock)
        {
            _presentation.CleanupResources();
            _disposed = true;
        }

        if (_configuration.EnableLogging) Debug.WriteLine("[VorticeD3D11] Vortice D3D11 renderer disposed");
    }
}
