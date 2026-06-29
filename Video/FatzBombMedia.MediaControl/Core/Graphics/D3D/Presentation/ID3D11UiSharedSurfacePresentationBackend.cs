using FatzBombMedia.MediaControl.Core.Graphics.D3D.Device;

namespace FatzBombMedia.MediaControl.Core.Graphics.D3D.Presentation;

/// <summary>
/// Framework adapter supplied presentation backend for UI shared-surface interop.
/// Core owns the compositor and presentation policy; adapters implement this bridge
/// only when their UI framework requires a shared surface such as a framework shared-surface bridge.
/// </summary>
internal interface ID3D11UiSharedSurfacePresentationBackend : ID3D11PresentationBackend
{
    bool Matches(int width, int height);

    bool Create(D3D11DeviceResources deviceResources, int width, int height);

    void NotifyDirty(Func<bool> isDisposed, Action framePresented);
}
