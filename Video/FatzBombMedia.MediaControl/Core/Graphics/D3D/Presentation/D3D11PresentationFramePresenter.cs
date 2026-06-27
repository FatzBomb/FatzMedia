using FatzBombMedia.MediaControl.Core.Graphics.D3D.Device;
using FatzBombMedia.MediaControl.Core.Graphics.D3D.Frame;
using FatzBombMedia.MediaControl.Core.Graphics.D3D.Upload;

namespace FatzBombMedia.MediaControl.Core.Graphics.D3D.Presentation;

/// <summary>
/// Owns the active presentation-target handoff for uploaded and hardware frames.
/// The renderer still decides when media frames arrive, but this class now owns the
/// repeated presentation plumbing:
///
///   validate active presentation -> apply pending native resize -> blit/present -> notify
///
/// This keeps VorticeD3D11Renderer from coordinating blitter details directly while
/// preserving the existing frame presenter, layer compositor, and backend behavior.
/// </summary>
internal sealed class D3D11PresentationFramePresenter
{
    private readonly D3D11FramePresenter _framePresenter;
    private readonly D3D11PresentationSurfaceController _presentationSurfaces;
    private readonly D3D11PresentationBlitter _presentationBlitter;
    private readonly D3D11PresentationRenderTargetViewCache _renderTargetViewCache;
    private readonly D3D11PresentationState _presentationState;
    private readonly Func<bool> _hasOverlayTexture;

    public D3D11PresentationFramePresenter(
        D3D11FramePresenter framePresenter,
        D3D11PresentationSurfaceController presentationSurfaces,
        D3D11PresentationBlitter presentationBlitter,
        D3D11PresentationRenderTargetViewCache renderTargetViewCache,
        D3D11PresentationState presentationState,
        Func<bool> hasOverlayTexture)
    {
        _framePresenter = framePresenter;
        _presentationSurfaces = presentationSurfaces;
        _presentationBlitter = presentationBlitter;
        _renderTargetViewCache = renderTargetViewCache;
        _presentationState = presentationState;
        _hasOverlayTexture = hasOverlayTexture;
    }

    public bool UploadCpuFrameWithRetainedLayers(
        D3D11DeviceResources deviceResources,
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
        if (isDisposed || deviceResources.Context == null)
        {
            return false;
        }

        if (!_presentationSurfaces.TryGetActiveTargetSnapshot(
                applyPendingNativeResize: true,
                reason: "retained-layer CPU frame upload",
                out var target))
        {
            return false;
        }

        return _framePresenter.UploadCpuFrameWithRetainedLayers(
            deviceResources,
            static () => true,
            target.Texture,
            _renderTargetViewCache.Current,
            target.Width,
            target.Height,
            pixelData,
            width,
            height,
            stride,
            framesPerSecond,
            washArgb,
            forceBaseUniformToFill,
            requireOverlayGraphics: _hasOverlayTexture(),
            CreateSnapshotBlitCallback(target),
            notifyPresented);
    }

    public void UploadCpuFrame(
        D3D11DeviceResources deviceResources,
        bool isDisposed,
        ReadOnlySpan<byte> pixelData,
        int width,
        int height,
        int stride,
        Action<int, int> notifyPresented)
    {
        if (isDisposed || deviceResources.Context == null)
        {
            return;
        }

        if (!_presentationSurfaces.TryGetActiveTargetSnapshot(
                applyPendingNativeResize: true,
                reason: "CPU frame upload",
                out var target))
        {
            return;
        }

        _framePresenter.UploadCpuFrame(
            deviceResources,
            static () => true,
            target.Texture,
            target.Width,
            target.Height,
            pixelData,
            width,
            height,
            stride,
            framesPerSecond: 60.0,
            baseStretchOverride: null,
            CreateSnapshotBlitCallback(target),
            notifyPresented);
    }

    public bool PresentHardwareFrame(
        D3D11DeviceResources deviceResources,
        bool isDisposed,
        IntPtr sourceTexturePointer,
        int sourceArraySlice,
        int width,
        int height,
        double framesPerSecond,
        Action<int, int> notifyPresented)
    {
        if (isDisposed
            || deviceResources.Context == null
            || !deviceResources.HasVideoProcessorSupport
            || sourceTexturePointer == IntPtr.Zero
            || width <= 0
            || height <= 0)
        {
            return false;
        }

        if (!_presentationSurfaces.TryGetActiveTargetSnapshot(
                applyPendingNativeResize: true,
                reason: "hardware frame presentation",
                out var target))
        {
            return false;
        }

        return _framePresenter.PresentHardwareFrame(
            deviceResources,
            static () => true,
            target.Texture,
            sourceTexturePointer,
            sourceArraySlice,
            width,
            height,
            framesPerSecond,
            target.Width,
            target.Height,
            _presentationState.NativePresentationStretch,
            notifyPresented);
    }


    private D3D11FrameUploadPipeline.BlitToPresentationCallback CreateSnapshotBlitCallback(
        D3D11PresentationTargetSnapshot target)
    {
        return (sourceTexture, sourceArraySlice, sourceWidth, sourceHeight, framesPerSecond, stretchOverride) =>
            _presentationBlitter.BlitToTarget(
                target.Texture,
                target.Width,
                target.Height,
                sourceTexture,
                sourceArraySlice,
                sourceWidth,
                sourceHeight,
                framesPerSecond,
                stretchOverride ?? _presentationState.NativePresentationStretch);
    }
}
