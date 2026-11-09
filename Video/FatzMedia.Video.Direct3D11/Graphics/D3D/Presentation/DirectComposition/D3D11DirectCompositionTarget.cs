using FatzMedia.Video.Core.Graphics.D3D.Diagnostics;
using FatzMedia.Video.Core.Graphics.Rendering;
using SharpGen.Runtime;
using Vortice.Direct3D11;
using Vortice.DirectComposition;
using Vortice.DXGI;

namespace FatzMedia.Video.Core.Graphics.D3D.Presentation.DirectComposition;

/// <summary>
/// Owns DirectComposition visual-tree state for the optional native layered
/// presentation backend.  This keeps composition visuals separate from the main
/// D3D11 renderer coordinator. Both swap chains are always the window's size, so the
/// visuals carry no transform.
/// </summary>
sealed class D3D11DirectCompositionTarget : IDisposable
{
    IDCompositionDevice? _compositionDevice;
    IDCompositionTarget? _compositionTarget;
    IDCompositionVisual? _rootVisual;
    IDCompositionVisual? _baseVisual;
    IDCompositionVisual? _overlayVisual;
    bool _deviceLostReported;
    // Commit failures other than device loss repeat every frame: one report.
    readonly RendererDiagnosticLatch _commitFailure = new();

    public IDXGISwapChain1? OverlaySwapChain { get; private set; }
    public ID3D11Texture2D? OverlayTexture { get; private set; }

    /// <summary>The cached render-target view of <see cref="OverlayTexture"/>, released with it.</summary>
    public D3D11TargetViewCache OverlayTargetViews { get; } = new();
    public bool IsActive => _compositionDevice != null && OverlaySwapChain != null;

    public bool Create(
        IDXGIDevice dxgiDevice,
        IntPtr hwnd,
        IDXGISwapChain baseSwapChain,
        IDXGISwapChain1 overlaySwapChain,
        int contentWidth,
        int contentHeight)
    {
        Dispose();

        try
        {
            // Take ownership immediately so any failure path can cleanly release it.
            OverlaySwapChain = overlaySwapChain;
            _compositionDevice = DComp.DCompositionCreateDevice<IDCompositionDevice>(dxgiDevice);

            var createTargetResult = _compositionDevice.CreateTargetForHwnd(hwnd, true, out _compositionTarget);
            ThrowIfCompositionCallFailed(createTargetResult, "CreateTargetForHwnd");

            _rootVisual = _compositionDevice.CreateVisual();
            _baseVisual = _compositionDevice.CreateVisual();
            _overlayVisual = _compositionDevice.CreateVisual();

            ThrowIfCompositionCallFailed(_baseVisual.SetContent(baseSwapChain), "SetContent (base)");
            ThrowIfCompositionCallFailed(_overlayVisual.SetContent(overlaySwapChain), "SetContent (overlay)");
            ThrowIfCompositionCallFailed(_rootVisual.AddVisual(_baseVisual, false, null), "AddVisual (base)");
            ThrowIfCompositionCallFailed(_rootVisual.AddVisual(_overlayVisual, true, _baseVisual), "AddVisual (overlay)");
            ThrowIfCompositionCallFailed(_compositionTarget.SetRoot(_rootVisual), "SetRoot");

            OverlayTexture = overlaySwapChain.GetBuffer<ID3D11Texture2D>(0);
            if (contentWidth <= 0 || contentHeight <= 0 || !CommitCore("Create"))
            {
                Dispose();
                return false;
            }

            return true;
        }
        catch (DirectCompositionDeviceLostException ex)
        {
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Error, "D3D11.DeviceLost", ex.Message, innerException: ex);
            Dispose();
            return false;
        }
        catch (Exception ex)
        {
            var removedReason = GetDeviceRemovedReason(baseSwapChain);
            var category = D3D11DeviceLostClassifier.IsDeviceLost(removedReason)
                ? "D3D11.DeviceLost"
                : "D3D11";
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Error, category, $"DirectComposition target creation failed: {ex.Message}", innerException: ex);
            Dispose();
            return false;
        }
    }

    static void ThrowIfCompositionCallFailed(Result result, string operation)
    {
        if (result.Success)
            return;

        var message = $"DirectComposition {operation} failed with {result}";
        if (D3D11DeviceLostClassifier.IsDeviceLost(result))
            throw new DirectCompositionDeviceLostException(message);

        throw new InvalidOperationException(message);
    }

    sealed class DirectCompositionDeviceLostException(string message) : Exception(message);

    /// <summary>Commits the visual tree after both swap chains were resized to <paramref name="contentWidth"/> x <paramref name="contentHeight"/>.</summary>
    public void SetContentSize(int contentWidth, int contentHeight)
    {
        if (!IsActive || contentWidth <= 0 || contentHeight <= 0)
        {
            return;
        }

        _ = CommitCore("SetContentSize");
    }

    public void ReleaseOverlayBackBuffer()
    {
        OverlayTargetViews.Release();
        OverlayTexture?.Dispose();
        OverlayTexture = null;
    }

    public void ReacquireOverlayBackBuffer()
    {
        if (OverlaySwapChain == null)
        {
            return;
        }

        ReleaseOverlayBackBuffer();
        OverlayTexture = OverlaySwapChain.GetBuffer<ID3D11Texture2D>(0);
    }

    public bool Commit() => CommitCore("Commit");

    bool CommitCore(string caller)
    {
        if (_compositionDevice == null)
        {
            return false;
        }

        var result = _compositionDevice.Commit();
        if (result.Failure)
        {
            if (D3D11DeviceLostClassifier.IsDeviceLost(result))
            {
                // Latch owned by this instance: report once per loss episode so repeated
                // Commit calls (every frame, via the per-frame Commit path) do not spam.
                if (!_deviceLostReported)
                {
                    _deviceLostReported = true;
                    DiagnosticRouter.Raise(MediaDiagnosticLevel.Error, "D3D11.DeviceLost", $"DirectComposition {caller} Commit detected GPU device lost: {D3D11DeviceLostClassifier.Describe(result, swapChain: null)}");
                }
            }
            else
            {
                _ = _commitFailure.Raise(MediaDiagnosticLevel.Warning, "D3D11", $"DirectComposition {caller} Commit failed: {result}");
            }

            return false;
        }

        _deviceLostReported = false;
        _commitFailure.Reset();
        return true;
    }

    static Result GetDeviceRemovedReason(IDXGISwapChain swapChain)
    {
        try
        {
            using var device = swapChain.GetDevice<ID3D11Device>();
            return device?.DeviceRemovedReason ?? default;
        }
        catch
        {
            return default;
        }
    }

    public void Dispose()
    {
        OverlayTargetViews.Release();
        OverlayTexture?.Dispose();
        OverlayTexture = null;
        OverlaySwapChain?.Dispose();
        OverlaySwapChain = null;
        _overlayVisual?.Dispose();
        _overlayVisual = null;
        _baseVisual?.Dispose();
        _baseVisual = null;
        _rootVisual?.Dispose();
        _rootVisual = null;
        _compositionTarget?.Dispose();
        _compositionTarget = null;
        _compositionDevice?.Dispose();
        _compositionDevice = null;
        _deviceLostReported = false;
    }
}
