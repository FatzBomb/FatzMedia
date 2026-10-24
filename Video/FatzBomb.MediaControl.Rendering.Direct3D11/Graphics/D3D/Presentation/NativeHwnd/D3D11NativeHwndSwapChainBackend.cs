using System.Diagnostics;
using FatzBomb.MediaControl.Core.Configuration;
using Vortice.Direct3D11;
using Vortice.DXGI;
using FatzBomb.MediaControl.Core.Graphics.D3D.Device;
using FatzBomb.MediaControl.Core.Graphics.D3D.Diagnostics;
using FatzBomb.MediaControl.Core.Graphics.D3D.SwapChain;
using FatzBomb.MediaControl.Core.Graphics.Rendering;
using FatzBomb.MediaControl.Rendering.Direct3D11;
using SharpGen.Runtime;

namespace FatzBomb.MediaControl.Core.Graphics.D3D.Presentation.NativeHwnd;

/// <summary>
/// Owns the native HWND swap-chain presentation backend: swap-chain creation, queued resize,
/// back-buffer ownership, and Present policy.  This keeps native-HWND-specific state out of
/// the renderer coordinator.
/// </summary>
internal sealed class D3D11NativeHwndSwapChainBackend(
    MediaPlayerConfiguration configuration,
    NativeSwapChainPresentMode presentMode,
    D3D11PresentationDiagnostics diagnostics,
    Action resetVideoProcessorState,
    Action resetOverlayTransientFailure) : ID3D11PresentationBackend
{
    private IDXGISwapChain? _swapChain;
    private int _pendingWidth;
    private int _pendingHeight;
    private bool _resizePending;
    // After a failed ResizeBuffers the resize stays pending but is retried only from here on.
    private long _resizeRetryNotBefore;
    private bool _deviceLostReported;
    // ResizeBuffers failures other than device loss repeat (retried every half second): one report.
    private readonly RendererDiagnosticLatch _resizeFailure = new();

    public D3D11PresentationBackendKind Kind => D3D11PresentationBackendKind.NativeHwndSwapChain;
    public bool IsActive => _swapChain != null && TargetTexture != null && Hwnd != IntPtr.Zero;
    public int Width { get; private set; }
    public int Height { get; private set; }
    public ID3D11Texture2D? TargetTexture { get; private set; }
    public IntPtr Hwnd { get; private set; }
    public D3D11TargetViewCache TargetViews { get; } = new();

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
            using IDXGIFactory factory = adapter.GetParent<IDXGIFactory>();

            _swapChain = D3D11SwapChainFactory.CreateBestNativeSwapChain(factory, deviceResources.Device, hwnd, width, height, configuration);
            if (_swapChain == null)
            {
                Cleanup();
                return false;
            }

            TargetTexture = _swapChain.GetBuffer<ID3D11Texture2D>(0);
            Hwnd = hwnd;
            Width = width;
            Height = height;
            _resizePending = false;
            _resizeFailure.Reset();
            return true;
        }
        catch (Exception ex)
        {
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Error, "D3D11", $"Failed to create native HWND swap-chain backend: {ex.Message}", innerException: ex);
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

    public bool ApplyPendingResizeIfNeeded()
    {
        if (!IsActive || !_resizePending || Stopwatch.GetTimestamp() < _resizeRetryNotBefore)
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

    /// <summary>
    /// A secondary window's resize (ENG-026): applies the queued <c>ResizeBuffers</c> like the
    /// primary does, and only when that throws (leaving no back buffer) re-creates the swap chain
    /// at the queued size, which is what every secondary resize did before.
    /// </summary>
    public bool ApplyPendingResizeOrRecreate(D3D11DeviceResources deviceResources)
    {
        IntPtr hwnd = Hwnd;
        var pending = _resizePending;
        var width = Math.Max(1, _pendingWidth);
        var height = Math.Max(1, _pendingHeight);
        if (ApplyPendingResizeIfNeeded())
            return true;

        return pending && hwnd != IntPtr.Zero && Create(deviceResources, hwnd, width, height);
    }

    public bool Present(int liveResizeFlag, bool syncImmediate) => D3D11SwapChainPresenter.Present(
            _swapChain,
            presentMode,
            liveResizeFlag,
            syncImmediate,
            diagnostics,
            ref _deviceLostReported);

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
            var resizeStart = Stopwatch.GetTimestamp();

            // DXGI ResizeBuffers requires all direct and indirect references to the current
            // backbuffer to be released first. The video processor output view is released
            // through a callback before the buffer resize.
            resetVideoProcessorState();
            TargetViews.Release();
            TargetTexture?.Dispose();
            TargetTexture = null;

            Result resizeResult = _swapChain.ResizeBuffers(
                0,
                (uint)width,
                (uint)height,
                Format.Unknown,
                SwapChainFlags.None);

            if (resizeResult.Failure)
            {
                if (D3D11DeviceLostClassifier.IsDeviceLost(resizeResult))
                {
                    // Latch shared with Present: same swap chain, same loss episode.
                    if (!_deviceLostReported)
                    {
                        _deviceLostReported = true;
                        DiagnosticRouter.Raise(MediaDiagnosticLevel.Error, "D3D11.DeviceLost", $"Native HWND swap-chain ResizeBuffers detected GPU device lost: {D3D11DeviceLostClassifier.Describe(resizeResult, _swapChain)}");
                    }
                }
                else
                {
                    _ = _resizeFailure.Raise(MediaDiagnosticLevel.Warning, "D3D11", $"Native HWND swap-chain ResizeBuffers to {width}x{height} failed: {resizeResult}; presenting at {Width}x{Height} and retrying.");
                }

                // The buffers keep their old size: present at that size and leave the resize
                // pending, retried after a short wait, instead of drawing a new-size viewport
                // into an old-size buffer.
                _resizeRetryNotBefore = Stopwatch.GetTimestamp() + (Stopwatch.Frequency / 2);
                TargetTexture = _swapChain.GetBuffer<ID3D11Texture2D>(0);
                return true;
            }
            else if (_deviceLostReported)
            {
                _deviceLostReported = false;
            }

            TargetTexture = _swapChain.GetBuffer<ID3D11Texture2D>(0);
            Width = width;
            Height = height;
            _resizePending = false;
            _resizeFailure.Reset();
            resetOverlayTransientFailure();
            diagnostics.RecordNativeResize(D3D11PresentationDiagnostics.ElapsedMilliseconds(resizeStart), width, height);

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
            _ = _resizeFailure.Raise(MediaDiagnosticLevel.Warning, "D3D11", $"Native HWND swap-chain resize to {width}x{height} failed: {ex.Message}", ex);
            return false;
        }
    }

    public void Cleanup()
    {
        TargetViews.Release();
        TargetTexture?.Dispose();
        TargetTexture = null;
        _swapChain?.Dispose();
        _swapChain = null;
        Hwnd = IntPtr.Zero;
        Width = 0;
        Height = 0;
        _pendingWidth = 0;
        _pendingHeight = 0;
        _resizePending = false;
        _deviceLostReported = false;
    }

    public void Dispose() => Cleanup();
}
