using FatzBombMedia.MediaControl.Core.Graphics.D3D.Device;
using FatzBombMedia.MediaControl.Core.Graphics.D3D.Layers;
using FatzBombMedia.MediaControl.Core.Graphics.D3D.Upload;
using FatzBombMedia.MediaControl.Core.Graphics.D3D.Video;

namespace FatzBombMedia.MediaControl.Core.Graphics.D3D.Presentation;

/// <summary>
/// Centralizes presentation surface/resource cleanup so the coordinator renderer does not
/// need to know the exact release order for backend textures, retained-layer resources,
/// render-target views, and upload caches.
/// </summary>
internal sealed class D3D11PresentationResourceCleaner
{
    private readonly D3D11DeviceResources _deviceResources;
    private readonly D3D11VideoProcessorPresenter _videoProcessorPresenter;
    private readonly D3D11PresentationSurfaceController _presentationSurfaces;
    private readonly D3D11PresentationRenderTargetViewCache _renderTargetViewCache;
    private readonly D3D11LayerCompositor _layerCompositor;
    private readonly D3D11FrameUploadResources _uploadResources;

    public D3D11PresentationResourceCleaner(
        D3D11DeviceResources deviceResources,
        D3D11VideoProcessorPresenter videoProcessorPresenter,
        D3D11PresentationSurfaceController presentationSurfaces,
        D3D11PresentationRenderTargetViewCache renderTargetViewCache,
        D3D11LayerCompositor layerCompositor,
        D3D11FrameUploadResources uploadResources)
    {
        _deviceResources = deviceResources;
        _videoProcessorPresenter = videoProcessorPresenter;
        _presentationSurfaces = presentationSurfaces;
        _renderTargetViewCache = renderTargetViewCache;
        _layerCompositor = layerCompositor;
        _uploadResources = uploadResources;
    }

    public void CleanupSurfaces()
    {
        _videoProcessorPresenter.Reset();
        var activeTarget = _presentationSurfaces.TargetTexture;
        var backendOwnsPresentationTexture = _presentationSurfaces.OwnsTexture(activeTarget);

        _renderTargetViewCache.Release();
        _layerCompositor.Dispose();
        _presentationSurfaces.CleanupBackends();

        _uploadResources.ReleaseCache();
        if (!backendOwnsPresentationTexture)
        {
            activeTarget?.Dispose();
        }

        _presentationSurfaces.Reset();
    }

    public void CleanupResources()
    {
        CleanupSurfaces();
        _videoProcessorPresenter.Reset();
        _deviceResources.Cleanup();
    }
}
