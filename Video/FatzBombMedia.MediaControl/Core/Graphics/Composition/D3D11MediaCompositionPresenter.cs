using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using FatzBombMedia.MediaControl.Core.DxCdg;
using FatzBombMedia.MediaControl.Core.Graphics.D3D.Device;
using FatzBombMedia.MediaControl.Core.Graphics.D3D.Video;
using FatzBombMedia.MediaControl.Core.Graphics.Geometry;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using System.Windows.Media;

namespace FatzBombMedia.MediaControl.Core.Graphics.Composition;

/// <summary>
/// D3D11 implementation of the single media compositor. It draws an optional
/// background/base layer, optional wash layer, optional DXCDG foreground overlay, then presents once.
/// </summary>
internal sealed class D3D11MediaCompositionPresenter
{
    private readonly byte[] _paletteBgra = new byte[DxCdgConstants.ColorCount * 4];

    private ID3D11Texture2D? _backgroundTexture;
    private ID3D11ShaderResourceView? _backgroundShaderResourceView;
    private ID3D11PixelShader? _backgroundPixelShader;
    private ID3D11PixelShader? _solidPixelShader;
    private bool _hasBackgroundFrame;
    private MediaCompositionStretchMode _backgroundStretchMode = MediaCompositionStretchMode.UniformFit;
    private int _backgroundWidth;
    private int _backgroundHeight;
    private int _backgroundStride;
    private long _lastBackgroundRevision = -1;

    private ID3D11Texture2D? _indexTexture;
    private ID3D11ShaderResourceView? _indexShaderResourceView;
    private ID3D11Texture2D? _paletteTexture;
    private ID3D11ShaderResourceView? _paletteShaderResourceView;
    private ID3D11VertexShader? _vertexShader;
    private ID3D11PixelShader? _pixelShader;
    private ID3D11Buffer? _constantBuffer;
    private ID3D11SamplerState? _samplerState;
    private ID3D11BlendState? _blendState;
    private ID3D11RasterizerState? _rasterizerState;
    private long _lastPresentedRevision = -1;
    private long _lastPaletteRevision = -1;
    private long _lastDrawnRevision = -1;
    private int _lastLoggedRevisionSecond = -1;
    private bool _pipelineFailed;

    public bool IsReady => _indexTexture != null
        && _indexShaderResourceView != null
        && _paletteTexture != null
        && _paletteShaderResourceView != null;

    public void Reset()
    {
        _lastPresentedRevision = -1;
        _lastPaletteRevision = -1;
        _lastDrawnRevision = -1;
        _lastBackgroundRevision = -1;
        _lastLoggedRevisionSecond = -1;
        _pipelineFailed = false;
        ReleaseResources();
    }

    public void SetBackgroundFrame(
        D3D11DeviceResources deviceResources,
        ReadOnlySpan<byte> bgraPixels,
        int width,
        int height,
        int stride,
        bool forceZoomToFill)
    {
        if (deviceResources.Device == null
            || deviceResources.Context == null
            || bgraPixels.IsEmpty
            || width <= 0
            || height <= 0
            || stride < width * 4)
        {
            ClearBackgroundFrame();
            return;
        }

        var required = checked(stride * height);
        if (bgraPixels.Length < required)
        {
            ClearBackgroundFrame();
            return;
        }

        try
        {
            EnsureBackgroundTexture(deviceResources.Device, width, height);
            if (_backgroundTexture == null)
            {
                return;
            }

            deviceResources.Context.UpdateSubresource(bgraPixels[..required], _backgroundTexture, 0, (uint)stride, 0, null);
            _hasBackgroundFrame = true;
            _backgroundStretchMode = forceZoomToFill ? MediaCompositionStretchMode.UniformToFill : MediaCompositionStretchMode.UniformFit;
            _backgroundWidth = width;
            _backgroundHeight = height;
            _backgroundStride = stride;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[DXCDG-D3D11] Background frame upload failed: {ex.GetType().Name}: {ex.Message}");
            ClearBackgroundFrame();
        }
    }

    public void ClearBackgroundFrame()
    {
        _hasBackgroundFrame = false;
        _backgroundStretchMode = MediaCompositionStretchMode.UniformFit;
        _backgroundWidth = 0;
        _backgroundHeight = 0;
        _backgroundStride = 0;
        _lastBackgroundRevision = -1;
        ReleaseBackgroundResources();
    }

