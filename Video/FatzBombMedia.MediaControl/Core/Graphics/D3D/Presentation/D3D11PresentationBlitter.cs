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
internal sealed class D3D11PresentationBlitter
{
    private readonly D3D11DeviceResources _deviceResources;
    private readonly D3D11VideoProcessorPresenter _videoProcessorPresenter;
    private readonly D3D11PresentationSurfaceController _presentationSurfaces;
    private readonly D3D11PresentationState _presentationState;

    public D3D11PresentationBlitter(
        D3D11DeviceResources deviceResources,
        D3D11VideoProcessorPresenter videoProcessorPresenter,
        D3D11PresentationSurfaceController presentationSurfaces,
        D3D11PresentationState presentationState)
    {
        _deviceResources = deviceResources;
        _videoProcessorPresenter = videoProcessorPresenter;
        _presentationSurfaces = presentationSurfaces;
        _presentationState = presentationState;
    }

    public bool BlitToActivePresentation(
        ID3D11Texture2D sourceTexture,
        int sourceArraySlice,
        int width,
        int height,
        double framesPerSecond,
        Stretch? stretchOverride = null)
    {
        if (_deviceResources.Context == null)
        {
            return false;
        }

        if (!_presentationSurfaces.TryGetActiveTargetSnapshot(
                applyPendingNativeResize: true,
                reason: "active presentation blit",
                out var target))
        {
            return false;
        }

        return BlitToTarget(
            target.Texture,
            targetWidth: target.Width,
            targetHeight: target.Height,
            sourceTexture,
            sourceArraySlice,
            width,
            height,
            framesPerSecond,
            stretchOverride ?? _presentationState.NativePresentationStretch);
    }

    public bool BlitToTarget(
        ID3D11Texture2D targetTexture,
        int targetWidth,
        int targetHeight,
        ID3D11Texture2D sourceTexture,
        int sourceArraySlice,
        int sourceWidth,
        int sourceHeight,
        double framesPerSecond,
        Stretch stretch)
    {
        if (_deviceResources.Context == null || targetTexture == null || targetWidth <= 0 || targetHeight <= 0)
        {
            return false;
        }

        return _videoProcessorPresenter.BlitTextureToPresentation(
            _deviceResources,
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
}
