using System.Diagnostics;
using FatzBombMedia.MediaControl.Core.Configuration;
using Vortice.Direct3D11;
using Vortice.DXGI;
using FatzBombMedia.MediaControl.Core.Graphics.D3D.Device;
using FatzBombMedia.MediaControl.Core.Graphics.D3D.Diagnostics;
using FatzBombMedia.MediaControl.Core.Graphics.D3D.Presentation;
using FatzBombMedia.MediaControl.Core.Graphics.D3D.SwapChain;

namespace FatzBombMedia.MediaControl.Core.Graphics.D3D.Presentation.NativeHwnd;

/// <summary>
/// Owns the native HWND swap-chain presentation backend: swap-chain creation, queued resize,
/// back-buffer ownership, and Present policy.  This keeps native-HWND-specific state out of
/// the renderer coordinator.
/// </summary>
internal sealed class D3D11NativeHwndSwapChainBackend(
    MediaPlayerConfiguration configuration,
    D3D11PresentationDiagnostics diagnostics,
    Action resetVideoProcessorState,
    Action releasePresentationRenderTargetView,
    Action resetOverlayTransientFailure) : ID3D11PresentationBackend
{
    private IDXGISwapChain? _swapChain;
    private ID3D11Texture2D? _targetTexture;
    private IntPtr _hwnd;
    private int _pendingWidth;
    private int _pendingHeight;
    private bool _resizePending;

    public D3D11PresentationBackendKind Kind => D3D11PresentationBackendKind.NativeHwndSwapChain;
    public bool IsActive => _swapChain != null && _targetTexture != null && _hwnd != IntPtr.Zero;
    public int Width { get; private set; }
    public int Height { get; private set; }
    public ID3D11Texture2D? TargetTexture => _targetTexture;
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
            using var factory = adapter.GetParent<IDXGIFactory>();

            _swapChain = D3D11SwapChainFactory.CreateBestNativeSwapChain(factory, deviceResources.Device, hwnd, width, height, configuration);
            if (_swapChain == null)
            {
                Cleanup();
                return false;
            }

            _targetTexture = _swapChain.GetBuffer<ID3D11Texture2D>(0);
            _hwnd = hwnd;
            Width = width;
            Height = height;
            _resizePending = false;
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VorticeD3D11] Failed to create native HWND swap-chain backend: {ex.Message}");
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
        _resizePending = width != Width || height != Height;
    }

    public bool Resize(int width, int height)
    {
        return ResizeCore(Math.Max(1, width), Math.Max(1, height));
    }

    public bool ApplyPendingResizeIfNeeded()
    {
        if (!IsActive || !_resizePending)
        {
            return true;
        }

        var width = Math.Max(1, _pendingWidth);
        var height = Math.Max(1, _pendingHeight);
        if (width == Width && height == Height)
        {
            _resizePending = false;
            return true;
        }

        return ResizeCore(width, height);
    }

    public void Present(int liveResizeFlag, bool syncImmediate)
    {
        D3D11SwapChainPresenter.Present(
            _swapChain,
            configuration,
            liveResizeFlag,
            syncImmediate,
            diagnostics);
    }

    private bool ResizeCore(int width, int height)
    {
        if (!IsActive || _swapChain == null)
        {
            return false;
        }

        if (width == Width && height == Height)
        {
            _resizePending = false;
            return true;
        }

        try
        {
            // DXGI ResizeBuffers requires all direct and indirect references to the current
            // backbuffer to be released first. The caller-owned render target view and video
            // processor output view are released through callbacks before the buffer resize.
            resetVideoProcessorState();
            releasePresentationRenderTargetView();
            _targetTexture?.Dispose();
            _targetTexture = null;

            _swapChain.ResizeBuffers(
                0,
                (uint)width,
                (uint)height,
                Format.Unknown,
                SwapChainFlags.None);

            _targetTexture = _swapChain.GetBuffer<ID3D11Texture2D>(0);
            Width = width;
            Height = height;
            _resizePending = false;
            resetOverlayTransientFailure();

            // Do not clear/present here. Presenting black during live resize is the flicker.
            // The renderer draws real content into the new backbuffer and presents once.
            if (configuration.EnableVerboseLogging)
            {
                Debug.WriteLine($"[VorticeD3D11] Native HWND swap-chain resized at frame boundary to {width}x{height}");
            }

            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VorticeD3D11] Native HWND swap-chain resize failed: {ex.Message}");
            return false;
        }
    }

    public void Cleanup()
    {
        _targetTexture?.Dispose();
        _targetTexture = null;
        _swapChain?.Dispose();
        _swapChain = null;
        _hwnd = IntPtr.Zero;
        Width = 0;
        Height = 0;
        _pendingWidth = 0;
        _pendingHeight = 0;
        _resizePending = false;
    }

    public void Dispose()
    {
        Cleanup();
    }
}
