using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using FatzBombMedia.MediaControl.Core.Configuration;
using FatzBombMedia.MediaControl.Core.Graphics.Geometry;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace FatzBombMedia.MediaControl.Core.Graphics.D3D.Layers;

/// <summary>
/// Owns the D3D11 resources for foreground overlay layers.
/// Layer 2 is a cached full-viewport wash color; Layer 3 is a retained native-size
/// transparent-background overlay texture. CDG is the current producer, but the
/// renderer policy is generic: wash fills the viewport and overlay graphics Uniform-fit.
/// </summary>
internal sealed class D3D11OverlayLayerRenderer : IDisposable
{
    private readonly MediaPlayerConfiguration _configuration;

    private ID3D11Texture2D? _lyricsTexture;
    private ID3D11ShaderResourceView? _lyricsShaderResourceView;
    private ID3D11VertexShader? _vertexShader;
    private ID3D11PixelShader? _pixelShader;
    private ID3D11Texture2D? _washTexture;
    private ID3D11ShaderResourceView? _washShaderResourceView;
    private ID3D11Buffer? _constantBuffer;
    private ID3D11SamplerState? _samplerState;
    private ID3D11BlendState? _blendState;
    private bool _pipelineFailed;
    private int _lyricsWidth;
    private int _lyricsHeight;
    private int _lyricsStride;

    public D3D11OverlayLayerRenderer(MediaPlayerConfiguration configuration)
    {
        _configuration = configuration;
    }

    public bool HasLyricsTexture => _lyricsTexture != null && _lyricsShaderResourceView != null;

    public bool UploadLyricsTexture(ID3D11Device device, ID3D11DeviceContext context, ReadOnlySpan<byte> pixelData, int width, int height, int stride)
    {
        if (width <= 0 || height <= 0 || stride < width * 4 || pixelData.IsEmpty)
        {
            return false;
        }

        try
        {
            if (_lyricsTexture == null
                || _lyricsShaderResourceView == null
                || _lyricsWidth != width
                || _lyricsHeight != height
                || _lyricsStride != stride)
            {
                _lyricsShaderResourceView?.Dispose();
                _lyricsShaderResourceView = null;
                _lyricsTexture?.Dispose();
                _lyricsTexture = null;

                var textureDescription = new Texture2DDescription(
                    Format.B8G8R8A8_UNorm,
                    (uint)width,
                    (uint)height,
                    arraySize: 1,
                    mipLevels: 1,
                    BindFlags.ShaderResource,
                    ResourceUsage.Default,
                    CpuAccessFlags.None,
                    sampleCount: 1,
                    sampleQuality: 0,
                    ResourceOptionFlags.None);

                _lyricsTexture = device.CreateTexture2D(in textureDescription);
                _lyricsShaderResourceView = device.CreateShaderResourceView(_lyricsTexture);
                _lyricsWidth = width;
                _lyricsHeight = height;
                _lyricsStride = stride;
                _pipelineFailed = false;
            }

            context.UpdateSubresource(pixelData, _lyricsTexture, 0, (uint)stride, 0, null);
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[D3D11OverlayLayerRenderer] Lyrics texture upload failed: {ex.Message}");
            ClearLyricsTexture();
            return false;
        }
    }

    public void ClearLyricsTexture()
    {
        _lyricsShaderResourceView?.Dispose();
        _lyricsShaderResourceView = null;
        _lyricsTexture?.Dispose();
        _lyricsTexture = null;
        _lyricsWidth = 0;
        _lyricsHeight = 0;
        _lyricsStride = 0;
        _pipelineFailed = false;
    }

    public void ResetTransientFailure() => _pipelineFailed = false;

