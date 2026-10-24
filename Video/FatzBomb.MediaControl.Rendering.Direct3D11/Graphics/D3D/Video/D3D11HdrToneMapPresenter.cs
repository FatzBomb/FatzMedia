using System.Numerics;
using System.Runtime.InteropServices;
using FatzBomb.MediaControl.Core.Graphics.Composition;
using FatzBomb.MediaControl.Core.Graphics.D3D.Device;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace FatzBomb.MediaControl.Core.Graphics.D3D.Video;

/// <summary>
/// Tone maps hardware-decoded HDR frames (P010, BT.2020; PQ, or HLG from Stage 042A) to SDR on
/// the GPU (Stage 038A slice 2), with the BT.2390 curve of Core's CPU tone mapper: the decoder's
/// texture slice is copied into a shader-readable P010 texture, and a pixel shader converts
/// YCbCr to RGB, PQ (or HLG through its inverse OETF and the BT.2100 OOTF) to display light,
/// applies the curve to the brightest channel, converts BT.2020 to BT.709 primaries and encodes
/// gamma 2.4, drawing into the target the video processor would have filled, at the same
/// rectangles.
/// </summary>
internal sealed class D3D11HdrToneMapPresenter : IDisposable
{
    private ID3D11Device? _device;
    private ID3D11VertexShader? _vertexShader;
    private ID3D11PixelShader? _pixelShader;
    private ID3D11Buffer? _constantBuffer;
    private ID3D11SamplerState? _sampler;
    private ID3D11RasterizerState? _rasterizerState;
    private ID3D11Texture2D? _source;
    private ID3D11ShaderResourceView? _lumaView;
    private ID3D11ShaderResourceView? _chromaView;
    private ID3D11RenderTargetView? _targetView;
    private nint _targetViewTexture;
    private bool _failed;
    private nint _failedDevice;

    /// <summary>Why the last <see cref="TryPresent"/> failed, or null.</summary>
    public string? LastFailure { get; private set; }

    /// <summary>
    /// Draws the frame into <paramref name="target"/>: black, then the tone-mapped
    /// <paramref name="sourceRect"/> of the frame into <paramref name="destinationRect"/>.
    /// False (nothing drawn) when the frame is not P010 or the GPU path is unavailable; the
    /// caller then uses the video processor.
    /// </summary>
    public bool TryPresent(
        D3D11DeviceResources deviceResources,
        ID3D11Texture2D sourceTexture,
        int sourceArraySlice,
        VideoColorSpace colorSpace,
        HdrToneMapRequest toneMap,
        ID3D11Texture2D target,
        int targetWidth,
        int targetHeight,
        Vortice.RawRect sourceRect,
        Vortice.RawRect destinationRect)
    {
        if (deviceResources.Device is not { } device || deviceResources.Context is not { } context)
            return false;

        // A failure is remembered for its device only: a new device (recovery, a new
        // decoder device) gets another try.
        if (_failed)
        {
            if (device.NativePointer == _failedDevice)
                return false;

            _failed = false;
        }

        Texture2DDescription sourceDescription = sourceTexture.Description;
        if (sourceDescription.Format != Format.P010)
        {
            LastFailure = $"the decoder's frames are {sourceDescription.Format}, not P010";
            return false;
        }

        try
        {
            EnsurePipeline(device);
            EnsureSource(device, sourceDescription);
            EnsureTargetView(device, target);

            // The decoder's texture array is not shader-readable; copy this frame's slice.
            var sourceSubresource = (uint)Math.Max(0, sourceArraySlice) * Math.Max(1u, sourceDescription.MipLevels);
            context.CopySubresourceRegion(_source!, 0, 0, 0, 0, sourceTexture, sourceSubresource);

            WriteConstants(context, colorSpace, toneMap, sourceDescription, targetWidth, targetHeight, sourceRect, destinationRect);

            context.OMSetRenderTargets(_targetView!);
            context.ClearRenderTargetView(_targetView!, new Color4(0f, 0f, 0f, 1f));
            context.OMSetBlendState(null);
            context.RSSetState(_rasterizerState);
            context.RSSetViewport(new Viewport(0, 0, targetWidth, targetHeight));
            context.IASetPrimitiveTopology(PrimitiveTopology.TriangleStrip);
            context.IASetInputLayout(null);
            context.VSSetShader(_vertexShader);
            context.VSSetConstantBuffer(0, _constantBuffer);
            context.PSSetShader(_pixelShader);
            context.PSSetConstantBuffer(0, _constantBuffer);
            context.PSSetSampler(0, _sampler);
            context.PSSetShaderResource(0, _lumaView!);
            context.PSSetShaderResource(1, _chromaView!);
            context.Draw(4, 0);

            // Leave nothing bound that the composition presenter or the video processor uses.
            context.PSSetShaderResource(0, null!);
            context.PSSetShaderResource(1, null!);
            context.PSSetSampler(0, null!);
            context.PSSetConstantBuffer(0, null!);
            context.VSSetConstantBuffer(0, null!);
            context.RSSetState(null);
            context.OMSetRenderTargets((ID3D11RenderTargetView)null!);
            LastFailure = null;
            return true;
        }
        catch (Exception exception)
        {
            // Shader compile (including a missing d3dcompiler_47.dll) or resource creation
            // failed: not retried on this device.
            LastFailure = $"{exception.GetType().Name}: {exception.Message}";
            Reset();
            _failed = true;
            _failedDevice = device.NativePointer;
            return false;
        }
    }