    public bool SetHardwareBackgroundFrame(
        D3D11DeviceResources deviceResources,
        D3D11VideoProcessorPresenter videoProcessorPresenter,
        IntPtr sourceTexturePointer,
        int sourceArraySlice,
        int width,
        int height,
        double framesPerSecond,
        MediaCompositionStretchMode stretchMode,
        long backgroundRevision)
    {
        if (deviceResources.Device == null
            || deviceResources.Context == null
            || !deviceResources.HasVideoProcessorSupport
            || sourceTexturePointer == IntPtr.Zero
            || width <= 0
            || height <= 0)
        {
            return false;
        }

        try
        {
            EnsureBackgroundTexture(deviceResources.Device, width, height);
            if (_backgroundTexture == null || _backgroundShaderResourceView == null)
            {
                return false;
            }

            var normalizedFps = double.IsFinite(framesPerSecond) && framesPerSecond > 0 ? framesPerSecond : 60.0;
            if (!videoProcessorPresenter.PresentHardwareFrame(
                    deviceResources,
                    _backgroundTexture,
                    sourceTexturePointer,
                    sourceArraySlice,
                    width,
                    height,
                    normalizedFps,
                    width,
                    height,
                    Stretch.Fill))
            {
                return false;
            }

            _hasBackgroundFrame = true;
            _backgroundStretchMode = stretchMode;
            _backgroundWidth = width;
            _backgroundHeight = height;
            _backgroundStride = width * 4;
            _lastBackgroundRevision = backgroundRevision;
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[COMPOSITION-D3D11] Hardware background copy failed: {ex.GetType().Name}: {ex.Message}");
            ClearBackgroundFrame();
            return false;
        }
    }

    public bool PresentToTarget(
        D3D11DeviceResources deviceResources,
        DxCdgRenderSnapshot snapshot,
        ID3D11Texture2D targetTexture,
        int targetWidth,
        int targetHeight,
        string backendName,
        long targetRevision,
        bool retainedRefresh = false)
        => PresentToTarget(
            deviceResources,
            new MediaCompositionFrame
            {
                ClearMode = MediaCompositionClearMode.Black,
                ClearArgb = 0xFF000000,
                WashArgb = snapshot.BackgroundWashArgb,
                CdgOverlay = snapshot,
                RetainedRefresh = retainedRefresh,
                Reason = retainedRefresh ? "retained-dxcdg" : "dxcdg"
            },
            targetTexture,
            targetWidth,
            targetHeight,
            backendName,
            targetRevision);

    public bool PresentToTarget(
        D3D11DeviceResources deviceResources,
        MediaCompositionFrame frame,
        ID3D11Texture2D targetTexture,
        int targetWidth,
        int targetHeight,
        string backendName,
        long targetRevision,
        D3D11VideoProcessorPresenter? videoProcessorPresenter = null)
    {
        if (deviceResources.Device == null
            || deviceResources.Context == null
            || targetTexture == null
            || targetWidth <= 0
            || targetHeight <= 0)
        {
            Debug.WriteLine($"[COMPOSITION-D3D11] Visible present skipped: invalid device/target target={targetWidth}x{targetHeight} reason={frame.Reason}.");
            return false;
        }

        try
        {
            if (!EnsureCompositionBackgroundFrame(deviceResources, frame, videoProcessorPresenter))
            {
                Debug.WriteLine($"[COMPOSITION-D3D11] Visible present skipped: background layer could not be prepared reason={frame.Reason}.");
                return false;
            }

            var snapshot = frame.CdgOverlay;
            if (frame.HasCdgOverlay && snapshot != null)
            {
                EnsureResources(deviceResources.Device);
                if (_indexTexture == null || _paletteTexture == null)
                {
                    Debug.WriteLine("[COMPOSITION-D3D11] Visible present skipped: DXCDG index/palette resources were not created.");
                    return false;
                }

                if (snapshot.Revision != _lastPresentedRevision)
                {
                    UploadIndexTexture(deviceResources.Context, snapshot);
                    UploadPaletteTextureIfNeeded(deviceResources.Context, snapshot);
                    _lastPresentedRevision = snapshot.Revision;
                }
            }

            if (frame.HasShaderVisibleLayer && !EnsureDrawPipeline(deviceResources.Device))
            {
                Debug.WriteLine("[COMPOSITION-D3D11] Visible present skipped: shader pipeline is unavailable.");
                return false;
            }

            using var targetRenderView = deviceResources.Device.CreateRenderTargetView(targetTexture);
            if (!frame.RetainedRefresh)
            {
                LogVisibleAttempt(frame, targetWidth, targetHeight, backendName, targetRevision);
            }

            var drew = DrawToRenderTarget(
                deviceResources.Context,
                frame,
                targetWidth,
                targetHeight,
                targetRenderView,
                clearTarget: frame.ClearMode != MediaCompositionClearMode.PreservePrevious);

            if (drew && !frame.RetainedRefresh)
            {
                LogVisiblePresent(frame, targetWidth, targetHeight, backendName, targetRevision);
            }

            return drew;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[COMPOSITION-D3D11] Visible target present failed: {ex.GetType().Name}: {ex.Message}");
            Reset();
            return false;
        }
    }


