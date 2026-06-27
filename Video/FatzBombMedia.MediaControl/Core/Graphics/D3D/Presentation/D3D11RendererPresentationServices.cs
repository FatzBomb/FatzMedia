using FatzBombMedia.MediaControl.Core.Configuration;
using FatzBombMedia.MediaControl.Core.Graphics.D3D.Device;
using FatzBombMedia.MediaControl.Core.Graphics.D3D.Diagnostics;
using FatzBombMedia.MediaControl.Core.Graphics.D3D.Frame;
using FatzBombMedia.MediaControl.Core.Graphics.D3D.Layers;
using FatzBombMedia.MediaControl.Core.Graphics.D3D.Presentation.D3DImage;
using FatzBombMedia.MediaControl.Core.Graphics.D3D.Presentation.DirectComposition;
using FatzBombMedia.MediaControl.Core.Graphics.D3D.Presentation.NativeHwnd;
using FatzBombMedia.MediaControl.Core.Graphics.D3D.Upload;
using FatzBombMedia.MediaControl.Core.Graphics.D3D.Video;
using System.Windows.Media;

namespace FatzBombMedia.MediaControl.Core.Graphics.D3D.Presentation;

/// <summary>
/// Builds and owns the presentation-side services used by <see cref="VorticeD3D11Renderer" />.
/// Keeping this object graph out of the renderer makes the renderer a small coordinator while
/// preserving the existing backend, upload, compositor, and presenter lifetimes.
/// </summary>
internal sealed class D3D11RendererPresentationServices
{
    private readonly MediaPlayerConfiguration _configuration;
    private readonly D3D11DeviceResources _deviceResources;

    public D3D11RendererPresentationServices(
        MediaPlayerConfiguration configuration,
        D3D11DeviceResources deviceResources)
    {
        _configuration = configuration;
        _deviceResources = deviceResources;

        VideoProcessorPresenter = new D3D11VideoProcessorPresenter(configuration);
        FrameUploadPipeline = new D3D11FrameUploadPipeline();
        Diagnostics = new D3D11PresentationDiagnostics(configuration);
        State = new D3D11PresentationState();
        RenderTargetViewCache = new D3D11PresentationRenderTargetViewCache();
        LayerCompositor = new D3D11LayerCompositor(configuration);

        var nativeHwndBackend = new D3D11NativeHwndSwapChainBackend(
            configuration,
            Diagnostics,
            () => VideoProcessorPresenter.Reset(),
            RenderTargetViewCache.Release,
            () => LayerCompositor.ResetTransientOverlayFailure());
        var d3dImageBackend = new D3D11D3DImagePresentationBackend();
        var directCompositionBackend = new D3D11DirectCompositionSingleTargetBackend(configuration, Diagnostics);

        Backends = new D3D11PresentationBackendSet(nativeHwndBackend, d3dImageBackend, directCompositionBackend);
        Surfaces = new D3D11PresentationSurfaceController(Backends);
        UploadResources = new D3D11FrameUploadResources(Diagnostics, () => Surfaces.Width, () => Surfaces.Height);
        ResourceCleaner = new D3D11PresentationResourceCleaner(
            deviceResources,
            VideoProcessorPresenter,
            Surfaces,
            RenderTargetViewCache,
            LayerCompositor,
            UploadResources);

        var framePresenter = new D3D11FramePresenter(
            FrameUploadPipeline,
            UploadResources,
            VideoProcessorPresenter,
            LayerCompositor);
        var presentationBlitter = new D3D11PresentationBlitter(
            deviceResources,
            VideoProcessorPresenter,
            Surfaces,
            State);

        FramePresenter = new D3D11PresentationFramePresenter(
            framePresenter,
            Surfaces,
            presentationBlitter,
            RenderTargetViewCache,
            State,
            () => LayerCompositor.HasOverlayTexture);
    }



    public ImageSource? ImageSource => Surfaces.ImageSource;

    public bool IsReady => _deviceResources.HasDevice && Surfaces.IsReady;

    public bool IsNativeHwndActive => Surfaces.IsNativeHwndActive;

    public bool IsDirectCompositionActive => Surfaces.IsDirectCompositionActive;

    public string PresentationBackend => Surfaces.PresentationBackend;

    public bool HasOverlayTexture => LayerCompositor.HasOverlayTexture;

    public bool IsNativeLiveResizeInProgress => State.IsNativeLiveResizeInProgress;

    public void SetNativeLiveResizeInProgress(bool isResizing)
    {
        State.SetNativeLiveResizeInProgress(isResizing);
    }

    public void SetNativePresentationStretch(Stretch stretch)
    {
        State.SetNativePresentationStretch(stretch, VideoProcessorPresenter);
    }

    public bool InitializeDevice(bool isDisposed)
    {
        if (isDisposed)
        {
            return false;
        }

        var initialized = _deviceResources.Initialize(_configuration);
        if (!initialized)
        {
            ResourceCleaner.CleanupResources();
        }

        return initialized;
    }

    public bool InitializeFromNativeDevice(bool isDisposed, IntPtr devicePointer, IntPtr deviceContextPointer)
    {
        if (isDisposed || devicePointer == IntPtr.Zero)
        {
            return false;
        }

        ResourceCleaner.CleanupResources();
        return _deviceResources.InitializeFromNativeDevice(devicePointer, deviceContextPointer, _configuration);
    }