    public bool Draw(
        ID3D11Device device,
        ID3D11DeviceContext context,
        ID3D11RenderTargetView presentationRenderTargetView,
        int surfaceWidth,
        int surfaceHeight,
        uint washArgb)
    {
        if (_lyricsShaderResourceView == null || _lyricsWidth <= 0 || _lyricsHeight <= 0)
        {
            return false;
        }

        if (surfaceWidth <= 0 || surfaceHeight <= 0)
        {
            return false;
        }

        try
        {
            if (!EnsurePipeline(device))
            {
                return false;
            }

            // Overlay policy is fixed: the wash covers the full control and overlay
            // graphics are Uniform-fitted inside that same full viewport.  Primary
            // video display modes (Original/Zoom/Stretch/ZoomToFill) must not affect
            // foreground overlay layers.
            var outputRect = new Vortice.RawRect(0, 0, Math.Max(1, surfaceWidth), Math.Max(1, surfaceHeight));

            var outputW = Math.Max(1, outputRect.Right - outputRect.Left);
            var outputH = Math.Max(1, outputRect.Bottom - outputRect.Top);
            var cdgRect = ViewportGeometry.UniformFit(_lyricsWidth, _lyricsHeight, outputW, outputH);
            var cdgLeft = outputRect.Left + (int)Math.Round(cdgRect.Left);
            var cdgTop = outputRect.Top + (int)Math.Round(cdgRect.Top);
            var cdgRight = cdgLeft + Math.Max(1, (int)Math.Round(cdgRect.Width));
            var cdgBottom = cdgTop + Math.Max(1, (int)Math.Round(cdgRect.Height));

            var ta = (int)((washArgb >> 24) & 0xFF);
            var tr = (int)((washArgb >> 16) & 0xFF);
            var tg = (int)((washArgb >> 8) & 0xFF);
            var tb = (int)(washArgb & 0xFF);

            if (_configuration.EnableVerboseLogging)
            {
                Debug.WriteLine($"[D3D11OverlayLayerRenderer] Draw wash=0x{washArgb:X8}, output=({outputRect.Left},{outputRect.Top},{outputRect.Right},{outputRect.Bottom}), cdg=({cdgLeft},{cdgTop},{cdgRight},{cdgBottom}), surface={surfaceWidth}x{surfaceHeight}");
            }

            context.IASetPrimitiveTopology(PrimitiveTopology.TriangleStrip);
            context.IASetInputLayout(null);
            context.VSSetShader(_vertexShader);
            context.VSSetConstantBuffer(0, _constantBuffer);
            context.RSSetViewport(new Viewport(0, 0, surfaceWidth, surfaceHeight));
            context.OMSetRenderTargets(presentationRenderTargetView);

            Span<byte> constantBytes = stackalloc byte[Marshal.SizeOf<OverlayLayerShaderConstants>()];

            // Layer 2: full-viewport color wash. This is intentionally not clipped
            // to the overlay graphics rect; the overlay texture has a transparent background.
            if (ta > 0 && _washTexture != null && _washShaderResourceView != null)
            {
                Span<byte> washPixel = stackalloc byte[4] { (byte)tb, (byte)tg, (byte)tr, (byte)ta };
                context.UpdateSubresource(washPixel, _washTexture, 0, 4, 0, null);

                DrawTexturedQuad(
                    context,
                    _washShaderResourceView,
                    constantBytes,
                    new Vortice.RawRect(0, 0, surfaceWidth, surfaceHeight),
                    surfaceWidth,
                    surfaceHeight);
            }

            // Layer 3: native-aspect overlay graphics Uniform-fitted inside the full viewport.
            DrawTexturedQuad(
                context,
                _lyricsShaderResourceView,
                constantBytes,
                new Vortice.RawRect(cdgLeft, cdgTop, cdgRight, cdgBottom),
                surfaceWidth,
                surfaceHeight);

            context.PSSetShaderResource(0, null);
            context.OMSetRenderTargets((ID3D11RenderTargetView?)null);
            return true;
        }
        catch (Exception ex)
        {
            if (_configuration.EnableLogging)
            {
                Debug.WriteLine($"[D3D11OverlayLayerRenderer] Draw failed: {ex.Message}");
            }
            return false;
        }
    }

    private void DrawTexturedQuad(
        ID3D11DeviceContext context,
        ID3D11ShaderResourceView shaderResourceView,
        Span<byte> constantBytes,
        Vortice.RawRect destinationRect,
        int surfaceWidth,
        int surfaceHeight)
    {
        var constants = new OverlayLayerShaderConstants
        {
            DestinationRect = new Vector4(
                (float)((destinationRect.Left / (double)surfaceWidth) * 2.0 - 1.0),
                (float)(1.0 - (destinationRect.Top / (double)surfaceHeight) * 2.0),
                (float)((destinationRect.Right / (double)surfaceWidth) * 2.0 - 1.0),
                (float)(1.0 - (destinationRect.Bottom / (double)surfaceHeight) * 2.0)),
            TextureRect = new Vector4(0f, 0f, 1f, 1f)
        };

        MemoryMarshal.Write(constantBytes, in constants);
        context.UpdateSubresource(constantBytes, _constantBuffer, 0, 0, 0, null);
        context.PSSetShaderResource(0, shaderResourceView);
        context.PSSetSampler(0, _samplerState);
        context.PSSetShader(_pixelShader);
        context.OMSetBlendState(_blendState);
        context.Draw(4, 0);
    }