    private bool EnsureCompositionBackgroundFrame(D3D11DeviceResources deviceResources, MediaCompositionFrame frame, D3D11VideoProcessorPresenter? videoProcessorPresenter)
    {
        if (frame.HasD3D11Background)
        {
            if (videoProcessorPresenter == null)
            {
                return false;
            }

            return SetHardwareBackgroundFrame(
                deviceResources,
                videoProcessorPresenter,
                frame.BackgroundD3D11TexturePointer,
                frame.BackgroundD3D11ArraySlice,
                frame.BackgroundD3D11Width,
                frame.BackgroundD3D11Height,
                frame.BackgroundD3D11FramesPerSecond,
                frame.EffectiveBackgroundStretchMode,
                frame.BackgroundRevision);
        }

        if (frame.HasBackground)
        {
            var revisionMatches = frame.BackgroundRevision >= 0 && frame.BackgroundRevision == _lastBackgroundRevision;
            var dimensionsMatch = _hasBackgroundFrame
                && _backgroundWidth == frame.BackgroundWidth
                && _backgroundHeight == frame.BackgroundHeight
                && _backgroundStride == frame.BackgroundStride
                && _backgroundStretchMode == frame.EffectiveBackgroundStretchMode;

            if (revisionMatches && dimensionsMatch && _backgroundShaderResourceView != null)
            {
                return true;
            }

            SetBackgroundFrame(
                deviceResources,
                frame.BackgroundBgra.Span,
                frame.BackgroundWidth,
                frame.BackgroundHeight,
                frame.BackgroundStride,
                frame.EffectiveBackgroundStretchMode == MediaCompositionStretchMode.UniformToFill);

            if (_hasBackgroundFrame)
            {
                _backgroundStretchMode = frame.EffectiveBackgroundStretchMode;
                _lastBackgroundRevision = frame.BackgroundRevision;
            }

            return _hasBackgroundFrame;
        }

        if (frame.HasRetainedBackground)
        {
            if (!_hasBackgroundFrame || _backgroundShaderResourceView == null)
            {
                return false;
            }

            _backgroundStretchMode = frame.EffectiveBackgroundStretchMode;
            return true;
        }

        ClearBackgroundFrame();
        return true;
    }

    private void EnsureResources(ID3D11Device device)
    {
        if (_indexTexture != null && _paletteTexture != null)
        {
            return;
        }

        ReleaseTextureResources();

        var indexDescription = new Texture2DDescription(
            Format.R8_UNorm,
            (uint)DxCdgConstants.FrameWidth,
            (uint)DxCdgConstants.FrameHeight,
            arraySize: 1,
            mipLevels: 1,
            BindFlags.ShaderResource,
            ResourceUsage.Default,
            CpuAccessFlags.None,
            sampleCount: 1,
            sampleQuality: 0,
            ResourceOptionFlags.None);

        _indexTexture = device.CreateTexture2D(in indexDescription);
        _indexShaderResourceView = device.CreateShaderResourceView(_indexTexture);

        var paletteDescription = new Texture2DDescription(
            Format.B8G8R8A8_UNorm,
            (uint)DxCdgConstants.ColorCount,
            1,
            arraySize: 1,
            mipLevels: 1,
            BindFlags.ShaderResource,
            ResourceUsage.Default,
            CpuAccessFlags.None,
            sampleCount: 1,
            sampleQuality: 0,
            ResourceOptionFlags.None);

        _paletteTexture = device.CreateTexture2D(in paletteDescription);
        _paletteShaderResourceView = device.CreateShaderResourceView(_paletteTexture);
    }

