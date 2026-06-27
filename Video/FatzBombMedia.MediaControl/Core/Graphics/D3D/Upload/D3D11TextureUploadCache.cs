using Vortice.Direct3D11;
using Vortice.DXGI;

namespace FatzBombMedia.MediaControl.Core.Graphics.D3D.Upload;

/// <summary>
/// Retains a reusable CPU-upload staging texture for BGRA frames that must be
/// scaled/cropped into the presentation target before presenting.  Keeping this
/// cache out of the coordinator renderer prevents source-frame upload lifetime
/// from being mixed with swap-chain and layer-composition state.
/// </summary>
internal sealed class D3D11TextureUploadCache : IDisposable
{
    private ID3D11Texture2D? _texture;
    private int _width;
    private int _height;

    public ID3D11Texture2D? Texture => _texture;

    public bool Ensure(ID3D11Device device, int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            return false;
        }

        if (_texture != null && _width == width && _height == height)
        {
            return true;
        }

        Dispose();

        var textureDescription = new Texture2DDescription(
            Format.B8G8R8A8_UNorm,
            (uint)width,
            (uint)height,
            arraySize: 1,
            mipLevels: 1,
            BindFlags.RenderTarget | BindFlags.ShaderResource,
            ResourceUsage.Default,
            CpuAccessFlags.None,
            sampleCount: 1,
            sampleQuality: 0,
            ResourceOptionFlags.None);

        _texture = device.CreateTexture2D(in textureDescription);
        _width = width;
        _height = height;
        return _texture != null;
    }

    public void Dispose()
    {
        _texture?.Dispose();
        _texture = null;
        _width = 0;
        _height = 0;
    }
}
