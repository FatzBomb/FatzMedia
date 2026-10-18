using FatzBomb.MediaControl.Core.Graphics.D3D.Device;
using FatzBomb.MediaControl.Core.Graphics.D3D.Frame;

namespace FatzBomb.MediaControl.Core.Graphics.D3D.Presentation;

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
    D3D11PresentationState presentationState)
{
    public bool PresentHardwareFrame(
        D3D11DeviceResources deviceResources,
        bool isDisposed,
        IntPtr sourceTexturePointer,
        int sourceArraySlice,
        int width,
        int height,
        double framesPerSecond,
        VideoColorSpace colorSpace,
        Func<int, int, bool> notifyPresented) =>
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
            colorSpace,
            target.Width,
            target.Height,
            presentationState.NativePresentationStretch,
            notifyPresented);
}
