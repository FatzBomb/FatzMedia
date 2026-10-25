using Vortice.Direct3D11;

namespace FatzBomb.MediaControl.Core.Graphics.D3D.Presentation;

/// <summary>
/// The render-target view of one presentation texture, kept while that texture lives instead of
/// being created for every target of every frame (ENG-026). The owner of the texture holds one
/// and calls <see cref="Release"/> before it disposes the texture or resizes the swap chain that
/// owns it: the view holds a reference that <c>ResizeBuffers</c> requires to be gone. A swap
/// chain's buffer 0 keeps its view across presents (Direct3D 11 renames the buffers itself).
/// </summary>
internal sealed class D3D11TargetViewCache : IDisposable
{
    private ID3D11Device? _device;
    private ID3D11Texture2D? _texture;
    private ID3D11RenderTargetView? _view;

    /// <summary>Gets the cached view, or null (tests).</summary>
    internal ID3D11RenderTargetView? CurrentView => _view;

    /// <summary>Gets the view of <paramref name="texture"/>, creating it when the texture or device changed.</summary>
    public ID3D11RenderTargetView Get(ID3D11Device device, ID3D11Texture2D texture)
    {
        if (_view != null && ReferenceEquals(_texture, texture) && ReferenceEquals(_device, device))
            return _view;

        Release();
        ID3D11RenderTargetView view = device.CreateRenderTargetView(texture);
        _view = view;
        _texture = texture;
        _device = device;
        return view;
    }

    /// <summary>Releases the view (before its texture is disposed or its swap chain resized).</summary>
    public void Release()
    {
        _view?.Dispose();
        _view = null;
        _texture = null;
        _device = null;
    }

    public void Dispose() => Release();
}