    private void EnsureBackgroundTexture(ID3D11Device device, int width, int height)
    {
        if (_backgroundTexture != null && _backgroundWidth == width && _backgroundHeight == height)
        {
            return;
        }

        ReleaseBackgroundResources();

        var backgroundDescription = new Texture2DDescription(
            Format.B8G8R8A8_UNorm,
            (uint)width,
            (uint)height,
            arraySize: 1,
            mipLevels: 1,
            BindFlags.ShaderResource | BindFlags.RenderTarget,
            ResourceUsage.Default,
            CpuAccessFlags.None,
            sampleCount: 1,
            sampleQuality: 0,
            ResourceOptionFlags.None);

        _backgroundTexture = device.CreateTexture2D(in backgroundDescription);
        _backgroundShaderResourceView = device.CreateShaderResourceView(_backgroundTexture);
        _backgroundWidth = width;
        _backgroundHeight = height;
    }

    private void UploadIndexTexture(ID3D11DeviceContext context, DxCdgRenderSnapshot snapshot)
    {
        var indices = snapshot.ColorIndices.Span;
        if (indices.Length < DxCdgConstants.TotalPixels || _indexTexture == null)
        {
            return;
        }

        // Upload one byte per CDG pixel. Palette colors are uploaded separately and
        // resolved by the pixel shader.
        context.UpdateSubresource(indices, _indexTexture, 0, (uint)DxCdgConstants.FrameWidth, 0, null);
    }

    private void UploadPaletteTextureIfNeeded(ID3D11DeviceContext context, DxCdgRenderSnapshot snapshot)
    {
        if (_paletteTexture == null || _lastPaletteRevision == snapshot.Revision)
        {
            return;
        }

        var palette = snapshot.PaletteArgb.Span;
        if (palette.Length < DxCdgConstants.ColorCount)
        {
            return;
        }

        for (var i = 0; i < DxCdgConstants.ColorCount; i++)
        {
            var argb = unchecked((uint)palette[i]);
            var offset = i * 4;
            _paletteBgra[offset + 0] = (byte)(argb & 0xFF);
            _paletteBgra[offset + 1] = (byte)((argb >> 8) & 0xFF);
            _paletteBgra[offset + 2] = (byte)((argb >> 16) & 0xFF);
            _paletteBgra[offset + 3] = (byte)((argb >> 24) & 0xFF);
        }

        context.UpdateSubresource(_paletteBgra.AsSpan(), _paletteTexture, 0, (uint)(DxCdgConstants.ColorCount * 4), 0, null);
        _lastPaletteRevision = snapshot.Revision;
    }

