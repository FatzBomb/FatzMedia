using System.Diagnostics;
using FatzBombMedia.MediaControl.Core.Configuration;
using FatzBombMedia.MediaControl.Core.Graphics.D3D.Device;
using FatzBombMedia.MediaControl.Core.Graphics.D3D.Diagnostics;
using FatzBombMedia.MediaControl.Core.Graphics.D3D.SwapChain;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using SharpGen.Runtime;

namespace FatzBombMedia.MediaControl.Core.Graphics.D3D.Presentation.DirectComposition;

/// <summary>
/// Owns the current DirectComposition presentation backend: a base composition swap-chain
/// for the main video/image content plus an alpha overlay swap-chain for retained overlay
/// graphics.  This is still the existing single-target/base+overlay implementation; the
/// future true layered DirectComposition backend should replace this class rather than
/// adding more DirectComposition conditionals to VorticeD3D11Renderer.
/// </summary>
internal sealed class D3D11DirectCompositionSingleTargetBackend(
    MediaPlayerConfiguration configuration,
    D3D11PresentationDiagnostics diagnostics,
    Action resetVideoProcessorState) : ID3D11PresentationBackend
{
    private readonly D3D11DirectCompositionTarget _target = new(configuration);

    private IDXGISwapChain? _baseSwapChain;
    private int _pendingWidth;
    private int _pendingHeight;
    private bool _baseDeviceLostReported;
    private bool _overlayDeviceLostReported;

    public D3D11PresentationBackendKind Kind => D3D11PresentationBackendKind.DirectCompositionSingleTarget;
    public bool IsActive => _target.IsActive && _baseSwapChain != null && TargetTexture != null && Hwnd != IntPtr.Zero;
    public int Width { get; private set; }
    public int Height { get; private set; }
    public ID3D11Texture2D? TargetTexture { get; private set; }
    public ID3D11Texture2D? OverlayTexture => _target.OverlayTexture;
    public IntPtr Hwnd { get; private set; }
    public bool HasPendingResize { get; private set; }

    public bool Matches(IntPtr hwnd, int width, int height) => IsActive && hwnd == Hwnd && width == Width && height == Height;

    public bool Create(D3D11DeviceResources deviceResources, IntPtr hwnd, int width, int height)
    {
        if (deviceResources.Device == null || hwnd == IntPtr.Zero || width <= 0 || height <= 0)
        {
            return false;
        }

        Cleanup();

        try
        {
            using IDXGIDevice dxgiDevice = deviceResources.Device.QueryInterface<IDXGIDevice>();
            using IDXGIAdapter adapter = dxgiDevice.GetAdapter();
            using IDXGIFactory2 factory = adapter.GetParent<IDXGIFactory2>();

            SwapChainDescription1 baseDescription = D3D11SwapChainFactory.CreateCompositionSwapChainDescription(width, height, AlphaMode.Premultiplied);
            _baseSwapChain = factory.CreateSwapChainForComposition(deviceResources.Device, baseDescription);
            if (_baseSwapChain == null)
            {
                Cleanup();
                return false;
            }

            SwapChainDescription1 overlayDescription = D3D11SwapChainFactory.CreateCompositionSwapChainDescription(width, height, AlphaMode.Premultiplied);
            IDXGISwapChain1 overlaySwapChain = factory.CreateSwapChainForComposition(deviceResources.Device, overlayDescription);
            if (overlaySwapChain == null || !_target.Create(dxgiDevice, hwnd, _baseSwapChain, overlaySwapChain, width, height))
            {
                Cleanup();
                return false;
            }

            TargetTexture = _baseSwapChain.GetBuffer<ID3D11Texture2D>(0);
            Hwnd = hwnd;
            Width = width;
            Height = height;
            HasPendingResize = false;
            return true;
        }
        catch (Exception ex)
        {
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Error, "D3D11", $"Failed to create DirectComposition backend: {ex.Message}", innerException: ex);
            Cleanup();
            return false;
        }
    }

    public void QueueResize(int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);

        if (!IsActive)
        {
            return;
        }

        _pendingWidth = width;
        _pendingHeight = height;
        HasPendingResize = width != Width || height != Height;
    }

    public bool ApplyPendingResizeIfNeeded(D3D11DeviceResources deviceResources)
    {
        if (!IsActive || !HasPendingResize)
        {
            return true;
        }

        var width = Math.Max(1, _pendingWidth);
        var height = Math.Max(1, _pendingHeight);
        if (width == Width && height == Height)
        {
            HasPendingResize = false;
            return true;
        }

        return ResizeCore(deviceResources, width, height);
    }

    private bool ResizeCore(D3D11DeviceResources deviceResources, int width, int height)
    {
        if (!IsActive || _baseSwapChain == null || _target.OverlaySwapChain == null)
            return false;

        if (width == Width && height == Height)
        {
            HasPendingResize = false;
            return true;
        }

        try
        {
            var resizeStart = Stopwatch.GetTimestamp();

            resetVideoProcessorState();
            TargetTexture?.Dispose();
            TargetTexture = null;
            _target.ReleaseOverlayBackBuffer();

            _ = _baseSwapChain.ResizeBuffers(
                0,
                (uint)width,
                (uint)height,
                Format.Unknown,
                SwapChainFlags.None);

            _ = _target.OverlaySwapChain.ResizeBuffers(
                0,
                (uint)width,
                (uint)height,
                Format.Unknown,
                SwapChainFlags.None);

            TargetTexture = _baseSwapChain.GetBuffer<ID3D11Texture2D>(0);
            _target.ReacquireOverlayBackBuffer();
            Width = width;
            Height = height;
            HasPendingResize = false;
            _target.SetContentSize(width, height);
            diagnostics.RecordNativeResize(D3D11PresentationDiagnostics.ElapsedMilliseconds(resizeStart), width, height);

            if (configuration.EnableVerboseLogging)
                Debug.WriteLine($"[VorticeD3D11] DirectComposition swap-chain buffers resized at frame boundary to {width}x{height}");

            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VorticeD3D11] DirectComposition resize failed: {ex.Message}");
            return false;
        }
    }

    public void Present(int liveResizeFlag, bool syncImmediate)
    {
        D3D11SwapChainPresenter.Present(
            _baseSwapChain,
            configuration,
            liveResizeFlag,
            syncImmediate,
            diagnostics,
            ref _baseDeviceLostReported);

        Commit();
        ReacquireBaseBackBuffer();
    }

    public void Commit() => _target.Commit();

    public bool PresentOverlayAndCommit(uint syncInterval = 1)
    {
        if (_target.OverlaySwapChain == null)
            return false;

        Result result = _target.OverlaySwapChain.Present(syncInterval, (int)PresentFlags.None);
        if (result.Failure)
        {
            if (D3D11DeviceLostClassifier.IsDeviceLost(result))
            {
                // Latch owned by this instance: report once per loss episode so a failing
                // overlay present loop does not spam an identical diagnostic every frame.
                if (!_overlayDeviceLostReported)
                {
                    _overlayDeviceLostReported = true;
                    DiagnosticRouter.Raise(MediaDiagnosticLevel.Error, "D3D11.DeviceLost", $"DirectComposition overlay Present detected GPU device lost: {D3D11DeviceLostClassifier.Describe(result, _target.OverlaySwapChain)}");
                }
            }
            else
            {
                Debug.WriteLine($"[VorticeD3D11] DirectComposition overlay Present failed: {result}");
            }

            return false;
        }

        _overlayDeviceLostReported = false;
        Commit();
        ReacquireOverlayBackBuffer();
        return true;
    }

    public void ReacquireOverlayBackBuffer() => _target.ReacquireOverlayBackBuffer();

    private void ReacquireBaseBackBuffer()
    {
        if (_baseSwapChain == null)
            return;

        TargetTexture?.Dispose();
        TargetTexture = _baseSwapChain.GetBuffer<ID3D11Texture2D>(0);
    }

    public void ClearOverlayToTransparent(D3D11DeviceResources deviceResources)
    {
        if (deviceResources.Device == null || deviceResources.Context == null || _target.OverlaySwapChain == null)
            return;

        try
        {
            // Clear every flip-model back buffer. The overlay is not necessarily presented
            // every base frame, so an uncleared stale back buffer can otherwise reappear as
            // transparency/ghosting when DWM samples the overlay chain.
            for (var bufferIndex = 0; bufferIndex < D3D11SwapChainFactory.DirectCompositionBufferCount; bufferIndex++)
            {
                ReacquireOverlayBackBuffer();
                if (_target.OverlayTexture == null)
                    return;

                using ID3D11RenderTargetView renderTargetView = deviceResources.Device.CreateRenderTargetView(_target.OverlayTexture);
                deviceResources.Context.ClearRenderTargetView(renderTargetView, new Color4(0f, 0f, 0f, 0f));
                deviceResources.Context.Flush();
                _ = _target.OverlaySwapChain.Present(0, (int)PresentFlags.None);
            }

            ReacquireOverlayBackBuffer();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VorticeD3D11] DirectComposition overlay clear failed: {ex.Message}");
        }
    }

    public void Cleanup()
    {
        TargetTexture?.Dispose();
        TargetTexture = null;
        _baseSwapChain?.Dispose();
        _baseSwapChain = null;
        _target.Dispose();
        Hwnd = IntPtr.Zero;
        Width = 0;
        Height = 0;
        _pendingWidth = 0;
        _pendingHeight = 0;
        HasPendingResize = false;
        _baseDeviceLostReported = false;
        _overlayDeviceLostReported = false;
    }

    public void Dispose() => Cleanup();
}
