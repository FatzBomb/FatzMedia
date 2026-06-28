using System.Windows.Media;
using FatzBombMedia.MediaControl.Core.Graphics.D3D.Device;
using FatzBombMedia.MediaControl.Core.Graphics.D3D.Video;
using Vortice.Direct3D11;

namespace FatzBombMedia.MediaControl.Core.Graphics.D3D.Presentation;

/// <summary>
/// Owns the small presentation-target blit bridge between frame upload/composition and
/// the D3D11 video-processor presenter. The renderer decides when frames should be
/// presented; this class owns how a source texture is copied into the active target.
/// </summary>
internal sealed class D3D11PresentationBlitter(
    D3D11DeviceResources deviceResources,
    D3D11VideoProcessorPresenter videoProcessorPresenter,
    D3D11PresentationSurfaceController presentationSurfaces,
    D3D11PresentationState presentationState)
{
    public bool BlitToActivePresentation(
        ID3D11Texture2D sourceTexture,
        int sourceArraySlice,
        int width,
        int height,
        double framesPerSecond,
        Stretch? stretchOverride = null) => deviceResources.Context != null && presentationSurfaces.TryGetActiveTargetSnapshot(
                applyPendingNativeResize: true,
                reason: "active presentation blit",
                out D3D11PresentationTargetSnapshot target) && BlitToTarget(
            target.Texture,
            targetWidth: target.Width,
            targetHeight: target.Height,
            sourceTexture,
            sourceArraySlice,
            width,
            height,
            framesPerSecond,
            stretchOverride ?? presentationState.NativePresentationStretch);

    public bool BlitToTarget(
        ID3D11Texture2D targetTexture,
        int targetWidth,
        int targetHeight,
        ID3D11Texture2D sourceTexture,
        int sourceArraySlice,
        int sourceWidth,
        int sourceHeight,
        double framesPerSecond,
        Stretch stretch) =>
        deviceResources.Context != null && targetTexture != null && targetWidth > 0 && targetHeight > 0 && videoProcessorPresenter.BlitTextureToPresentation(
            deviceResources,
            targetTexture,
            sourceTexture,
            sourceArraySlice,
            sourceWidth,
            sourceHeight,
            framesPerSecond,
            targetWidth,
            targetHeight,
            stretch);
}
