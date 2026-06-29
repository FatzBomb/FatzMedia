using FatzBombMedia.MediaControl.Core.Configuration;
using FatzBombMedia.MediaControl.Core.DxCdg;
using FatzBombMedia.MediaControl.Core.Graphics.Composition;
using FatzBombMedia.MediaControl.Core.Graphics.D3D.Device;
using FatzBombMedia.MediaControl.Core.Graphics.D3D.Diagnostics;
using FatzBombMedia.MediaControl.Core.Graphics.D3D.Frame;
using FatzBombMedia.MediaControl.Core.Graphics.D3D.Presentation.DirectComposition;
using FatzBombMedia.MediaControl.Core.Graphics.D3D.Presentation.NativeHwnd;
using FatzBombMedia.MediaControl.Core.Graphics.D3D.Upload;
using FatzBombMedia.MediaControl.Core.Graphics.D3D.Video;
using System.Diagnostics;

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
        D3D11DeviceResources deviceResources,
        ID3D11UiSharedSurfacePresentationBackend uiSharedSurfaceBackend)
    {
        _configuration = configuration;
        _deviceResources = deviceResources;

        VideoProcessorPresenter = new D3D11VideoProcessorPresenter(configuration);
        FrameUploadPipeline = new D3D11FrameUploadPipeline();
        Diagnostics = new D3D11PresentationDiagnostics(configuration);
        State = new D3D11PresentationState();
        RenderTargetViewCache = new D3D11PresentationRenderTargetViewCache();
        MediaCompositor = new D3D11MediaCompositionPresenter(configuration.EnableVerboseLogging);

        D3D11NativeHwndSwapChainBackend CreateNativeHwndBackend() => new(
            configuration,
            Diagnostics,
            () => VideoProcessorPresenter.Reset(),
            RenderTargetViewCache.Release,
            static () => { });

        var nativeHwndBackend = CreateNativeHwndBackend();
        var directCompositionBackend = new D3D11DirectCompositionSingleTargetBackend(configuration, Diagnostics);

        Backends = new D3D11PresentationBackendSet(nativeHwndBackend, uiSharedSurfaceBackend, directCompositionBackend, CreateNativeHwndBackend);
        Surfaces = new D3D11PresentationSurfaceController(Backends);
        UploadResources = new D3D11FrameUploadResources(Diagnostics, () => Surfaces.Width, () => Surfaces.Height);
        ResourceCleaner = new D3D11PresentationResourceCleaner(
            deviceResources,
            VideoProcessorPresenter,
            Surfaces,
            RenderTargetViewCache,
            UploadResources);

        var framePresenter = new D3D11FramePresenter(
            FrameUploadPipeline,
            UploadResources,
            VideoProcessorPresenter);
        var presentationBlitter = new D3D11PresentationBlitter(
            deviceResources,
            VideoProcessorPresenter,
            Surfaces,
            State);

        FramePresenter = new D3D11PresentationFramePresenter(
            framePresenter,
            Surfaces,
            presentationBlitter,
            State);
    }


    public bool IsReady => _deviceResources.HasDevice && Surfaces.IsReady;

    public bool IsNativeHwndActive => Surfaces.IsNativeHwndActive;

    public bool IsDirectCompositionActive => Surfaces.IsDirectCompositionActive;

    public string PresentationBackend => Surfaces.PresentationBackend;


    public bool IsNativeLiveResizeInProgress => State.IsNativeLiveResizeInProgress;

    public void SetNativeLiveResizeInProgress(bool isResizing) => State.SetNativeLiveResizeInProgress(isResizing);

    public void SetNativePresentationStretch(MediaCompositionStretchMode stretchMode) => State.SetNativePresentationStretch(stretchMode, VideoProcessorPresenter);

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

    public bool CreateUiSharedSurface(bool isDisposed, int width, int height, Action<int, int, bool> notifyPresented) => Surfaces.CreateUiSharedSurface(
            isDisposed,
            _deviceResources,
            width,
            height,
            ResourceCleaner.CleanupSurfaces,
            _configuration.EnableLogging,
            notifyPresented);

    public bool CreateNativeHwndSurface(bool isDisposed, IntPtr hwnd, int width, int height, Action<int, int, bool> notifyPresented) => Surfaces.CreateNativeHwndSurface(
            isDisposed,
            _deviceResources,
            hwnd,
            width,
            height,
            ResourceCleaner.CleanupSurfaces,
            _configuration.EnableLogging,
            notifyPresented);

    public bool CreateSecondaryNativeHwndSurface(bool isDisposed, string targetId, IntPtr hwnd, int width, int height) => Surfaces.CreateSecondaryNativeHwndSurface(
            isDisposed,
            _deviceResources,
            targetId,
            hwnd,
            width,
            height,
            _configuration.EnableLogging);

    public bool RemoveSecondaryPresentationTarget(string targetId) => Surfaces.RemoveSecondaryPresentationTarget(targetId);

    public bool CreateDirectCompositionSurface(bool isDisposed, IntPtr hwnd, int width, int height, Action<int, int, bool> notifyPresented) => Surfaces.CreateDirectCompositionSurface(
            isDisposed,
            _deviceResources,
            hwnd,
            width,
            height,
            ResourceCleaner.CleanupSurfaces,
            _configuration.EnableLogging,
            notifyPresented);

    public void SetDirectCompositionOutputSize(bool isDisposed, int width, int height) => Surfaces.SetDirectCompositionOutputSize(isDisposed, width, height, IsNativeLiveResizeInProgress);

    public bool UploadDirectCompositionOverlayFrame(bool isDisposed, ReadOnlySpan<byte> pixelData, int width, int height, int stride) => Surfaces.UploadDirectCompositionOverlayFrame(
            isDisposed,
            _deviceResources,
            UploadResources,
            VideoProcessorPresenter,
            pixelData,
            width,
            height,
            stride,
            IsNativeLiveResizeInProgress);


    public void ClearPresentedFrame(bool isDisposed, Action raiseFramePresented) => Surfaces.ClearPresentedFrame(
            _deviceResources,
            isDisposed,
            IsNativeLiveResizeInProgress,
            raiseFramePresented);

    public void QueueNativeHwndResize(bool isDisposed, int width, int height) => Surfaces.QueueNativeHwndResize(isDisposed, width, height);

    public bool ResizeNativeHwndPresentation(bool isDisposed, int width, int height) => Surfaces.ResizeNativeHwndPresentation(isDisposed, width, height);

    public bool TryGetActiveTargetSnapshot(bool applyPendingNativeResize, string? reason, out D3D11PresentationTargetSnapshot target) => Surfaces.TryGetActiveTargetSnapshot(applyPendingNativeResize, reason, out target);



    public void UploadCpuFrame(
        bool isDisposed,
        ReadOnlySpan<byte> pixelData,
        int width,
        int height,
        int stride,
        Action<int, int> notifyPresented) => FramePresenter.UploadCpuFrame(
            _deviceResources,
            isDisposed,
            pixelData,
            width,
            height,
            stride,
            notifyPresented);

    public bool PresentHardwareFrame(
        bool isDisposed,
        IntPtr sourceTexturePointer,
        int sourceArraySlice,
        int width,
        int height,
        double framesPerSecond,
        Action<int, int> notifyPresented) => FramePresenter.PresentHardwareFrame(
            _deviceResources,
            isDisposed,
            sourceTexturePointer,
            sourceArraySlice,
            width,
            height,
            framesPerSecond,
            notifyPresented);

    public void NotifyFramePresented(bool isDisposed, bool syncImmediate, Action raiseFramePresented) => Surfaces.NotifyFramePresented(
            IsNativeLiveResizeInProgress,
            syncImmediate,
            () => isDisposed,
            raiseFramePresented);

    public void SetCompositionBackgroundFrame(bool isDisposed, ReadOnlySpan<byte> bgraPixels, int width, int height, int stride, bool forceZoomToFill)
    {
        if (isDisposed || !_deviceResources.HasDevice)
        {
            return;
        }

        MediaCompositor.SetBackgroundFrame(_deviceResources, bgraPixels, width, height, stride, forceZoomToFill);
    }

    public void ClearCompositionBackgroundFrame() => MediaCompositor.ClearBackgroundFrame();

    public bool PresentMediaCompositionVisible(
        bool isDisposed,
        MediaCompositionFrame frame,
        Action<int, int> framePresented)
    {
        if (isDisposed || !_deviceResources.HasDevice)
        {
            if (_configuration.EnableLogging) Debug.WriteLine("[COMPOSITION] Visible present skipped: renderer disposed or D3D11 device unavailable.");
            return false;
        }

        IReadOnlyList<D3D11PresentationTargetSnapshot> targets = Surfaces.GetActiveTargetSnapshots(
            applyPendingNativeResize: true,
            reason: "media composition presentation");
        if (targets.Count == 0)
        {
            if (_configuration.EnableLogging) Debug.WriteLine("[COMPOSITION] Visible present skipped: no active D3D11 presentation target.");
            return false;
        }

        if (!MediaCompositor.PresentToTargets(
                _deviceResources,
                frame,
                targets,
                VideoProcessorPresenter,
                out IReadOnlyList<D3D11PresentationTargetSnapshot> presentedTargets))
        {
            if (_configuration.EnableLogging) Debug.WriteLine($"[COMPOSITION] Visible present failed in media compositor reason={frame.Reason}.");
            return false;
        }

        Surfaces.NotifyTargetsPresented(
            presentedTargets,
            State.IsNativeLiveResizeInProgress,
            syncImmediate: true,
            () => isDisposed,
            target => framePresented(target.Width, target.Height));

        return true;
    }

    public bool PresentDxCdgSnapshotVisible(
        bool isDisposed,
        DxCdgRenderSnapshot snapshot,
        Action<int, int> framePresented,
        bool retainedRefresh = false)
    {
        if (isDisposed || !_deviceResources.HasDevice)
        {
            if (_configuration.EnableLogging) Debug.WriteLine("[DXCDG] Visible present skipped: renderer disposed or D3D11 device unavailable.");
            return false;
        }

        IReadOnlyList<D3D11PresentationTargetSnapshot> targets = Surfaces.GetActiveTargetSnapshots(
            applyPendingNativeResize: true,
            reason: "DXCDG active-target presentation");
        if (targets.Count == 0)
        {
            if (_configuration.EnableLogging) Debug.WriteLine("[DXCDG] Visible present skipped: no active D3D11 presentation target.");
            return false;
        }

        if (!MediaCompositor.PresentToTargets(
                _deviceResources,
                new MediaCompositionFrame
                {
                    ClearMode = MediaCompositionClearMode.Black,
                    ClearArgb = 0xFF000000,
                    WashArgb = snapshot.BackgroundWashArgb,
                    CdgOverlay = snapshot,
                    RetainedRefresh = retainedRefresh,
                    Reason = retainedRefresh ? "retained-dxcdg" : "dxcdg"
                },
                targets,
                VideoProcessorPresenter,
                out IReadOnlyList<D3D11PresentationTargetSnapshot> presentedTargets))
        {
            if (_configuration.EnableLogging) Debug.WriteLine($"[DXCDG] Visible present failed in DXCDG presenter for revision={snapshot.Revision}.");
            return false;
        }

        Surfaces.NotifyTargetsPresented(
            presentedTargets,
            State.IsNativeLiveResizeInProgress,
            syncImmediate: true,
            () => isDisposed,
            target => framePresented(target.Width, target.Height));

        return true;
    }

    public void CleanupResources()
    {
        MediaCompositor.Reset();
        ResourceCleaner.CleanupResources();
    }

    public D3D11VideoProcessorPresenter VideoProcessorPresenter { get; }

    public D3D11FrameUploadPipeline FrameUploadPipeline { get; }

    public D3D11PresentationDiagnostics Diagnostics { get; }

    public D3D11PresentationBackendSet Backends { get; }

    public D3D11PresentationSurfaceController Surfaces { get; }

    public D3D11PresentationState State { get; }

    public D3D11PresentationRenderTargetViewCache RenderTargetViewCache { get; }


    public D3D11MediaCompositionPresenter MediaCompositor { get; }

    public D3D11FrameUploadResources UploadResources { get; }

    public D3D11PresentationResourceCleaner ResourceCleaner { get; }

    public D3D11PresentationFramePresenter FramePresenter { get; }
}
