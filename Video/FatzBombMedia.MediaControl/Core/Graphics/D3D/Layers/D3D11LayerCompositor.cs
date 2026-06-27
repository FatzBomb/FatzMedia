using FatzBombMedia.MediaControl.Core.Configuration;
using FatzBombMedia.MediaControl.Core.Graphics.D3D.Device;
using Vortice.Direct3D11;

namespace FatzBombMedia.MediaControl.Core.Graphics.D3D.Layers;

/// <summary>
/// Coordinates retained GPU layers that are drawn after the base media frame has been
/// uploaded into the active presentation target.
///
/// Current layer contract:
///   1. Base media/background is drawn before this compositor is called.
///   2. WashLayer is a full-viewport color/opacity layer, currently driven by CDG.
///   3. OverlayLayer is transparent foreground graphics, currently CDG lyrics/graphics,
///      Uniform-fitted inside the full viewport.
///
/// This keeps layer policy out of VorticeD3D11Renderer.  The renderer uploads the base
/// frame, asks this compositor to draw retained layers, then presents the active backend.
/// Future DirectComposition layered backends can implement the same contract without
/// forcing the coordinator to know how each layer is physically presented.
/// </summary>
internal sealed class D3D11LayerCompositor : IDisposable
{
    private readonly D3D11OverlayLayerRenderer _overlayLayerRenderer;

    public D3D11LayerCompositor(MediaPlayerConfiguration configuration)
    {
        _overlayLayerRenderer = new D3D11OverlayLayerRenderer(configuration);
    }

    public bool HasOverlayTexture => _overlayLayerRenderer.HasLyricsTexture;

    public bool UploadOverlayTexture(D3D11DeviceResources deviceResources, ReadOnlySpan<byte> pixelData, int width, int height, int stride)
    {
        var device = deviceResources.Device;
        var context = deviceResources.Context;
        return device != null
            && context != null
            && _overlayLayerRenderer.UploadLyricsTexture(device, context, pixelData, width, height, stride);
    }

    public void ClearOverlayTexture() => _overlayLayerRenderer.ClearLyricsTexture();

    public void ResetTransientOverlayFailure() => _overlayLayerRenderer.ResetTransientFailure();

    /// <summary>
    /// Draws retained overlay layers over the already-uploaded base frame.
    /// </summary>
    /// <param name="requireOverlayGraphics">
    /// True when the caller is presenting a media frame that must include overlay graphics.
    /// If the overlay draw fails in that case, false is returned so the caller can avoid
    /// presenting a video-only frame and let higher-level fallback logic choose another path.
    /// </param>
    public bool CompositeAfterBaseFrame(
        D3D11DeviceResources deviceResources,
        ID3D11RenderTargetView? presentationRenderTargetView,
        int surfaceWidth,
        int surfaceHeight,
        uint washArgb,
        bool requireOverlayGraphics)
    {
        if (!requireOverlayGraphics && !HasOverlayTexture)
        {
            return true;
        }

        var device = deviceResources.Device;
        var context = deviceResources.Context;
        if (device == null || context == null || presentationRenderTargetView == null)
        {
            return !requireOverlayGraphics;
        }

        if (surfaceWidth <= 0 || surfaceHeight <= 0)
        {
            return !requireOverlayGraphics;
        }

        if (!HasOverlayTexture)
        {
            return !requireOverlayGraphics;
        }

        return _overlayLayerRenderer.Draw(
            device,
            context,
            presentationRenderTargetView,
            surfaceWidth,
            surfaceHeight,
            washArgb);
    }

    public void Dispose() => _overlayLayerRenderer.Dispose();
}
