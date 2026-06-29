using FatzBombMedia.MediaControl.Core.Graphics.D3D.Device;
using Vortice.Direct3D11;

namespace FatzBombMedia.MediaControl.Core.Graphics.D3D.Presentation;

internal sealed class NullD3D11UiSharedSurfacePresentationBackend : ID3D11UiSharedSurfacePresentationBackend
{
    public D3D11PresentationBackendKind Kind => D3D11PresentationBackendKind.UiSharedSurface;

    public bool IsActive => false;

    public int Width => 0;

    public int Height => 0;

    public ID3D11Texture2D? TargetTexture => null;

    public bool Matches(int width, int height) => false;

    public bool Create(D3D11DeviceResources deviceResources, int width, int height) => false;

    public void NotifyDirty(Func<bool> isDisposed, Action framePresented)
    {
    }

    public void Present(int liveResizeFlag, bool syncImmediate)
    {
    }

    public void Cleanup()
    {
    }

    public void Dispose()
    {
    }
}
