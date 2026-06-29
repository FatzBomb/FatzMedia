using FatzBombMedia.MediaControl.Core.Graphics.D3D.Device;
using FatzBombMedia.MediaControl.Core.Graphics.D3D.Upload;
using FatzBombMedia.MediaControl.Core.Graphics.D3D.Video;
using Vortice.Direct3D11;

namespace FatzBombMedia.MediaControl.Core.Graphics.D3D.Presentation;

/// <summary>
/// Centralizes presentation surface/resource cleanup so the coordinator renderer does not
/// need to know the exact release order for backend textures, render-target views, and upload caches.
/// </summary>
internal sealed class D3D11PresentationResourceCleaner(
    D3D11DeviceResources deviceResources,
    D3D11VideoProcessorPresenter videoProcessorPresenter,
    ID3D11PresentationSurfaceProvider presentationSurfaces,
    D3D11PresentationRenderTargetViewCache renderTargetViewCache,
    D3D11FrameUploadResources uploadResources)
{
    public void CleanupSurfaces()
    {
        videoProcessorPresenter.Reset();
        ID3D11Texture2D? activeTarget = presentationSurfaces.TargetTexture;
        var backendOwnsPresentationTexture = presentationSurfaces.OwnsTexture(activeTarget);

        renderTargetViewCache.Release();
        presentationSurfaces.CleanupBackends();

        uploadResources.ReleaseCache();
        if (!backendOwnsPresentationTexture)
        {
            activeTarget?.Dispose();
        }

        presentationSurfaces.Reset();
    }

    public void CleanupResources()
    {
        CleanupSurfaces();
        videoProcessorPresenter.Reset();
        deviceResources.Cleanup();
    }
}