    private bool DrawToRenderTarget(
        ID3D11DeviceContext context,
        MediaCompositionFrame frame,
        int outputWidth,
        int outputHeight,
        ID3D11RenderTargetView renderTargetView,
        bool clearTarget)
    {
        if (frame.HasShaderVisibleLayer
            && (_constantBuffer == null
                || _vertexShader == null
                || _samplerState == null
                || _blendState == null
                || _rasterizerState == null))
        {
            return false;
        }

        context.IASetPrimitiveTopology(PrimitiveTopology.TriangleStrip);
        context.IASetInputLayout(null);
        context.RSSetViewport(new Viewport(0, 0, outputWidth, outputHeight));
        context.OMSetRenderTargets(renderTargetView);

        if (clearTarget)
        {
            context.ClearRenderTargetView(renderTargetView, ResolveClearColor(frame));
        }

        if (frame.HasShaderVisibleLayer)
        {
            context.VSSetShader(_vertexShader);
            context.RSSetState(_rasterizerState);
            context.OMSetBlendState(_blendState);
            context.PSSetSampler(0, _samplerState);
        }

        if (frame.HasAnyBackground
            && _hasBackgroundFrame
            && _backgroundShaderResourceView != null
            && _backgroundPixelShader != null)
        {
            DrawBackgroundLayer(context, outputWidth, outputHeight);
        }

        if (frame.HasWash)
        {
            DrawWashLayer(context, outputWidth, outputHeight, frame.WashArgb);
        }

        var snapshot = frame.CdgOverlay;
        if (frame.HasCdgOverlay && snapshot != null)
        {
            if (_indexShaderResourceView == null
                || _paletteShaderResourceView == null
                || _pixelShader == null)
            {
                return false;
            }

            var cdgRect = ViewportGeometry.UniformFit(DxCdgConstants.FrameWidth, DxCdgConstants.FrameHeight, outputWidth, outputHeight);
            var cdgLeft = (int)Math.Round(cdgRect.Left);
            var cdgTop = (int)Math.Round(cdgRect.Top);
            var cdgRight = cdgLeft + Math.Max(1, (int)Math.Round(cdgRect.Width));
            var cdgBottom = cdgTop + Math.Max(1, (int)Math.Round(cdgRect.Height));
            var cdgDestination = new RectD(cdgLeft, cdgTop, cdgRight - cdgLeft, cdgBottom - cdgTop);

            SetQuadConstants(
                context,
                cdgDestination,
                outputWidth,
                outputHeight,
                new Vector4(DxCdgConstants.ColorCount, snapshot.BackgroundColorIndex, 0f, 0f));
            DrawCdgForegroundLayer(context);
            _lastDrawnRevision = snapshot.Revision;
        }
        else
        {
            _lastDrawnRevision = -1;
        }

        context.PSSetShaderResource(0, null);
        context.PSSetShaderResource(1, null);
        context.PSSetSampler(0, null);
        context.PSSetConstantBuffer(0, null);
        context.VSSetConstantBuffer(0, null);
        context.RSSetState(null);
        context.OMSetRenderTargets((ID3D11RenderTargetView?)null);
        context.Flush();
        return true;
    }

    private static Color4 ResolveClearColor(MediaCompositionFrame frame)
    {
        return frame.ClearMode switch
        {
            MediaCompositionClearMode.Transparent => new Color4(0, 0, 0, 0),
            MediaCompositionClearMode.ConfiguredColor => ToColor4(frame.ClearArgb),
            MediaCompositionClearMode.PreservePrevious => new Color4(0, 0, 0, 0),
            _ => ToColor4(frame.ClearArgb == 0 ? 0xFF000000 : frame.ClearArgb)
        };
    }

    private void DrawBackgroundLayer(ID3D11DeviceContext context, int outputWidth, int outputHeight)
    {
        var backgroundRect = ResolveBackgroundDestinationRect(outputWidth, outputHeight);

        SetQuadConstants(context, backgroundRect, outputWidth, outputHeight, new Vector4(0, 0, 0, 0));
        context.PSSetShader(_backgroundPixelShader);
        context.PSSetShaderResource(0, _backgroundShaderResourceView);
        context.Draw(4, 0);
        context.PSSetShaderResource(0, null);
    }


    private RectD ResolveBackgroundDestinationRect(int outputWidth, int outputHeight)
    {
        return _backgroundStretchMode switch
        {
            MediaCompositionStretchMode.Original => CenterOriginal(_backgroundWidth, _backgroundHeight, outputWidth, outputHeight),
            MediaCompositionStretchMode.Fill => new RectD(0, 0, outputWidth, outputHeight),
            MediaCompositionStretchMode.UniformToFill => ViewportGeometry.UniformToFill(_backgroundWidth, _backgroundHeight, outputWidth, outputHeight),
            _ => ViewportGeometry.UniformFit(_backgroundWidth, _backgroundHeight, outputWidth, outputHeight)
        };
    }

    private static RectD CenterOriginal(int sourceWidth, int sourceHeight, int outputWidth, int outputHeight)
    {
        var width = Math.Max(1, Math.Min(sourceWidth, outputWidth));
        var height = Math.Max(1, Math.Min(sourceHeight, outputHeight));
        var left = (outputWidth - width) / 2.0;
        var top = (outputHeight - height) / 2.0;
        return new RectD(left, top, width, height);
    }

