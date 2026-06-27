using System.Diagnostics;
using FatzBombMedia.MediaControl.Core.Configuration;
using FatzBombMedia.MediaControl.Core.Graphics.D3D.Device;
using FatzBombMedia.MediaControl.Core.Graphics.D3D.Diagnostics;
using FatzBombMedia.MediaControl.Core.Graphics.D3D.SwapChain;
using FatzBombMedia.MediaControl.Core.Graphics.D3D.Upload;
using FatzBombMedia.MediaControl.Core.Graphics.D3D.Video;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using System.Windows.Media;

namespace FatzBombMedia.MediaControl.Core.Graphics.D3D.Presentation.DirectComposition;

/// <summary>
/// Owns the current DirectComposition presentation backend: a base composition swap-chain
/// for the main video/image content plus an alpha overlay swap-chain for retained overlay
/// graphics.  This is still the existing single-target/base+overlay implementation; the
/// future true layered DirectComposition backend should replace this class rather than
/// adding more DirectComposition conditionals to VorticeD3D11Renderer.
/// </summary>
internal sealed class D3D11DirectCompositionSingleTargetBackend : ID3D11PresentationBackend
{
    private readonly MediaPlayerConfiguration _configuration;
    private readonly D3D11PresentationDiagnostics _diagnostics;
    private readonly D3D11DirectCompositionTarget _target;

    private IDXGISwapChain? _baseSwapChain;
    private ID3D11Texture2D? _targetTexture;
    private IntPtr _hwnd;

    public D3D11DirectCompositionSingleTargetBackend(
        MediaPlayerConfiguration configuration,
        D3D11PresentationDiagnostics diagnostics)
    {
        _configuration = configuration;
        _diagnostics = diagnostics;
        _target = new D3D11DirectCompositionTarget(configuration);
    }

    public D3D11PresentationBackendKind Kind => D3D11PresentationBackendKind.DirectCompositionSingleTarget;
    public bool IsActive => _target.IsActive && _baseSwapChain != null && _targetTexture != null && _hwnd != IntPtr.Zero;
    public int Width { get; private set; }
    public int Height { get; private set; }
    public ID3D11Texture2D? TargetTexture => _targetTexture;
    public ID3D11Texture2D? OverlayTexture => _target.OverlayTexture;
    public IntPtr Hwnd => _hwnd;

    public bool Matches(IntPtr hwnd, int width, int height)
    {
        return IsActive && hwnd == _hwnd && width == Width && height == Height;
    }

