using System.Diagnostics;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace FatzBombMedia.MediaControl.Core.Graphics.Composition.D3D11;

/// <summary>
/// Future offscreen render target owned by a retained layer. Stage 10J introduces
/// lifetime/allocation structure only; current compositor code still draws all
/// layers into the active final target and does not independently present this
/// texture.
/// </summary>
internal sealed class D3D11CompositionLayerRenderTarget : IDisposable
{
    public ID3D11Texture2D? Texture { get; private set; }
    public ID3D11RenderTargetView? RenderTargetView { get; private set; }
    public ID3D11ShaderResourceView? ShaderResourceView { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }
    public Format Format { get; private set; } = Format.B8G8R8A8_UNorm;
    public long Revision { get; private set; } = -1;

    public bool IsAllocated => Texture != null
        && RenderTargetView != null
        && ShaderResourceView != null
        && Width > 0
        && Height > 0;

    public bool Ensure(ID3D11Device device, int width, int height, Format format = Format.B8G8R8A8_UNorm)
    {
        if (width <= 0 || height <= 0)
        {
            Release();
            return false;
        }

        if (IsAllocated && Width == width && Height == height && Format == format)
        {
            return true;
        }

        Release();

        try
        {
            var description = new Texture2DDescription(
                format,
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

            Texture = device.CreateTexture2D(in description);
            RenderTargetView = device.CreateRenderTargetView(Texture);
            ShaderResourceView = device.CreateShaderResourceView(Texture);
            Width = width;
            Height = height;
            Format = format;
            Revision = -1;
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[COMPOSITION-D3D11] Offscreen layer target allocation failed: {ex.GetType().Name}: {ex.Message}");
            Release();
            return false;
        }
    }

    public void MarkRendered(long revision) => Revision = revision;

    public void Release()
    {
        ShaderResourceView?.Dispose();
        ShaderResourceView = null;
        RenderTargetView?.Dispose();
        RenderTargetView = null;
        Texture?.Dispose();
        Texture = null;
        Width = 0;
        Height = 0;
        Revision = -1;
    }

    public void Dispose() => Release();

    public override string ToString()
        => IsAllocated ? $"offscreen={Width}x{Height}/{Format} rev={Revision}" : "offscreen=none";
}
