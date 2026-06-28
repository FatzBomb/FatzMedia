using Vortice.Direct3D11;

namespace FatzBombMedia.MediaControl.Core.Graphics.D3D.Presentation;

/// <summary>
/// Holds the optional caller-owned render target view used by retained-layer composition.
/// The current pipeline normally composites through transient views, so this cache mostly
/// centralizes lifetime/release behavior that previously lived directly in the renderer.
/// </summary>
internal sealed class D3D11PresentationRenderTargetViewCache : IDisposable
{
    public ID3D11RenderTargetView? Current { get; private set; }

    public void Release()
    {
        Current?.Dispose();
        Current = null;
    }

    public void Dispose() => Release();
}