    private void DrawWashLayer(ID3D11DeviceContext context, int outputWidth, int outputHeight, uint washArgb)
    {
        var alpha = ((washArgb >> 24) & 0xFF) / 255.0f;
        if (alpha <= 0f || _solidPixelShader == null)
        {
            return;
        }

        var r = ((washArgb >> 16) & 0xFF) / 255.0f;
        var g = ((washArgb >> 8) & 0xFF) / 255.0f;
        var b = (washArgb & 0xFF) / 255.0f;
        SetQuadConstants(context, new RectD(0, 0, outputWidth, outputHeight), outputWidth, outputHeight, new Vector4(r, g, b, alpha));
        context.PSSetShader(_solidPixelShader);
        context.Draw(4, 0);
    }

    private void DrawCdgForegroundLayer(ID3D11DeviceContext context)
    {
        context.PSSetShader(_pixelShader);
        context.PSSetConstantBuffer(0, _constantBuffer);
        context.PSSetShaderResource(0, _indexShaderResourceView);
        context.PSSetShaderResource(1, _paletteShaderResourceView);
        context.Draw(4, 0);
    }

    private void SetQuadConstants(ID3D11DeviceContext context, RectD destinationRect, int outputWidth, int outputHeight, Vector4 paletteInfo)
    {
        var left = (int)Math.Round(destinationRect.Left);
        var top = (int)Math.Round(destinationRect.Top);
        var right = left + Math.Max(1, (int)Math.Round(destinationRect.Width));
        var bottom = top + Math.Max(1, (int)Math.Round(destinationRect.Height));

        Span<byte> constantBytes = stackalloc byte[Marshal.SizeOf<DxCdgShaderConstants>()];
        var constants = new DxCdgShaderConstants
        {
            DestinationRect = new Vector4(
                (float)((left / (double)outputWidth) * 2.0 - 1.0),
                (float)(1.0 - (top / (double)outputHeight) * 2.0),
                (float)((right / (double)outputWidth) * 2.0 - 1.0),
                (float)(1.0 - (bottom / (double)outputHeight) * 2.0)),
            TextureRect = new Vector4(0f, 0f, 1f, 1f),
            PaletteInfo = paletteInfo
        };

        MemoryMarshal.Write(constantBytes, in constants);
        context.UpdateSubresource(constantBytes, _constantBuffer, 0, 0, 0, null);
        context.VSSetConstantBuffer(0, _constantBuffer);
        context.PSSetConstantBuffer(0, _constantBuffer);
    }