    public bool CreateD3DImageSurface(bool isDisposed, int width, int height, Action<int, int, bool> notifyPresented)
    {
        return Surfaces.CreateD3DImageSurface(
            isDisposed,
            _deviceResources,
            width,
            height,
            ResourceCleaner.CleanupSurfaces,
            _configuration.EnableLogging,
            notifyPresented);
    }

    public bool CreateNativeHwndSurface(bool isDisposed, IntPtr hwnd, int width, int height, Action<int, int, bool> notifyPresented)
    {
        return Surfaces.CreateNativeHwndSurface(
            isDisposed,
            _deviceResources,
            hwnd,
            width,
            height,
            ResourceCleaner.CleanupSurfaces,
            _configuration.EnableLogging,
            notifyPresented);
    }

    public bool CreateDirectCompositionSurface(bool isDisposed, IntPtr hwnd, int width, int height, Action<int, int, bool> notifyPresented)
    {
        return Surfaces.CreateDirectCompositionSurface(
            isDisposed,
            _deviceResources,
            hwnd,
            width,
            height,
            ResourceCleaner.CleanupSurfaces,
            _configuration.EnableLogging,
            notifyPresented);
    }

    public void SetDirectCompositionOutputSize(bool isDisposed, int width, int height)
    {
        Surfaces.SetDirectCompositionOutputSize(isDisposed, width, height, IsNativeLiveResizeInProgress);
    }

    public bool UploadDirectCompositionOverlayFrame(bool isDisposed, ReadOnlySpan<byte> pixelData, int width, int height, int stride)
    {
        return Surfaces.UploadDirectCompositionOverlayFrame(
            isDisposed,
            _deviceResources,
            UploadResources,
            VideoProcessorPresenter,
            pixelData,
            width,
            height,
            stride,
            IsNativeLiveResizeInProgress);
    }

    public bool UploadOverlayTexture(bool isDisposed, ReadOnlySpan<byte> pixelData, int width, int height, int stride)
    {
        if (isDisposed || !_deviceResources.HasDevice)
        {
            return false;
        }

        return LayerCompositor.UploadOverlayTexture(_deviceResources, pixelData, width, height, stride);
    }

    public void ClearOverlayTexture()
    {
        LayerCompositor.ClearOverlayTexture();
    }

    public void ClearPresentedFrame(bool isDisposed, Action raiseFramePresented)
    {
        Surfaces.ClearPresentedFrame(
            _deviceResources,
            isDisposed,
            IsNativeLiveResizeInProgress,
            raiseFramePresented);
    }

    public void QueueNativeHwndResize(bool isDisposed, int width, int height)
    {
        Surfaces.QueueNativeHwndResize(isDisposed, width, height);
    }

    public bool ResizeNativeHwndPresentation(bool isDisposed, int width, int height)
    {
        return Surfaces.ResizeNativeHwndPresentation(isDisposed, width, height);
    }

    public bool TryGetActiveTargetSnapshot(bool applyPendingNativeResize, string? reason, out D3D11PresentationTargetSnapshot target)
    {
        return Surfaces.TryGetActiveTargetSnapshot(applyPendingNativeResize, reason, out target);
    }



    public bool UploadCpuFrameWithRetainedLayers(
        bool isDisposed,
        ReadOnlySpan<byte> pixelData,
        int width,
        int height,
        int stride,
        double framesPerSecond,
        uint washArgb,
        bool forceBaseUniformToFill,
        Action<int, int> notifyPresented)
    {
        return FramePresenter.UploadCpuFrameWithRetainedLayers(
            _deviceResources,
            isDisposed,
            pixelData,
            width,
            height,
            stride,
            framesPerSecond,
            washArgb,
            forceBaseUniformToFill,
            notifyPresented);
    }

    public void UploadCpuFrame(
        bool isDisposed,
        ReadOnlySpan<byte> pixelData,
        int width,
        int height,
        int stride,
        Action<int, int> notifyPresented)
    {
        FramePresenter.UploadCpuFrame(
            _deviceResources,
            isDisposed,
            pixelData,
            width,
            height,
            stride,
            notifyPresented);
    }

    public bool PresentHardwareFrame(
        bool isDisposed,
        IntPtr sourceTexturePointer,
        int sourceArraySlice,
        int width,
        int height,
        double framesPerSecond,
        Action<int, int> notifyPresented)
    {
        return FramePresenter.PresentHardwareFrame(
            _deviceResources,
            isDisposed,
            sourceTexturePointer,
            sourceArraySlice,
            width,
            height,
            framesPerSecond,
            notifyPresented);
    }

    public void NotifyFramePresented(bool isDisposed, bool syncImmediate, Action raiseFramePresented)
    {
        Surfaces.NotifyFramePresented(
            IsNativeLiveResizeInProgress,
            syncImmediate,
            () => isDisposed,
            raiseFramePresented);
    }

    public void CleanupResources()
    {
        ResourceCleaner.CleanupResources();
    }

    public D3D11VideoProcessorPresenter VideoProcessorPresenter { get; }

    public D3D11FrameUploadPipeline FrameUploadPipeline { get; }

    public D3D11PresentationDiagnostics Diagnostics { get; }

    public D3D11PresentationBackendSet Backends { get; }

    public D3D11PresentationSurfaceController Surfaces { get; }

    public D3D11PresentationState State { get; }

    public D3D11PresentationRenderTargetViewCache RenderTargetViewCache { get; }

    public D3D11LayerCompositor LayerCompositor { get; }

    public D3D11FrameUploadResources UploadResources { get; }

    public D3D11PresentationResourceCleaner ResourceCleaner { get; }

    public D3D11PresentationFramePresenter FramePresenter { get; }
}
