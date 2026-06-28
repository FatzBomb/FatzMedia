using FatzBombMedia.MediaControl.Core.Configuration;
using FatzBombMedia.MediaControl.Core.Graphics.D3D;
using System.Diagnostics;

namespace FatzBombMedia.MediaControl.Core.Graphics;

/// <summary>
/// Centralizes the host-level presentation target routing used by <see cref="OptimizedGraphicsRenderer"/>.
/// This keeps backend selection, host-output sizing, and D3DImage surface sizing in one place while
/// leaving the renderer as the owner of public API state and media-frame orchestration.
/// </summary>
internal sealed class OptimizedPresentationRouter(MediaPlayerConfiguration configuration)
{
    public bool CreateActiveD3D11Surfaces(
        VorticeD3D11Renderer renderer,
        IntPtr nativePresentationHwnd,
        bool directCompositionLayersRequested,
        int requestedNativePresentationWidth,
        int requestedNativePresentationHeight,
        int requestedWpfPresentationWidth,
        int requestedWpfPresentationHeight,
        int sourceWidth,
        int sourceHeight)
    {
        if (nativePresentationHwnd != IntPtr.Zero)
        {
            return CreateNativeSurfaces(
                renderer,
                nativePresentationHwnd,
                directCompositionLayersRequested,
                requestedNativePresentationWidth,
                requestedNativePresentationHeight,
                sourceWidth,
                sourceHeight);
        }

        var (surfaceWidth, surfaceHeight) = GetD3DImagePresentationSize(
            requestedWpfPresentationWidth,
            requestedWpfPresentationHeight,
            sourceWidth,
            sourceHeight);
        var success = renderer.CreateSurfaces(surfaceWidth, surfaceHeight);
        if (success && configuration.EnableLogging)
        {
            Debug.WriteLine($"[OptimizedRenderer] Active presentation backend: D3DImage, source={sourceWidth}x{sourceHeight}, surface={surfaceWidth}x{surfaceHeight}");
        }

        return success;
    }

    public bool PresentationSurfaceChanged(
        VorticeD3D11Renderer? renderer,
        IntPtr nativePresentationHwnd,
        bool directCompositionLayersRequested,
        int requestedNativePresentationWidth,
        int requestedNativePresentationHeight,
        int requestedWpfPresentationWidth,
        int requestedWpfPresentationHeight,
        int lastPresentationWidth,
        int lastPresentationHeight,
        int sourceWidth,
        int sourceHeight)
    {
        if (nativePresentationHwnd != IntPtr.Zero)
        {
            if (directCompositionLayersRequested && renderer?.IsDirectCompositionActive != true)
            {
                return true;
            }

            if (!directCompositionLayersRequested)
            {
                // Host-size changes are handled by the existing native swap-chain at the next frame
                // boundary. Avoid tearing down/recreating during WPF live resize intermediate sizes.
                return false;
            }
        }

        var (desiredWidth, desiredHeight) = GetPresentationSizeForActiveTarget(
            nativePresentationHwnd,
            directCompositionLayersRequested,
            requestedNativePresentationWidth,
            requestedNativePresentationHeight,
            requestedWpfPresentationWidth,
            requestedWpfPresentationHeight,
            sourceWidth,
            sourceHeight);

        return desiredWidth != lastPresentationWidth || desiredHeight != lastPresentationHeight;
    }

    public (int width, int height) GetNativeOutputSize(
        int requestedNativePresentationWidth,
        int requestedNativePresentationHeight,
        int sourceWidth,
        int sourceHeight)
    {
        if (requestedNativePresentationWidth > 0 && requestedNativePresentationHeight > 0)
        {
            return (requestedNativePresentationWidth, requestedNativePresentationHeight);
        }

        return (Math.Max(1, sourceWidth), Math.Max(1, sourceHeight));
    }