    private bool EnsureDrawPipeline(ID3D11Device device)
    {
        if (_pipelineFailed)
        {
            return false;
        }

        try
        {
            if (_vertexShader == null || _pixelShader == null)
            {
                var vertexShaderBytecode = CompileShaderBytecode(DxCdgShaderSource, "VSMain", "vs_5_0");
                var pixelShaderBytecode = CompileShaderBytecode(DxCdgShaderSource, "PSMain", "ps_5_0");
                var backgroundPixelShaderBytecode = CompileShaderBytecode(DxCdgShaderSource, "PSBackground", "ps_5_0");
                var solidPixelShaderBytecode = CompileShaderBytecode(DxCdgShaderSource, "PSSolid", "ps_5_0");
                _vertexShader = device.CreateVertexShader(vertexShaderBytecode);
                _pixelShader = device.CreatePixelShader(pixelShaderBytecode);
                _backgroundPixelShader = device.CreatePixelShader(backgroundPixelShaderBytecode);
                _solidPixelShader = device.CreatePixelShader(solidPixelShaderBytecode);
            }

            if (_constantBuffer == null)
            {
                var constantBufferDescription = new BufferDescription
                {
                    ByteWidth = (uint)Marshal.SizeOf<DxCdgShaderConstants>(),
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
                    Filter = Filter.MinMagMipPoint,
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

            if (_rasterizerState == null)
            {
                var rasterizerDescription = new RasterizerDescription
                {
                    FillMode = FillMode.Solid,
                    CullMode = CullMode.None,
                    FrontCounterClockwise = false,
                    DepthBias = 0,
                    DepthBiasClamp = 0,
                    SlopeScaledDepthBias = 0,
                    DepthClipEnable = true,
                    ScissorEnable = false,
                    MultisampleEnable = false,
                    AntialiasedLineEnable = false
                };
                _rasterizerState = device.CreateRasterizerState(rasterizerDescription);
            }

            return _vertexShader != null
                && _pixelShader != null
                && _backgroundPixelShader != null
                && _solidPixelShader != null
                && _constantBuffer != null
                && _samplerState != null
                && _blendState != null
                && _rasterizerState != null;
        }
        catch (Exception ex)
        {
            _pipelineFailed = true;
            Debug.WriteLine($"[DXCDG-D3D11] Draw pipeline initialization failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }



    private void LogVisibleAttempt(MediaCompositionFrame frame, int outputWidth, int outputHeight, string backendName, long targetRevision)
    {
        var snapshot = frame.CdgOverlay;
        if (snapshot == null)
        {
            Debug.WriteLine(
                $"[COMPOSITION-D3D11] Drawing composition reason={frame.Reason} target={outputWidth}x{outputHeight} " +
                $"backend={backendName} targetRevision={targetRevision} backgroundFrame={frame.HasAnyBackground} hardwareBackground={frame.HasD3D11Background} retainedBackground={frame.HasRetainedBackground} " +
                $"background={frame.BackgroundWidth}x{frame.BackgroundHeight} hwBackground={frame.BackgroundD3D11Width}x{frame.BackgroundD3D11Height} " +
                $"bgRevision={frame.BackgroundRevision} bgStretch={frame.EffectiveBackgroundStretchMode} wash=0x{frame.WashArgb:X8} overlay=False clear={frame.ClearMode}");
            return;
        }

        var (NonZero, Foreground, Background, Histogram) = CountIndexStats(snapshot);
        Debug.WriteLine(
            $"[COMPOSITION-D3D11] Drawing composition reason={frame.Reason} revision={snapshot.Revision} dirty={snapshot.DirtyRects.Count} " +
            $"target={outputWidth}x{outputHeight} backend={backendName} targetRevision={targetRevision} " +
            $"clear={frame.ClearMode} bg=0x{snapshot.BackgroundWashArgb:X8} bgIndex={snapshot.BackgroundColorIndex} " +
            $"backgroundFrame={frame.HasAnyBackground} hardwareBackground={frame.HasD3D11Background} retainedBackground={frame.HasRetainedBackground} " +
            $"background={frame.BackgroundWidth}x{frame.BackgroundHeight} hwBackground={frame.BackgroundD3D11Width}x{frame.BackgroundD3D11Height} bgRevision={frame.BackgroundRevision} bgStretch={frame.EffectiveBackgroundStretchMode} " +
            $"wash=0x{frame.WashArgb:X8} overlay=True nonZero={NonZero} foreground={Foreground} bgPixels={Background} " +
            $"hist=[{Histogram}]");

        if (Foreground == 0)
        {
            Debug.WriteLine(
                $"[COMPOSITION-D3D11] WARNING: snapshot revision={snapshot.Revision} contains no foreground pixels after background-index filtering.");
        }
    }

    private static (int NonZero, int Foreground, int Background, string Histogram) CountIndexStats(DxCdgRenderSnapshot snapshot)
    {
        var background = 0;
        var nonZero = 0;
        var foreground = 0;
        var histogram = new int[DxCdgConstants.ColorCount];
        var backgroundIndex = snapshot.BackgroundColorIndex is >= 0 and < DxCdgConstants.ColorCount
            ? snapshot.BackgroundColorIndex
            : -1;

        foreach (var index in snapshot.ColorIndices.Span)
        {
            if (index < DxCdgConstants.ColorCount)
            {
                histogram[index]++;
            }

            if (index != 0)
            {
                nonZero++;
            }

            if (index == backgroundIndex)
            {
                background++;
                continue;
            }

            if (index < DxCdgConstants.ColorCount)
            {
                foreground++;
            }
        }

        var histogramText = string.Join(",", histogram.Select((count, index) => $"{index}:{count}"));
        return (nonZero, foreground, background, histogramText);
    }

    private static Color4 ToColor4(uint argb)
    {
        var a = ((argb >> 24) & 0xFF) / 255.0f;
        var r = ((argb >> 16) & 0xFF) / 255.0f;
        var g = ((argb >> 8) & 0xFF) / 255.0f;
        var b = (argb & 0xFF) / 255.0f;
        return new Color4(r, g, b, a);
    }

    private void LogVisiblePresent(MediaCompositionFrame frame, int outputWidth, int outputHeight, string backendName, long targetRevision)
    {
        var snapshot = frame.CdgOverlay;
        if (snapshot == null)
        {
            Debug.WriteLine(
                $"[COMPOSITION-D3D11] Presented composition reason={frame.Reason} target={outputWidth}x{outputHeight} " +
                $"backend={backendName} targetRevision={targetRevision}");
            return;
        }

        var logBucket = (int)(snapshot.Revision / 300);
        if (logBucket == _lastLoggedRevisionSecond)
        {
            return;
        }

        _lastLoggedRevisionSecond = logBucket;
        Debug.WriteLine(
            $"[COMPOSITION-D3D11] Presented composition revision={snapshot.Revision} dirty={snapshot.DirtyRects.Count} " +
            $"target={outputWidth}x{outputHeight} backend={backendName} targetRevision={targetRevision} drawRevision={_lastDrawnRevision}");
    }

    private void ReleaseResources()
    {
        ReleaseTextureResources();
        ReleaseBackgroundResources();
        _vertexShader?.Dispose();
        _vertexShader = null;
        _pixelShader?.Dispose();
        _pixelShader = null;
        _backgroundPixelShader?.Dispose();
        _backgroundPixelShader = null;
        _solidPixelShader?.Dispose();
        _solidPixelShader = null;
        _constantBuffer?.Dispose();
        _constantBuffer = null;
        _samplerState?.Dispose();
        _samplerState = null;
        _blendState?.Dispose();
        _blendState = null;
        _rasterizerState?.Dispose();
        _rasterizerState = null;
    }

    private void ReleaseTextureResources()
    {
        _indexShaderResourceView?.Dispose();
        _indexShaderResourceView = null;
        _indexTexture?.Dispose();
        _indexTexture = null;
        _paletteShaderResourceView?.Dispose();
        _paletteShaderResourceView = null;
        _paletteTexture?.Dispose();
        _paletteTexture = null;
    }

    private void ReleaseBackgroundResources()
    {
        _backgroundShaderResourceView?.Dispose();
        _backgroundShaderResourceView = null;
        _backgroundTexture?.Dispose();
        _backgroundTexture = null;
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
    private struct DxCdgShaderConstants
    {
        public Vector4 DestinationRect;
        public Vector4 TextureRect;
        public Vector4 PaletteInfo;
    }

    private const string DxCdgShaderSource = @"
cbuffer DxCdgConstants : register(b0)
{
    float4 DestinationRect;
    float4 TextureRect;
    float4 PaletteInfo;
};

Texture2D IndexTexture : register(t0);
Texture2D PaletteTexture : register(t1);
SamplerState PointSampler : register(s0);

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

float4 PSBackground(VertexOut input) : SV_TARGET
{
    return IndexTexture.Sample(PointSampler, input.TexCoord);
}

float4 PSSolid(VertexOut input) : SV_TARGET
{
    return PaletteInfo;
}

float4 PSMain(VertexOut input) : SV_TARGET
{
    float normalizedIndex = IndexTexture.Sample(PointSampler, input.TexCoord).r;
    float paletteIndex = clamp(floor((normalizedIndex * 255.0f) + 0.5f), 0.0f, PaletteInfo.x - 1.0f);

    // The active DXCDG path clears the target to the background/wash color first.
    // Only the current CDG background index is transparent in the foreground pass.
    // Do not automatically hide index 0; CDG files may legitimately use color 0 as
    // a lyric/tile color when a different background index is active.
    if (PaletteInfo.y >= 0.0f && abs(paletteIndex - PaletteInfo.y) < 0.5f)
    {
        return float4(0.0f, 0.0f, 0.0f, 0.0f);
    }

    float paletteU = (paletteIndex + 0.5f) / PaletteInfo.x;
    float4 color = PaletteTexture.Sample(PointSampler, float2(paletteU, 0.5f));
    return float4(color.rgb, 1.0f);
}
";

    public void Dispose() => Reset();
}