    private bool EnsurePipeline(ID3D11Device device)
    {
        if (_pipelineFailed)
        {
            return false;
        }

        try
        {
            if (_vertexShader == null || _pixelShader == null)
            {
                var vertexShaderBytecode = CompileShaderBytecode(OverlayLayerShaderSource, "VSMain", "vs_5_0");
                var pixelShaderBytecode = CompileShaderBytecode(OverlayLayerShaderSource, "PSMain", "ps_5_0");
                _vertexShader = device.CreateVertexShader(vertexShaderBytecode);
                _pixelShader = device.CreatePixelShader(pixelShaderBytecode);
            }

            if (_constantBuffer == null)
            {
                var constantBufferDescription = new BufferDescription
                {
                    ByteWidth = (uint)Marshal.SizeOf<OverlayLayerShaderConstants>(),
                    Usage = ResourceUsage.Default,
                    BindFlags = BindFlags.ConstantBuffer,
                    CPUAccessFlags = CpuAccessFlags.None,
                    MiscFlags = ResourceOptionFlags.None,
                    StructureByteStride = 0
                };
                _constantBuffer = device.CreateBuffer(constantBufferDescription);
            }

            if (_samplerState == null)
            {
                var samplerDescription = new SamplerDescription
                {
                    Filter = Filter.MinMagMipLinear,
                    AddressU = TextureAddressMode.Clamp,
                    AddressV = TextureAddressMode.Clamp,
                    AddressW = TextureAddressMode.Clamp,
                    MipLODBias = 0,
                    MaxAnisotropy = 1,
                    ComparisonFunc = ComparisonFunction.Never,
                    BorderColor = new Color4(0, 0, 0, 0),
                    MinLOD = 0,
                    MaxLOD = float.MaxValue
                };
                _samplerState = device.CreateSamplerState(samplerDescription);
            }

            if (_blendState == null)
            {
                var blendDescription = new BlendDescription
                {
                    AlphaToCoverageEnable = false,
                    IndependentBlendEnable = false
                };
                blendDescription.RenderTarget[0] = new RenderTargetBlendDescription
                {
                    BlendEnable = true,
                    SourceBlend = Blend.SourceAlpha,
                    DestinationBlend = Blend.InverseSourceAlpha,
                    BlendOperation = BlendOperation.Add,
                    SourceBlendAlpha = Blend.One,
                    DestinationBlendAlpha = Blend.InverseSourceAlpha,
                    BlendOperationAlpha = BlendOperation.Add,
                    RenderTargetWriteMask = ColorWriteEnable.All
                };
                _blendState = device.CreateBlendState(blendDescription);
            }

            if (_washTexture == null)
            {
                var washTextureDesc = new Texture2DDescription(
                    Format.B8G8R8A8_UNorm, 1, 1,
                    arraySize: 1, mipLevels: 1,
                    BindFlags.ShaderResource,
                    ResourceUsage.Default,
                    CpuAccessFlags.None,
                    sampleCount: 1, sampleQuality: 0,
                    ResourceOptionFlags.None);
                _washTexture = device.CreateTexture2D(in washTextureDesc);
                _washShaderResourceView = device.CreateShaderResourceView(_washTexture);
            }

            return _vertexShader != null
                && _pixelShader != null
                && _constantBuffer != null
                && _samplerState != null
                && _blendState != null
                && _washShaderResourceView != null;
        }
        catch (Exception ex)
        {
            _pipelineFailed = true;
            Debug.WriteLine($"[D3D11OverlayLayerRenderer] Pipeline initialization failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private static byte[] CompileShaderBytecode(string shaderSource, string entryPoint, string targetProfile)
    {
        var sourceBytes = Encoding.UTF8.GetBytes(shaderSource);
        var hr = D3DCompile(
            sourceBytes,
            (nuint)sourceBytes.Length,
            null,
            IntPtr.Zero,
            IntPtr.Zero,
            entryPoint,
            targetProfile,
            0,
            0,
            out var codePtr,
            out var errorPtr);

        try
        {
            if (hr < 0)
            {
                var errorMessage = errorPtr == IntPtr.Zero
                    ? null
                    : Marshal.PtrToStringAnsi(GetBufferPointer(errorPtr), checked((int)GetBufferSize(errorPtr)));
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(errorMessage)
                    ? $"D3DCompile failed for {entryPoint}/{targetProfile}: 0x{hr:X8}"
                    : $"D3DCompile failed for {entryPoint}/{targetProfile}: {errorMessage}");
            }

            if (codePtr == IntPtr.Zero)
            {
                throw new InvalidOperationException($"D3DCompile returned no bytecode for {entryPoint}/{targetProfile}.");
            }

            var size = checked((int)GetBufferSize(codePtr));
            var bytecode = new byte[size];
            Marshal.Copy(GetBufferPointer(codePtr), bytecode, 0, size);
            return bytecode;
        }
        finally
        {
            if (codePtr != IntPtr.Zero)
            {
                Marshal.Release(codePtr);
            }

            if (errorPtr != IntPtr.Zero)
            {
                Marshal.Release(errorPtr);
            }
        }
    }

    private static IntPtr GetBufferPointer(IntPtr blob)
    {
        var vtbl = Marshal.ReadIntPtr(blob);
        var fp = Marshal.ReadIntPtr(vtbl, 3 * IntPtr.Size);
        return Marshal.GetDelegateForFunctionPointer<GetBufferPointerDelegate>(fp)(blob);
    }

    private static nuint GetBufferSize(IntPtr blob)
    {
        var vtbl = Marshal.ReadIntPtr(blob);
        var fp = Marshal.ReadIntPtr(vtbl, 4 * IntPtr.Size);
        return Marshal.GetDelegateForFunctionPointer<GetBufferSizeDelegate>(fp)(blob);
    }

    private delegate IntPtr GetBufferPointerDelegate(IntPtr pThis);
    private delegate nuint GetBufferSizeDelegate(IntPtr pThis);

    [DllImport("d3dcompiler_47.dll", EntryPoint = "D3DCompile", ExactSpelling = true, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    private static extern int D3DCompile(
        [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] byte[] sourceData,
        nuint sourceDataSize,
        string? sourceName,
        IntPtr defines,
        IntPtr include,
        string entryPoint,
        string target,
        uint flags1,
        uint flags2,
        out IntPtr code,
        out IntPtr errorMessages);

    [StructLayout(LayoutKind.Sequential)]
    private struct OverlayLayerShaderConstants
    {
        public Vector4 DestinationRect;
        public Vector4 TextureRect;
    }

    private const string OverlayLayerShaderSource = @"
cbuffer OverlayConstants : register(b0)
{
    float4 DestinationRect;
    float4 TextureRect;
};

Texture2D OverlayTexture : register(t0);
SamplerState OverlaySampler : register(s0);

struct VertexOut
{
    float4 Position : SV_POSITION;
    float2 TexCoord : TEXCOORD0;
};

VertexOut VSMain(uint vertexId : SV_VertexID)
{
    float2 positions[4] =
    {
        float2(DestinationRect.x, DestinationRect.w),
        float2(DestinationRect.x, DestinationRect.y),
        float2(DestinationRect.z, DestinationRect.w),
        float2(DestinationRect.z, DestinationRect.y)
    };

    float2 texCoords[4] =
    {
        float2(TextureRect.x, TextureRect.w),
        float2(TextureRect.x, TextureRect.y),
        float2(TextureRect.z, TextureRect.w),
        float2(TextureRect.z, TextureRect.y)
    };

    VertexOut output;
    output.Position = float4(positions[vertexId], 0.0f, 1.0f);
    output.TexCoord = texCoords[vertexId];
    return output;
}

float4 PSMain(VertexOut input) : SV_TARGET
{
    return OverlayTexture.Sample(OverlaySampler, input.TexCoord);
}
";

    public void Dispose()
    {
        _lyricsShaderResourceView?.Dispose();
        _lyricsShaderResourceView = null;
        _lyricsTexture?.Dispose();
        _lyricsTexture = null;
        _vertexShader?.Dispose();
        _vertexShader = null;
        _pixelShader?.Dispose();
        _pixelShader = null;
        _washShaderResourceView?.Dispose();
        _washShaderResourceView = null;
        _washTexture?.Dispose();
        _washTexture = null;
        _constantBuffer?.Dispose();
        _constantBuffer = null;
        _samplerState?.Dispose();
        _samplerState = null;
        _blendState?.Dispose();
        _blendState = null;
        _pipelineFailed = false;
        _lyricsWidth = 0;
        _lyricsHeight = 0;
        _lyricsStride = 0;
    }
}
