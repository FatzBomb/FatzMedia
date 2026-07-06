using System.Diagnostics;
using FatzBombMedia.MediaControl.Core.Configuration;
using Vortice.DirectComposition;
using Vortice.Direct3D11;
using Vortice.DXGI;
using SharpGen.Runtime;

namespace FatzBombMedia.MediaControl.Core.Graphics.D3D.Presentation.DirectComposition;

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
            _ = _compositionDevice.CreateTargetForHwnd(hwnd, true, out _compositionTarget);
            _rootVisual = _compositionDevice.CreateVisual();
            _baseVisual = _compositionDevice.CreateVisual();
            _overlayVisual = _compositionDevice.CreateVisual();

            _ = _baseVisual.SetContent(baseSwapChain);
            _ = _overlayVisual.SetContent(overlaySwapChain);
            _ = _rootVisual.AddVisual(_baseVisual, false, null);
            _ = _rootVisual.AddVisual(_overlayVisual, true, _baseVisual);
            _ = _compositionTarget.SetRoot(_rootVisual);

            OverlayTexture = overlaySwapChain.GetBuffer<ID3D11Texture2D>(0);
            _outputWidth = Math.Max(1, contentWidth);
            _outputHeight = Math.Max(1, contentHeight);
            ApplyTransforms(contentWidth, contentHeight);
            _ = _compositionDevice.Commit();
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VorticeD3D11] DirectComposition target creation failed: {ex.Message}");
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Error, "D3D11", $"DirectComposition target creation failed: {ex.Message}", innerException: ex);
            Dispose();
            return false;
        }
    }

    public void SetContentSize(int contentWidth, int contentHeight)
    {
        if (!IsActive || _compositionDevice == null || contentWidth <= 0 || contentHeight <= 0)
        {
            return;
        }

        _outputWidth = Math.Max(1, contentWidth);
        _outputHeight = Math.Max(1, contentHeight);
        ApplyTransforms(contentWidth, contentHeight);
        _ = _compositionDevice.Commit();
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

    public void Commit() => _compositionDevice?.Commit();

    private void ApplyTransforms(int contentWidth, int contentHeight)
    {
        if (!IsActive || _rootVisual == null || contentWidth <= 0 || contentHeight <= 0)
        {
            return;
        }

        var outputWidth = _outputWidth > 0 ? _outputWidth : contentWidth;
        var outputHeight = _outputHeight > 0 ? _outputHeight : contentHeight;
        var scaleX = (float)outputWidth / contentWidth;
        var scaleY = (float)outputHeight / contentHeight;
        var matrix = System.Numerics.Matrix3x2.CreateScale(scaleX, scaleY);
        Result result = _rootVisual.SetTransform(matrix);
        if (result.Failure && configuration.EnableLogging)
        {
            Debug.WriteLine($"[VorticeD3D11] DirectComposition root transform update failed: {result}");
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
    }
}