    /// <summary>Releases the render-target view, so its texture (a swap-chain buffer) can be resized.</summary>
    public void ReleaseTargetView()
    {
        _targetView?.Dispose();
        _targetView = null;
        _targetViewTexture = 0;
    }

    /// <summary>Releases every GPU resource; the next frame creates them again.</summary>
    public void Reset()
    {
        ReleaseTargetView();
        _lumaView?.Dispose();
        _lumaView = null;
        _chromaView?.Dispose();
        _chromaView = null;
        _source?.Dispose();
        _source = null;
        _rasterizerState?.Dispose();
        _rasterizerState = null;
        _sampler?.Dispose();
        _sampler = null;
        _constantBuffer?.Dispose();
        _constantBuffer = null;
        _pixelShader?.Dispose();
        _pixelShader = null;
        _vertexShader?.Dispose();
        _vertexShader = null;
        _device = null;
        _failed = false;
        _failedDevice = 0;
    }

    public void Dispose() => Reset();

    private void EnsurePipeline(ID3D11Device device)
    {
        if (_device != null && _device.NativePointer == device.NativePointer && _pixelShader != null)
            return;

        Reset();
        _device = device;
        _vertexShader = device.CreateVertexShader(Composition.D3D11MediaCompositionPresenter.CompileShaderBytecode(ShaderSource, "VSMain", "vs_5_0"));
        _pixelShader = device.CreatePixelShader(Composition.D3D11MediaCompositionPresenter.CompileShaderBytecode(ShaderSource, "PSToneMap", "ps_5_0"));
        _constantBuffer = device.CreateBuffer(new BufferDescription
        {
            ByteWidth = (uint)Marshal.SizeOf<ToneMapConstants>(),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ConstantBuffer,
        });
        _sampler = device.CreateSamplerState(new SamplerDescription
        {
            Filter = Filter.MinMagMipLinear,
            AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp,
            AddressW = TextureAddressMode.Clamp,
            MaxAnisotropy = 1,
            ComparisonFunc = ComparisonFunction.Never,
            MinLOD = 0,
            MaxLOD = float.MaxValue,
        });
        _rasterizerState = device.CreateRasterizerState(RasterizerDescription.CullNone);
    }

    private void EnsureSource(ID3D11Device device, Texture2DDescription decoderDescription)
    {
        if (_source is { } existing
            && existing.Description.Width == decoderDescription.Width
            && existing.Description.Height == decoderDescription.Height)
        {
            return;
        }

        _lumaView?.Dispose();
        _chromaView?.Dispose();
        _source?.Dispose();
        _source = device.CreateTexture2D(new Texture2DDescription
        {
            Width = decoderDescription.Width,
            Height = decoderDescription.Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.P010,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource,
        });

        // P010's planes as views: R16 is the full-size luma plane, R16G16 the half-size CbCr plane.
        _lumaView = device.CreateShaderResourceView(_source, PlaneView(Format.R16_UNorm));
        _chromaView = device.CreateShaderResourceView(_source, PlaneView(Format.R16G16_UNorm));
    }

    private void EnsureTargetView(ID3D11Device device, ID3D11Texture2D target)
    {
        if (_targetView != null && _targetViewTexture == target.NativePointer)
            return;

        _targetView?.Dispose();
        _targetView = device.CreateRenderTargetView(target);
        _targetViewTexture = target.NativePointer;
    }

    private static ShaderResourceViewDescription PlaneView(Format format) => new()
    {
        Format = format,
        ViewDimension = ShaderResourceViewDimension.Texture2D,
        Texture2D = new Texture2DShaderResourceView { MostDetailedMip = 0, MipLevels = 1 },
    };

    private void WriteConstants(
        ID3D11DeviceContext context,
        VideoColorSpace colorSpace,
        HdrToneMapRequest toneMap,
        Texture2DDescription sourceDescription,
        int targetWidth,
        int targetHeight,
        Vortice.RawRect sourceRect,
        Vortice.RawRect destinationRect)
    {
        ToneMapConstants constants = CreateConstants(
            colorSpace,
            toneMap,
            (int)sourceDescription.Width,
            (int)sourceDescription.Height,
            targetWidth,
            targetHeight,
            sourceRect,
            destinationRect);
        context.UpdateSubresource(in constants, _constantBuffer!, 0, 0, 0, null);
    }

