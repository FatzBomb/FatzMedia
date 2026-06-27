using System.Diagnostics;
using FatzBombMedia.MediaControl.Core.Graphics.D3D.Device;
using FatzBombMedia.MediaControl.Core.Graphics.D3D.Presentation.D3DImage;
using FatzBombMedia.MediaControl.Core.Graphics.D3D.Presentation.DirectComposition;
using FatzBombMedia.MediaControl.Core.Graphics.D3D.Presentation.NativeHwnd;
using Vortice.Direct3D11;
using Vortice.Mathematics;

namespace FatzBombMedia.MediaControl.Core.Graphics.D3D.Presentation;

/// <summary>
/// Owns the three currently-supported D3D11 presentation backends and centralizes the
/// active-backend selection order.  VorticeD3D11Renderer should coordinate rendering work;
/// it should not repeatedly branch on every backend field just to answer basic questions
/// such as "where is the active target texture?" or "does this backend present natively?".
/// </summary>
internal sealed class D3D11PresentationBackendSet : IDisposable
{
    public D3D11PresentationBackendSet(
        D3D11NativeHwndSwapChainBackend nativeHwnd,
        D3D11D3DImagePresentationBackend d3dImage,
        D3D11DirectCompositionSingleTargetBackend directComposition)
    {
        NativeHwnd = nativeHwnd;
        D3DImage = d3dImage;
        DirectComposition = directComposition;
    }

    public D3D11NativeHwndSwapChainBackend NativeHwnd { get; }
    public D3D11D3DImagePresentationBackend D3DImage { get; }
    public D3D11DirectCompositionSingleTargetBackend DirectComposition { get; }

    /// <summary>
    /// Active backend priority is intentional. DirectComposition is the most specific native
    /// layered mode, then plain native HWND, then WPF D3DImage fallback.
    /// </summary>
    public ID3D11PresentationBackend? Active
    {
        get
        {
            if (DirectComposition.IsActive)
            {
                return DirectComposition;
            }

            if (NativeHwnd.IsActive)
            {
                return NativeHwnd;
            }

            return D3DImage.IsActive ? D3DImage : null;
        }
    }

    public D3D11PresentationBackendKind ActiveKind => Active?.Kind ?? D3D11PresentationBackendKind.None;

    public ID3D11Texture2D? ActiveTargetTexture => Active?.TargetTexture;

    public bool HasNativePresentingBackend => ActiveKind is D3D11PresentationBackendKind.NativeHwndSwapChain
        or D3D11PresentationBackendKind.DirectCompositionSingleTarget;

    public bool HasD3DImageBackend => ActiveKind == D3D11PresentationBackendKind.D3DImage;

    public bool OwnsTexture(ID3D11Texture2D? texture)
    {
        return texture != null
            && ((NativeHwnd.IsActive && ReferenceEquals(texture, NativeHwnd.TargetTexture))
                || (DirectComposition.IsActive && ReferenceEquals(texture, DirectComposition.TargetTexture))
                || (D3DImage.IsActive && ReferenceEquals(texture, D3DImage.TargetTexture)));
    }

    public void Present(int liveResizeFlag, bool syncImmediate)
    {
        var active = Active;
        if (active == null || active.Kind == D3D11PresentationBackendKind.D3DImage)
        {
            return;
        }

        active.Present(liveResizeFlag, syncImmediate);
    }

    /// <summary>
    /// Presents or invalidates the active backend after a complete frame has been rendered.
    /// Native backends are DXGI-presented immediately; the WPF backend is notified through
    /// D3DImage dirty-rect invalidation.
    /// </summary>
    public void NotifyFramePresented(
        int liveResizeFlag,
        bool syncImmediate,
        Func<bool> isDisposed,
        Action framePresented)
    {
        var active = Active;
        if (active == null)
        {
            return;
        }

        if (active.Kind == D3D11PresentationBackendKind.D3DImage)
        {
            D3DImage.NotifyDirty(isDisposed, framePresented);
            return;
        }

        active.Present(liveResizeFlag, syncImmediate);
        framePresented();
    }

    /// <summary>
    /// Clears the active backend's base target to black. This is backend-facing presentation
    /// cleanup, used when switching to audio-only media or when a new surface is created.
    /// DirectComposition overlay transparency is intentionally separate because it targets
    /// the overlay swap chain, not the base presentation target.
    /// </summary>
    public bool ClearActiveTargetToBlack(D3D11DeviceResources deviceResources, bool flush = true)
    {
        var target = ActiveTargetTexture;
        if (deviceResources.Device == null || deviceResources.Context == null || target == null)
        {
            return false;
        }

        try
        {
            using var renderTargetView = deviceResources.Device.CreateRenderTargetView(target);
            deviceResources.Context.ClearRenderTargetView(renderTargetView, new Color4(0f, 0f, 0f, 1f));
            if (flush)
            {
                deviceResources.Context.Flush();
            }

            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VorticeD3D11] Presentation target black clear failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Clears the base frame and, for DirectComposition, the retained overlay chain. Then
    /// immediately presents/invalidates the active backend so stale video frames disappear
    /// when the current media has no visual layer.
    /// </summary>
    public void ClearPresentedFrame(
        D3D11DeviceResources deviceResources,
        int liveResizeFlag,
        bool syncImmediate,
        Func<bool> isDisposed,
        Action framePresented)
    {
        if (Active == null)
        {
            return;
        }

        ClearActiveTargetToBlack(deviceResources, flush: false);

        if (DirectComposition.IsActive)
        {
            DirectComposition.ClearOverlayToTransparent(deviceResources);
        }

        NotifyFramePresented(liveResizeFlag, syncImmediate, isDisposed, framePresented);
        deviceResources.Context?.Flush();
    }

    public void Cleanup()
    {
        DirectComposition.Dispose();
        NativeHwnd.Dispose();
        D3DImage.Dispose();
    }

    public void Dispose()
    {
        Cleanup();
    }
}
