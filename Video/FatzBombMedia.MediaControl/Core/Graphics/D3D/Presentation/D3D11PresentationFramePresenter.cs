using FatzBombMedia.MediaControl.Core.Graphics.D3D.Device;
using FatzBombMedia.MediaControl.Core.Graphics.D3D.Frame;
using FatzBombMedia.MediaControl.Core.Graphics.D3D.Upload;
using Vortice.Direct3D11;
using FatzBombMedia.MediaControl.Core.Graphics.Composition;

namespace FatzBombMedia.MediaControl.Core.Graphics.D3D.Presentation;

/// <summary>
/// Owns the active presentation-target handoff for uploaded and hardware frames.
/// The renderer still decides when media frames arrive, but this class now owns the
/// repeated presentation plumbing:
///
///   validate active presentation -> apply pending native resize -> blit/present -> notify
///
/// This keeps VorticeD3D11Renderer from coordinating blitter details directly while
/// preserving the existing frame presenter and backend behavior.
/// </summary>
internal sealed class D3D11PresentationFramePresenter(
    D3D11FramePresenter framePresenter,
    ID3D11PresentationSurfaceProvider presentationSurfaces,
    D3D11PresentationBlitter presentationBlitter,
    D3D11PresentationState presentationState)
{
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

        if (!presentationSurfaces.TryGetActiveTargetSnapshot(
                applyPendingNativeResize: true,
                reason: "CPU frame upload",
                out D3D11PresentationTargetSnapshot target))
        {
            return;
        }

        _ = framePresenter.UploadCpuFrame(
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
        Action<int, int> notifyPresented) =>
        !isDisposed
            && deviceResources.Context != null
            && deviceResources.HasVideoProcessorSupport
            && sourceTexturePointer != IntPtr.Zero
            && width > 0
            && height > 0 && presentationSurfaces.TryGetActiveTargetSnapshot(
                applyPendingNativeResize: true,
                reason: "hardware frame presentation",
                out D3D11PresentationTargetSnapshot target) && framePresenter.PresentHardwareFrame(
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
            presentationState.NativePresentationStretch,
            notifyPresented);

    public bool PresentTextureFrame(
        D3D11DeviceResources deviceResources,
        bool isDisposed,
        ID3D11Texture2D sourceTexture,
        int width,
        int height,
        double framesPerSecond,
        MediaCompositionStretchMode stretch,
        Action<int, int> notifyPresented)
    {
        if (isDisposed
            || deviceResources.Context == null
            || sourceTexture == null
            || width <= 0
            || height <= 0)
        {
            return false;
        }

        if (!presentationSurfaces.TryGetActiveTargetSnapshot(
                applyPendingNativeResize: true,
                reason: "DXCDG texture presentation",
                out D3D11PresentationTargetSnapshot target))
        {
            return false;
        }

        if (!presentationBlitter.BlitToTarget(
                target.Texture,
                target.Width,
                target.Height,
                sourceTexture,
                sourceArraySlice: 0,
                sourceWidth: width,
                sourceHeight: height,
                framesPerSecond,
                stretch))
        {
            return false;
        }

        notifyPresented(width, height);
        return true;
    }


    private D3D11FrameUploadPipeline.BlitToPresentationCallback CreateSnapshotBlitCallback(
        D3D11PresentationTargetSnapshot target) => (sourceTexture, sourceArraySlice, sourceWidth, sourceHeight, framesPerSecond, stretchOverride) =>
                                                            presentationBlitter.BlitToTarget(
                                                                target.Texture,
                                                                target.Width,
                                                                target.Height,
                                                                sourceTexture,
                                                                sourceArraySlice,
                                                                sourceWidth,
                                                                sourceHeight,
                                                                framesPerSecond,
                                                                stretchOverride ?? presentationState.NativePresentationStretch);
}