    public (int width, int height) GetPresentationSizeForActiveTarget(
        IntPtr nativePresentationHwnd,
        bool directCompositionLayersRequested,
        int requestedNativePresentationWidth,
        int requestedNativePresentationHeight,
        int requestedWpfPresentationWidth,
        int requestedWpfPresentationHeight,
        int sourceWidth,
        int sourceHeight)
    {
        sourceWidth = Math.Max(1, sourceWidth);
        sourceHeight = Math.Max(1, sourceHeight);

        if (nativePresentationHwnd != IntPtr.Zero)
        {
            if (directCompositionLayersRequested)
            {
                // DirectComposition layer swap-chains stay source-sized; host/control resize is handled
                // by DirectComposition visual transforms.
                return (sourceWidth, sourceHeight);
            }

            // Native HWND swap-chain uses a stable HWND but a host-sized backbuffer. This lets D3D own
            // the viewport/letterbox rectangle instead of letting USER32/DXGI stretch a source-sized
            // swap-chain during WPF live resize.
            return GetNativeOutputSize(
                requestedNativePresentationWidth,
                requestedNativePresentationHeight,
                sourceWidth,
                sourceHeight);
        }

        return GetD3DImagePresentationSize(
            requestedWpfPresentationWidth,
            requestedWpfPresentationHeight,
            sourceWidth,
            sourceHeight);
    }

    public (int width, int height) GetD3DImagePresentationSize(
        int requestedWpfPresentationWidth,
        int requestedWpfPresentationHeight,
        int sourceWidth,
        int sourceHeight)
    {
        sourceWidth = Math.Max(1, sourceWidth);
        sourceHeight = Math.Max(1, sourceHeight);

        if (configuration.WpfD3DImageSurfaceSizingMode == WpfD3DImageSurfaceSizingMode.DisplayRectangle
            && requestedWpfPresentationWidth > 0
            && requestedWpfPresentationHeight > 0)
        {
            return (requestedWpfPresentationWidth, requestedWpfPresentationHeight);
        }

        if (configuration.WpfD3DImageSurfaceSizingMode == WpfD3DImageSurfaceSizingMode.SourceCapped)
        {
            return CapPresentationSize(sourceWidth, sourceHeight);
        }

        return (sourceWidth, sourceHeight);
    }

    private bool CreateNativeSurfaces(
        VorticeD3D11Renderer renderer,
        IntPtr nativePresentationHwnd,
        bool directCompositionLayersRequested,
        int requestedNativePresentationWidth,
        int requestedNativePresentationHeight,
        int sourceWidth,
        int sourceHeight)
    {
        var success = false;
        if (directCompositionLayersRequested)
        {
            // Keep DirectComposition layer swap-chains at the media/source size. Live control resizing
            // is handled by DComp visual transforms, not by recreating swap-chains.
            success = renderer.CreateDirectCompositionHwndSurfaces(nativePresentationHwnd, sourceWidth, sourceHeight);
            if (success)
            {
                var (outputWidth, outputHeight) = GetNativeOutputSize(
                    requestedNativePresentationWidth,
                    requestedNativePresentationHeight,
                    sourceWidth,
                    sourceHeight);
                renderer.SetDirectCompositionOutputSize(outputWidth, outputHeight);
                if (configuration.EnableLogging)
                {
                    Debug.WriteLine($"[OptimizedRenderer] Active presentation backend: DirectCompositionNativeLayers, source={sourceWidth}x{sourceHeight}, surface={sourceWidth}x{sourceHeight}, output={outputWidth}x{outputHeight}");
                }
            }
        }

        if (success)
        {
            return true;
        }

        var (nativeOutputWidth, nativeOutputHeight) = GetNativeOutputSize(
            requestedNativePresentationWidth,
            requestedNativePresentationHeight,
            sourceWidth,
            sourceHeight);
        success = renderer.CreateNativeHwndSurfaces(nativePresentationHwnd, nativeOutputWidth, nativeOutputHeight);
        if (success && configuration.EnableLogging)
        {
            Debug.WriteLine($"[OptimizedRenderer] Active presentation backend: NativeHwndSwapChain, source={sourceWidth}x{sourceHeight}, output={nativeOutputWidth}x{nativeOutputHeight}");
        }

        return success;
    }

    private (int width, int height) CapPresentationSize(int sourceWidth, int sourceHeight)
    {
        var maxWidth = configuration.MaxWpfD3DImageSurfaceWidth;
        var maxHeight = configuration.MaxWpfD3DImageSurfaceHeight;
        if (maxWidth <= 0 || maxHeight <= 0 || (sourceWidth <= maxWidth && sourceHeight <= maxHeight))
        {
            return (sourceWidth, sourceHeight);
        }

        var scale = Math.Min(maxWidth / (double)sourceWidth, maxHeight / (double)sourceHeight);
        scale = Math.Clamp(scale, 0.01, 1.0);
        return (
            Math.Max(1, (int)Math.Round(sourceWidth * scale)),
            Math.Max(1, (int)Math.Round(sourceHeight * scale)));
    }
}
