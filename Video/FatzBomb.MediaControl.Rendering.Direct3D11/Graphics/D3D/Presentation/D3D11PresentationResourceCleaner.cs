using FatzBomb.MediaControl.Core.Graphics.D3D.Device;
using FatzBomb.MediaControl.Core.Graphics.D3D.Video;
using Vortice.Direct3D11;

namespace FatzBomb.MediaControl.Core.Graphics.D3D.Presentation;

/// <summary>
/// Centralizes presentation surface/resource cleanup so the coordinator renderer does not
/// need to know the exact release order for backend textures, render-target views, and upload caches.
/// </summary>
internal sealed class D3D11PresentationResourceCleaner(
    D3D11DeviceResources deviceResources,
    D3D11VideoProcessorPresenter videoProcessorPresenter,
    ID3D11PresentationSurfaceProvider presentationSurfaces)
{
    public void CleanupSurfaces()
    {
        videoProcessorPresenter.Reset();
        ID3D11Texture2D? activeTarget = presentationSurfaces.TargetTexture;
        var backendOwnsPresentationTexture = presentationSurfaces.OwnsTexture(activeTarget);

        presentationSurfaces.CleanupBackends();

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
