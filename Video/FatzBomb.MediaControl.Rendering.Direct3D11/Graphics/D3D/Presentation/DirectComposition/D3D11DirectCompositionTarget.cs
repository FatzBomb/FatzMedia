using System.Diagnostics;
using FatzBomb.MediaControl.Core.Configuration;
using FatzBomb.MediaControl.Core.Graphics.D3D.Diagnostics;
using Vortice.DirectComposition;
using Vortice.Direct3D11;
using Vortice.DXGI;
using SharpGen.Runtime;

namespace FatzBomb.MediaControl.Core.Graphics.D3D.Presentation.DirectComposition;

/// <summary>
/// Owns DirectComposition visual-tree state for the optional native layered
/// presentation backend.  This keeps composition visuals/transforms separate
/// from the main D3D11 renderer coordinator.
/// </summary>
internal sealed class D3D11DirectCompositionTarget(MediaPlayerConfiguration configuration) : IDisposable
{
    private IDCompositionDevice? _compositionDevice;
    private IDCompositionTarget? _compositionTarget;
    private IDCompositionVisual? _rootVisual;
    private IDCompositionVisual? _baseVisual;
    private IDCompositionVisual? _overlayVisual;
    private int _outputWidth;
    private int _outputHeight;
    private bool _deviceLostReported;

    public IDXGISwapChain1? OverlaySwapChain { get; private set; }
    public ID3D11Texture2D? OverlayTexture { get; private set; }
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

            Result createTargetResult = _compositionDevice.CreateTargetForHwnd(hwnd, true, out _compositionTarget);
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
            _outputWidth = Math.Max(1, contentWidth);
            _outputHeight = Math.Max(1, contentHeight);
            if (!ApplyTransforms(contentWidth, contentHeight) || !CommitCore("Create"))
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
            Result removedReason = GetDeviceRemovedReason(baseSwapChain);
            string category = D3D11DeviceLostClassifier.IsDeviceLost(removedReason)
                ? "D3D11.DeviceLost"
                : "D3D11";
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Error, category, $"DirectComposition target creation failed: {ex.Message}", innerException: ex);
            Dispose();
            return false;
        }
    }

    private static void ThrowIfCompositionCallFailed(Result result, string operation)
    {
        if (result.Success)
            return;

        string message = $"DirectComposition {operation} failed with {result}";
        if (D3D11DeviceLostClassifier.IsDeviceLost(result))
            throw new DirectCompositionDeviceLostException(message);

        throw new InvalidOperationException(message);
    }

    private sealed class DirectCompositionDeviceLostException(string message) : Exception(message);

    public void SetContentSize(int contentWidth, int contentHeight)
    {
        if (!IsActive || _compositionDevice == null || contentWidth <= 0 || contentHeight <= 0)
        {
            return;
        }

        _outputWidth = Math.Max(1, contentWidth);
        _outputHeight = Math.Max(1, contentHeight);
        if (ApplyTransforms(contentWidth, contentHeight))
            _ = CommitCore("SetContentSize");
    }

    public void ReleaseOverlayBackBuffer()
    {
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

    private bool CommitCore(string caller)
    {
        if (_compositionDevice == null)
        {
            return false;
        }

        Result result = _compositionDevice.Commit();
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
            else if (configuration.EnableLogging)
            {
                Debug.WriteLine($"[VorticeD3D11] DirectComposition {caller} Commit failed: {result}");
            }

            return false;
        }

        _deviceLostReported = false;
        return true;
    }

    private bool ApplyTransforms(int contentWidth, int contentHeight)
    {
        if (!IsActive || _rootVisual == null || contentWidth <= 0 || contentHeight <= 0)
        {
            return false;
        }

        var outputWidth = _outputWidth > 0 ? _outputWidth : contentWidth;
        var outputHeight = _outputHeight > 0 ? _outputHeight : contentHeight;
        var scaleX = (float)outputWidth / contentWidth;
        var scaleY = (float)outputHeight / contentHeight;
        var matrix = System.Numerics.Matrix3x2.CreateScale(scaleX, scaleY);
        Result result = _rootVisual.SetTransform(matrix);
        if (result.Failure)
        {
            if (D3D11DeviceLostClassifier.IsDeviceLost(result))
            {
                if (!_deviceLostReported)
                {
                    _deviceLostReported = true;
                    DiagnosticRouter.Raise(MediaDiagnosticLevel.Error, "D3D11.DeviceLost", $"DirectComposition root transform update detected GPU device lost: {D3D11DeviceLostClassifier.Describe(result, swapChain: null)}");
                }
            }
            else if (configuration.EnableLogging)
            {
                Debug.WriteLine($"[VorticeD3D11] DirectComposition root transform update failed: {result}");
            }

            return false;
        }

        return true;
    }

    private static Result GetDeviceRemovedReason(IDXGISwapChain swapChain)
    {
        try
        {
            using ID3D11Device? device = swapChain.GetDevice<ID3D11Device>();
            return device?.DeviceRemovedReason ?? default;
        }
        catch
        {
            return default;
        }
    }

    public void Dispose()
    {
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
        _outputWidth = 0;
        _outputHeight = 0;
        _deviceLostReported = false;
    }
}