    /// <summary>The shader constants for a frame (pure; tested).</summary>
    internal static ToneMapConstants CreateConstants(
        VideoColorSpace colorSpace,
        HdrToneMapRequest toneMap,
        int textureWidth,
        int textureHeight,
        int targetWidth,
        int targetHeight,
        Vortice.RawRect sourceRect,
        Vortice.RawRect destinationRect)
    {
        // P010 keeps 10-bit codes in the top bits of 16: a sampled value v is code * 64 / 65535.
        const double codeScale = 65535.0 / 64;
        var full = colorSpace.Range == VideoColorRange.Full;
        var lumaScale = codeScale / (full ? 1023 : 876);
        var lumaOffset = full ? 0 : -64.0 / 876;
        var chromaScale = codeScale / (full ? 1023 : 896);
        var chromaOffset = -512.0 / (full ? 1023 : 896);

        (double kr, double kb) = colorSpace.Matrix switch
        {
            VideoColorMatrix.Bt601 => (0.299, 0.114),
            VideoColorMatrix.Bt709 => (0.2126, 0.0722),
            _ => (0.2627, 0.0593),
        };
        var kg = 1 - kr - kb;

        var hlg = toneMap.Transfer == HdrTransferFunction.Hlg;
        var sourcePeak = toneMap.SourcePeakSignal;
        var maxLum = sourcePeak > 0 ? toneMap.TargetPeakSignal / sourcePeak : 1;
        var compress = toneMap.SourcePeakNits > toneMap.TargetPeakNits;

        return new ToneMapConstants
        {
            DestinationRect = new Vector4(
                (float)((destinationRect.Left / (double)targetWidth * 2) - 1),
                (float)(1 - (destinationRect.Top / (double)targetHeight * 2)),
                (float)((destinationRect.Right / (double)targetWidth * 2) - 1),
                (float)(1 - (destinationRect.Bottom / (double)targetHeight * 2))),
            TextureRect = new Vector4(
                (float)(sourceRect.Left / (double)textureWidth),
                (float)(sourceRect.Top / (double)textureHeight),
                (float)(sourceRect.Right / (double)textureWidth),
                (float)(sourceRect.Bottom / (double)textureHeight)),
            YuvScaleOffset = new Vector4((float)lumaScale, (float)lumaOffset, (float)chromaScale, (float)chromaOffset),
            // R = Y + rCr * Cr; G = Y - gCb * Cb - gCr * Cr; B = Y + bCb * Cb.
            Matrix = new Vector4(
                (float)(2 * (1 - kr)),
                (float)(2 * kb * (1 - kb) / kg),
                (float)(2 * kr * (1 - kr) / kg),
                (float)(2 * (1 - kb))),
            Curve = new Vector4(
                (float)sourcePeak,
                (float)maxLum,
                (float)Math.Max((1.5 * maxLum) - 0.5, 0),
                compress ? 1f : 0f),
            Output = new Vector4(
                (float)(1 / toneMap.TargetPeakNits),
                (float)(1 / HdrToneMapRequest.SdrGamma),
                (float)toneMap.TargetPeakNits,
                0f),
            // HLG (Stage 042A): the OOTF's nominal peak and system gamma - 1.
            Transfer = new Vector4(
                hlg ? 1f : 0f,
                (float)toneMap.SourcePeakNits,
                (float)(toneMap.HlgSystemGamma - 1),
                0f),
            // Half a chroma texel (one luma texel) inside the crop, so bilinear taps never
            // reach the decoder's padding rows and columns.
            SampleClamp = new Vector4(
                (float)((sourceRect.Left + 1) / (double)textureWidth),
                (float)((sourceRect.Top + 1) / (double)textureHeight),
                (float)((sourceRect.Right - 1) / (double)textureWidth),
                (float)((sourceRect.Bottom - 1) / (double)textureHeight)),
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ToneMapConstants
    {
        public Vector4 DestinationRect;
        public Vector4 TextureRect;
        public Vector4 YuvScaleOffset;
        public Vector4 Matrix;
        public Vector4 Curve;
        public Vector4 Output;
        public Vector4 SampleClamp;
        public Vector4 Transfer;
    }

    internal const string ShaderSource = @"
cbuffer ToneMapConstants : register(b0)
{
    float4 DestinationRect;   // clip space: left, top, right, bottom
    float4 TextureRect;       // normalized: left, top, right, bottom
    float4 YuvScaleOffset;    // luma scale, luma offset, chroma scale, chroma offset
    float4 YuvMatrix;         // rCr, gCb, gCr, bCb
    float4 Curve;             // source peak (PQ), target peak / source peak (PQ), knee start, compress
    float4 Output;            // 1 / target nits, 1 / SDR gamma, target nits
    float4 SampleClamp;       // normalized sampling bounds inside the crop: left, top, right, bottom
    float4 Transfer;          // HLG (1) or PQ (0), HLG nominal peak nits, HLG system gamma - 1
};

Texture2D<float> LumaPlane : register(t0);
Texture2D<float2> ChromaPlane : register(t1);
SamplerState LinearSampler : register(s0);

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

static const float PqM1 = 2610.0 / 16384.0;
static const float PqM2 = 2523.0 / 4096.0 * 128.0;
static const float PqC1 = 3424.0 / 4096.0;
static const float PqC2 = 2413.0 / 4096.0 * 32.0;
static const float PqC3 = 2392.0 / 4096.0 * 32.0;

float3 PqToNits(float3 signal)
{
    float3 e = pow(saturate(signal), 1.0 / PqM2);
    return 10000.0 * pow(max(e - PqC1, 0.0) / (PqC2 - PqC3 * e), 1.0 / PqM1);
}

float PqToNits1(float signal)
{
    float e = pow(saturate(signal), 1.0 / PqM2);
    return 10000.0 * pow(max(e - PqC1, 0.0) / (PqC2 - PqC3 * e), 1.0 / PqM1);
}

float NitsToPq(float nits)
{
    float y = pow(saturate(nits / 10000.0), PqM1);
    return pow((PqC1 + PqC2 * y) / (1.0 + PqC3 * y), PqM2);
}

// ARIB STD-B67 / BT.2100 HLG inverse OETF, as Core's HlgCurve: signal to normalized scene light.
static const float HlgA = 0.17883277;
static const float HlgB = 0.28466892;
static const float HlgC = 0.55991073;

float HlgToScene1(float signal)
{
    return signal <= 0.5 ? signal * signal / 3.0 : (exp((signal - HlgC) / HlgA) + HlgB) / 12.0;
}

// The BT.2100 OOTF: display nits Lw * Ys^(gamma - 1) * E, Ys with BT.2020 weights.
float3 HlgToNits(float3 signal)
{
    float3 s = saturate(signal);
    float3 scene = float3(HlgToScene1(s.r), HlgToScene1(s.g), HlgToScene1(s.b));
    float luminance = dot(scene, float3(0.2627, 0.6780, 0.0593));
    float ootf = luminance > 0.0 ? Transfer.y * pow(luminance, Transfer.z) : 0.0;
    return scene * ootf;
}

// BT.2390 EETF, as Core's Bt2390ToneCurve.
float ToneCurve(float nits)
{
    if (Curve.w < 0.5)
        return min(nits, Output.z);

    float e1 = min(NitsToPq(nits) / Curve.x, 1.0);
    float kneeStart = Curve.z;
    float e2 = e1;
    if (e1 > kneeStart)
    {
        float t = (e1 - kneeStart) / (1.0 - kneeStart);
        float t2 = t * t;
        float t3 = t2 * t;
        e2 = (2.0 * t3 - 3.0 * t2 + 1.0) * kneeStart
            + (t3 - 2.0 * t2 + t) * (1.0 - kneeStart)
            + (-2.0 * t3 + 3.0 * t2) * Curve.y;
    }

    return min(PqToNits1(e2 * Curve.x), Output.z);
}

float4 PSToneMap(VertexOut input) : SV_TARGET
{
    float2 uv = clamp(input.TexCoord, SampleClamp.xy, SampleClamp.zw);
    float y = LumaPlane.Sample(LinearSampler, uv) * YuvScaleOffset.x + YuvScaleOffset.y;
    float2 c = ChromaPlane.Sample(LinearSampler, uv) * YuvScaleOffset.z + YuvScaleOffset.w;
    float3 signal = float3(
        y + YuvMatrix.x * c.y,
        y - YuvMatrix.y * c.x - YuvMatrix.z * c.y,
        y + YuvMatrix.w * c.x);

    float3 nits = Transfer.x > 0.5 ? HlgToNits(signal) : PqToNits(signal);
    float brightest = max(nits.r, max(nits.g, nits.b));
    float gain = brightest > 0.0 ? ToneCurve(brightest) / brightest : 1.0;
    float3 linear2020 = nits * (gain * Output.x);

    // BT.2020 to BT.709 primaries, linear light (ITU-R BT.2087).
    float3 linear709 = float3(
        dot(linear2020, float3(1.660491, -0.587641, -0.072850)),
        dot(linear2020, float3(-0.124550, 1.132900, -0.008349)),
        dot(linear2020, float3(-0.018151, -0.100579, 1.118730)));

    return float4(pow(saturate(linear709), Output.y), 1.0);
}
";
}
