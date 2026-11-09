using System.Diagnostics;
using FatzMedia.Video.Core.Configuration;
using FatzMedia.Video.Core.Graphics.D3D.Device;
using FatzMedia.Video.Core.Graphics.D3D.Diagnostics;
using FatzMedia.Video.Core.Graphics.D3D.Presentation.Hdr;
using FatzMedia.Video.Core.Graphics.D3D.SwapChain;
using FatzMedia.Video.Core.Graphics.Rendering;
using FatzMedia.Video.Core.Video.Hdr;
using FatzMedia.Video.Direct3D11;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using SharpGen.Runtime;

namespace FatzMedia.Video.Core.Graphics.D3D.Presentation.DirectComposition;

/// <summary>
/// Owns the current DirectComposition presentation backend: a base composition swap-chain
/// for the main video/image content plus an alpha overlay swap-chain for retained overlay
/// graphics.  This is still the existing single-target/base+overlay implementation; the
/// future true layered DirectComposition backend should replace this class rather than
/// adding more DirectComposition conditionals to VorticeD3D11Renderer.
/// </summary>
internal sealed class D3D11DirectCompositionSingleTargetBackend(
    MediaPlayerConfiguration configuration,
    NativeSwapChainPresentMode presentMode,
    D3D11PresentationDiagnostics diagnostics,
    Action resetVideoProcessorState) : ID3D11PresentationBackend, ID3D11HdrOutputBackend
{
    private readonly D3D11DirectCompositionTarget _target = new();

    private IDXGISwapChain? _baseSwapChain;
    private int _pendingWidth;
    private int _pendingHeight;
    private bool _baseDeviceLostReported;
    private bool _overlayDeviceLostReported;
    // Failures other than device loss that can repeat every frame or resize retry: one report each.
    private readonly RendererDiagnosticLatch _resizeFailure = new();
    private readonly RendererDiagnosticLatch _overlayPresentFailure = new();
    private readonly RendererDiagnosticLatch _basePresentFailure = new();

    public D3D11PresentationBackendKind Kind => D3D11PresentationBackendKind.DirectCompositionSingleTarget;
    public bool IsActive => _target.IsActive && _baseSwapChain != null && TargetTexture != null && Hwnd != IntPtr.Zero;
    public int Width { get; private set; }
    public int Height { get; private set; }
    public ID3D11Texture2D? TargetTexture { get; private set; }
    public ID3D11Texture2D? OverlayTexture => _target.OverlayTexture;
    public D3D11TargetViewCache TargetViews { get; } = new();

    /// <summary>The cached render-target view of <see cref="OverlayTexture"/>.</summary>
    public D3D11TargetViewCache OverlayTargetViews => _target.OverlayTargetViews;
    public IntPtr Hwnd { get; private set; }
    public bool HasPendingResize { get; private set; }

    /// <inheritdoc />
    /// <remarks>The base swap chain's; the overlay swap chain (CD+G split) always stays SDR, and DWM composes it at the SDR white level.</remarks>
    public HdrOutputMode OutputMode { get; private set; }

    /// <inheritdoc />
    public string? HdrOutputFailure { get; private set; }

    private Hdr10MetadataValues? _hdrMetadata;
    // Borrowed from the renderer (not owned): re-creates the swap chain in SDR when going back
    // from scRGB fails.
    private D3D11DeviceResources? _deviceResources;

    // After a failed ResizeBuffers the resize stays pending but is retried only from here on.
    private long _resizeRetryNotBefore;

    public bool Matches(IntPtr hwnd, int width, int height) => IsActive && hwnd == Hwnd && width == Width && height == Height;

    public bool Create(D3D11DeviceResources deviceResources, IntPtr hwnd, int width, int height)
    {
        if (deviceResources.Device == null || hwnd == IntPtr.Zero || width <= 0 || height <= 0)
        {
            return false;
        }

        Cleanup();
        _deviceResources = deviceResources;

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
            OutputMode = D3D11HdrSwapChain.ModeOf(TargetTexture);
            Hwnd = hwnd;
            Width = width;
            Height = height;
            HasPendingResize = false;
            _resizeFailure.Reset();
            _overlayPresentFailure.Reset();
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

    public bool ApplyPendingResizeIfNeeded()
    {
        if (!IsActive || !HasPendingResize || Stopwatch.GetTimestamp() < _resizeRetryNotBefore)
            return true;

        var width = Math.Max(1, _pendingWidth);
        var height = Math.Max(1, _pendingHeight);
        if (width == Width && height == Height)
        {
            HasPendingResize = false;
            return true;
        }

        return ResizeCore(width, height);
    }

    private bool ResizeCore(int width, int height)
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
            TargetViews.Release();
            TargetTexture?.Dispose();
            TargetTexture = null;
            _target.ReleaseOverlayBackBuffer();

            Result baseResizeResult = _baseSwapChain.ResizeBuffers(
                0,
                (uint)width,
                (uint)height,
                Format.Unknown,
                SwapChainFlags.None);
            ReportResizeFailure(baseResizeResult, _baseSwapChain, "base", width, height, ref _baseDeviceLostReported);

            Result overlayResizeResult = _target.OverlaySwapChain.ResizeBuffers(
                0,
                (uint)width,
                (uint)height,
                Format.Unknown,
                SwapChainFlags.None);
            ReportResizeFailure(overlayResizeResult, _target.OverlaySwapChain, "overlay", width, height, ref _overlayDeviceLostReported);

            if (baseResizeResult.Failure || overlayResizeResult.Failure)
            {
                // Both layers must stay one size: put a chain that did resize back to the old
                // size, present at that size, and leave the resize pending, retried after a
                // short wait.
                _resizeRetryNotBefore = Stopwatch.GetTimestamp() + (Stopwatch.Frequency / 2);
                if (baseResizeResult.Success)
                    _ = _baseSwapChain.ResizeBuffers(0, (uint)Width, (uint)Height, Format.Unknown, SwapChainFlags.None);
                if (overlayResizeResult.Success)
                    _ = _target.OverlaySwapChain.ResizeBuffers(0, (uint)Width, (uint)Height, Format.Unknown, SwapChainFlags.None);

                TargetTexture = _baseSwapChain.GetBuffer<ID3D11Texture2D>(0);
                _target.ReacquireOverlayBackBuffer();
                return true;
            }

            TargetTexture = _baseSwapChain.GetBuffer<ID3D11Texture2D>(0);
            _target.ReacquireOverlayBackBuffer();
            if (OutputMode == HdrOutputMode.ScRgb)
                D3D11HdrSwapChain.ReapplyScRgbColorSpace(_baseSwapChain);
            Width = width;
            Height = height;
            HasPendingResize = false;
            _resizeFailure.Reset();
            _target.SetContentSize(width, height);
            diagnostics.RecordNativeResize(D3D11PresentationDiagnostics.ElapsedMilliseconds(resizeStart), width, height);

            if (configuration.EnableVerboseLogging)
                Debug.WriteLine($"[VorticeD3D11] DirectComposition swap-chain buffers resized at frame boundary to {width}x{height}");

            return true;
        }
        catch (Exception ex)
        {
            _ = _resizeFailure.Raise(MediaDiagnosticLevel.Warning, "D3D11", $"DirectComposition resize to {width}x{height} failed: {ex.Message}", ex);
            return false;
        }
    }

    /// <inheritdoc />
    public bool TrySetOutputMode(HdrOutputMode mode, Hdr10MetadataValues? metadata)
    {
        if (!IsActive || _baseSwapChain == null)
            return false;

        if (mode == OutputMode)
        {
            if (mode == HdrOutputMode.ScRgb && metadata != _hdrMetadata)
            {
                D3D11HdrSwapChain.SetMetadata(_baseSwapChain, metadata);
                _hdrMetadata = metadata;
            }

            return true;
        }

        // As for ResizeBuffers: no reference to a base back buffer may remain.
        resetVideoProcessorState();
        TargetViews.Release();
        TargetTexture?.Dispose();
        TargetTexture = null;

        var applied = D3D11HdrSwapChain.TryApply(_baseSwapChain, Width, Height, mode, metadata, out var failure);
        try
        {
            TargetTexture = _baseSwapChain.GetBuffer<ID3D11Texture2D>(0);
        }
        catch (Exception ex)
        {
            failure ??= $"GetBuffer failed: {ex.Message}";
            applied = false;
        }

        OutputMode = D3D11HdrSwapChain.ModeOf(TargetTexture);
        _hdrMetadata = applied && mode == HdrOutputMode.ScRgb ? metadata : null;
        if (!applied)
            HdrOutputFailure = failure ?? "the swap chain could not change its format";

        // Going back to SDR failed and the buffers are still FP16: SDR content cannot be drawn
        // into them by every path, so re-create the swap chain in SDR, as device-loss recovery
        // does. The failure stays, keeping this swap chain SDR.
        if (!applied && mode == HdrOutputMode.Sdr && OutputMode == HdrOutputMode.ScRgb)
            applied = RecreateInSdr(HdrOutputFailure);

        return applied;
    }

    private bool RecreateInSdr(string? failure)
    {
        if (_deviceResources is not { } deviceResources)
            return false;

        (IntPtr hwnd, int width, int height) = (Hwnd, Width, Height);
        (bool pending, int pendingWidth, int pendingHeight) = (HasPendingResize, _pendingWidth, _pendingHeight);
        if (!Create(deviceResources, hwnd, width, height))
            return false;

        (HasPendingResize, _pendingWidth, _pendingHeight) = (pending, pendingWidth, pendingHeight);
        HdrOutputFailure = failure;
        // As on activation: a new overlay chain starts transparent.
        ClearOverlayToTransparent(deviceResources);
        _ = PresentOverlayAndCommit();
        return OutputMode == HdrOutputMode.Sdr;
    }

    private void ReportResizeFailure(Result result, IDXGISwapChain? swapChain, string chainName, int width, int height, ref bool alreadyReported)
    {
        if (result.Failure)
        {
            if (D3D11DeviceLostClassifier.IsDeviceLost(result))
            {
                // Latch shared with Present for the same chain: same loss episode.
                if (!alreadyReported)
                {
                    alreadyReported = true;
                    DiagnosticRouter.Raise(MediaDiagnosticLevel.Error, "D3D11.DeviceLost", $"DirectComposition {chainName} ResizeBuffers detected GPU device lost: {D3D11DeviceLostClassifier.Describe(result, swapChain)}");
                }
            }
            else
                _ = _resizeFailure.Raise(MediaDiagnosticLevel.Warning, "D3D11", $"DirectComposition {chainName} ResizeBuffers to {width}x{height} failed: {result}; presenting at {Width}x{Height} and retrying.");

            return;
        }

        alreadyReported = false;
    }

    public bool Present(int liveResizeFlag, bool syncImmediate)
    {
        if (!D3D11SwapChainPresenter.Present(
                _baseSwapChain,
                presentMode,
                liveResizeFlag,
                syncImmediate,
                diagnostics,
                ref _baseDeviceLostReported,
                _basePresentFailure))
        {
            // A failed base present means the composed frame did not reach the target; skip the
            // DirectComposition commit so the failing present loop does not double-report.
            return false;
        }

        // No GetBuffer(0) after the present (ENG-026): in Direct3D 11 buffer 0 of a flip-model
        // swap chain always names the current back buffer, as the native HWND backend relies on,
        // so the wrapper and its cached render-target view stay valid until a resize.
        return _target.Commit();
    }

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
                _ = _overlayPresentFailure.Raise(MediaDiagnosticLevel.Warning, "D3D11", $"DirectComposition overlay Present failed: {result}");

            return false;
        }

        _overlayDeviceLostReported = false;
        _overlayPresentFailure.Reset();
        // As for the base chain, buffer 0 stays the current back buffer: no re-wrap (ENG-026).
        return _target.Commit();
    }

    public void ReacquireOverlayBackBuffer() => _target.ReacquireOverlayBackBuffer();

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
                Result result = _target.OverlaySwapChain.Present(0, (int)PresentFlags.None);
                if (result.Failure)
                {
                    if (D3D11DeviceLostClassifier.IsDeviceLost(result))
                    {
                        if (!_overlayDeviceLostReported)
                        {
                            _overlayDeviceLostReported = true;
                            DiagnosticRouter.Raise(MediaDiagnosticLevel.Error, "D3D11.DeviceLost", $"DirectComposition overlay clear detected GPU device lost: {D3D11DeviceLostClassifier.Describe(result, _target.OverlaySwapChain)}");
                        }
                    }
                    else
                    {
                        _ = _overlayPresentFailure.Raise(MediaDiagnosticLevel.Warning, "D3D11", $"DirectComposition overlay clear Present failed: {result}");
                    }

                    return;
                }

                _overlayDeviceLostReported = false;
            }

            ReacquireOverlayBackBuffer();
        }
        catch (Exception ex)
        {
            _ = _overlayPresentFailure.Raise(MediaDiagnosticLevel.Warning, "D3D11", $"DirectComposition overlay clear failed: {ex.Message}", ex);
        }
    }

    public void Cleanup()
    {
        TargetViews.Release();
        TargetTexture?.Dispose();
        TargetTexture = null;
        _baseSwapChain?.Dispose();
        _baseSwapChain = null;
        OutputMode = HdrOutputMode.Sdr;
        HdrOutputFailure = null;
        _hdrMetadata = null;
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