    public bool Create(D3D11DeviceResources deviceResources, IntPtr hwnd, int width, int height)
    {
        if (deviceResources.Device == null || hwnd == IntPtr.Zero || width <= 0 || height <= 0)
        {
            return false;
        }

        Cleanup();

        try
        {
            using var dxgiDevice = deviceResources.Device.QueryInterface<IDXGIDevice>();
            using var adapter = dxgiDevice.GetAdapter();
            using var factory = adapter.GetParent<IDXGIFactory2>();

            var baseDescription = D3D11SwapChainFactory.CreateCompositionSwapChainDescription(width, height, AlphaMode.Ignore);
            _baseSwapChain = factory.CreateSwapChainForComposition(deviceResources.Device, baseDescription);
            if (_baseSwapChain == null)
            {
                Cleanup();
                return false;
            }

            var overlayDescription = D3D11SwapChainFactory.CreateCompositionSwapChainDescription(width, height, AlphaMode.Premultiplied);
            var overlaySwapChain = factory.CreateSwapChainForComposition(deviceResources.Device, overlayDescription);
            if (overlaySwapChain == null || !_target.Create(dxgiDevice, hwnd, _baseSwapChain, overlaySwapChain, width, height))
            {
                Cleanup();
                return false;
            }

            _targetTexture = _baseSwapChain.GetBuffer<ID3D11Texture2D>(0);
            _hwnd = hwnd;
            Width = width;
            Height = height;
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VorticeD3D11] Failed to create DirectComposition backend: {ex.Message}");
            Cleanup();
            return false;
        }
    }

    public void SetOutputSize(int width, int height, int contentWidth, int contentHeight, bool liveResize)
    {
        _target.SetOutputSize(width, height, contentWidth, contentHeight, liveResize);
    }

    public void Present(int liveResizeFlag, bool syncImmediate)
    {
        D3D11SwapChainPresenter.Present(
            _baseSwapChain,
            _configuration,
            liveResizeFlag,
            syncImmediate,
            _diagnostics);
    }

    public void Commit()
    {
        _target.Commit();
    }

    public void PresentOverlayAndCommit(uint syncInterval = 1)
    {
        _target.OverlaySwapChain?.Present(syncInterval, (int)PresentFlags.None);
        Commit();
    }

    public void ReacquireOverlayBackBuffer()
    {
        _target.ReacquireOverlayBackBuffer();
    }

    public void ClearOverlayToTransparent(D3D11DeviceResources deviceResources)
    {
        if (deviceResources.Device == null || deviceResources.Context == null || _target.OverlaySwapChain == null)
        {
            return;
        }

        try
        {
            // Clear every flip-model back buffer. The overlay is not necessarily presented
            // every base frame, so an uncleared stale back buffer can otherwise reappear as
            // transparency/ghosting when DWM samples the overlay chain.
            for (var bufferIndex = 0; bufferIndex < D3D11SwapChainFactory.DirectCompositionBufferCount; bufferIndex++)
            {
                ReacquireOverlayBackBuffer();
                if (_target.OverlayTexture == null)
                {
                    return;
                }

                using var renderTargetView = deviceResources.Device.CreateRenderTargetView(_target.OverlayTexture);
                deviceResources.Context.ClearRenderTargetView(renderTargetView, new Color4(0f, 0f, 0f, 0f));
                deviceResources.Context.Flush();
                _target.OverlaySwapChain.Present(0, (int)PresentFlags.None);
            }

            ReacquireOverlayBackBuffer();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VorticeD3D11] DirectComposition overlay clear failed: {ex.Message}");
        }
    }

    public bool UploadOverlayFrame(
        D3D11DeviceResources deviceResources,
        D3D11TextureUploadCache cpuUploadCache,
        D3D11VideoProcessorPresenter videoProcessorPresenter,
        ReadOnlySpan<byte> pixelData,
        int width,
        int height,
        int stride,
        bool liveResize,
        Action<double, int, int> recordUploadDiagnostics)
    {
        if (!IsActive
            || deviceResources.Context == null
            || _target.OverlaySwapChain == null
            || width <= 0
            || height <= 0
            || stride < width * 4
            || pixelData.IsEmpty)
        {
            if (_configuration.EnableVerboseLogging)
            {
                Debug.WriteLine($"[CDG-DC-TRACE] UploadDirectCompositionOverlayFrame -> false (early: active={IsActive}, ctx={deviceResources.Context != null}, swapChain={_target.OverlaySwapChain != null}, {width}x{height} stride={stride}, data={!pixelData.IsEmpty})");
            }
            return false;
        }

        if (_configuration.EnableVerboseLogging)
        {
            Debug.WriteLine($"[CDG-DC-TRACE] UploadDirectCompositionOverlayFrame entering: {width}x{height} into {Width}x{Height}, resize={liveResize}");
        }

        try
        {
            var uploadStart = Stopwatch.GetTimestamp();

            // The overlay swap-chain is updated only when overlay graphics change, unlike the
            // base video swap-chain which receives a new frame every video tick. With a flip-model
            // composition swap-chain, stale back buffers can reappear if only one buffer is updated.
            // Prime every overlay buffer with the same overlay frame.
            for (var bufferIndex = 0; bufferIndex < D3D11SwapChainFactory.DirectCompositionBufferCount; bufferIndex++)
            {
                ReacquireOverlayBackBuffer();
                if (_target.OverlayTexture == null)
                {
                    if (_configuration.EnableVerboseLogging)
                    {
                        Debug.WriteLine($"[CDG-DC-TRACE] UploadDirectCompositionOverlayFrame -> false (null overlay texture at bufferIndex={bufferIndex}, resize={liveResize})");
                    }
                    return false;
                }

                if (width == Width && height == Height)
                {
                    deviceResources.Context.UpdateSubresource(pixelData, _target.OverlayTexture, 0, (uint)stride, 0, null);
                    deviceResources.Context.Flush();
                }
                else
                {
                    var device = deviceResources.Device;
                    if (device == null || !cpuUploadCache.Ensure(device, width, height) || cpuUploadCache.Texture == null)
                    {
                        if (_configuration.EnableVerboseLogging)
                        {
                            Debug.WriteLine($"[CDG-DC-TRACE] UploadDirectCompositionOverlayFrame -> false (blit path failed, bufferIndex={bufferIndex}, resize={liveResize})");
                        }
                        return false;
                    }

                    deviceResources.Context.UpdateSubresource(pixelData, cpuUploadCache.Texture, 0, (uint)stride, 0, null);
                    if (!BlitTextureToOverlay(deviceResources, videoProcessorPresenter, cpuUploadCache.Texture, width, height, 60.0))
                    {
                        if (_configuration.EnableVerboseLogging)
                        {
                            Debug.WriteLine($"[CDG-DC-TRACE] UploadDirectCompositionOverlayFrame -> false (blit failed, bufferIndex={bufferIndex}, resize={liveResize})");
                        }
                        return false;
                    }
                }

                var syncInterval = (bufferIndex == D3D11SwapChainFactory.DirectCompositionBufferCount - 1 && !liveResize) ? 1u : 0u;
                var result = _target.OverlaySwapChain.Present(syncInterval, (int)PresentFlags.None);
                if (result.Failure)
                {
                    Debug.WriteLine($"[VorticeD3D11] DirectComposition overlay Present failed: {result}");
                    if (_configuration.EnableVerboseLogging)
                    {
                        Debug.WriteLine($"[CDG-DC-TRACE] UploadDirectCompositionOverlayFrame -> false (Present failed at bufferIndex={bufferIndex}, resize={liveResize})");
                    }
                    return false;
                }
            }

            ReacquireOverlayBackBuffer();
            Commit();
            recordUploadDiagnostics(D3D11PresentationDiagnostics.ElapsedMilliseconds(uploadStart), width, height);
            if (_configuration.EnableVerboseLogging)
            {
                Debug.WriteLine($"[CDG-DC-TRACE] UploadDirectCompositionOverlayFrame -> true (commit done, {width}x{height}, resize={liveResize})");
            }
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VorticeD3D11] DirectComposition overlay upload failed: {ex.Message}");
            if (_configuration.EnableVerboseLogging)
            {
                Debug.WriteLine($"[CDG-DC-TRACE] UploadDirectCompositionOverlayFrame -> exception: {ex.Message}, resize={liveResize}");
            }
            return false;
        }
    }


    private bool BlitTextureToOverlay(
        D3D11DeviceResources deviceResources,
        D3D11VideoProcessorPresenter videoProcessorPresenter,
        ID3D11Texture2D sourceTexture,
        int width,
        int height,
        double framesPerSecond)
    {
        if (_target.OverlayTexture == null || deviceResources.Context == null || width <= 0 || height <= 0 || Width <= 0 || Height <= 0)
        {
            return false;
        }

        // DirectComposition overlay frames are already prepared in overlay coordinates.
        // Copy them into the alpha overlay swap-chain with Stretch.Fill; do not use the
        // primary video display mode or background-media zoom policy for this target.
        return videoProcessorPresenter.BlitTextureToPresentation(
            deviceResources,
            _target.OverlayTexture,
            sourceTexture,
            sourceArraySlice: 0,
            width,
            height,
            framesPerSecond,
            Width,
            Height,
            Stretch.Fill);
    }

    public void Cleanup()
    {
        _targetTexture?.Dispose();
        _targetTexture = null;
        _baseSwapChain?.Dispose();
        _baseSwapChain = null;
        _target.Dispose();
        _hwnd = IntPtr.Zero;
        Width = 0;
        Height = 0;
    }

    public void Dispose()
    {
        Cleanup();
    }
}
