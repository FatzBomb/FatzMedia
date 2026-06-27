using System.Diagnostics;
using System.Windows.Media;
using FatzBombMedia.MediaControl.Core.Graphics.D3D.Device;
using FatzBombMedia.MediaControl.Core.Graphics.D3D.Layers;
using FatzBombMedia.MediaControl.Core.Graphics.D3D.Upload;
using FatzBombMedia.MediaControl.Core.Graphics.D3D.Video;
using Vortice.Direct3D11;

namespace FatzBombMedia.MediaControl.Core.Graphics.D3D.Frame;

/// <summary>
/// Coordinates one base media frame through the D3D11 upload/video-processor path,
/// optional retained layer composition, diagnostics, and caller-provided presentation
/// notification.  This keeps VorticeD3D11Renderer focused on surface/backend state while
/// this class owns the repeatable frame pipeline:
///
///   upload base frame -> composite retained layers -> notify/present active backend
///
/// Presentation backend ownership still remains outside this class; the renderer supplies
/// the current target texture, target dimensions, and blit callback for the active backend.
/// </summary>
internal sealed class D3D11FramePresenter
{
    private readonly D3D11FrameUploadPipeline _uploadPipeline;
    private readonly D3D11FrameUploadResources _uploadResources;
    private readonly D3D11VideoProcessorPresenter _videoProcessorPresenter;
    private readonly D3D11LayerCompositor _layerCompositor;

    public D3D11FramePresenter(
        D3D11FrameUploadPipeline uploadPipeline,
        D3D11FrameUploadResources uploadResources,
        D3D11VideoProcessorPresenter videoProcessorPresenter,
        D3D11LayerCompositor layerCompositor)
    {
        _uploadPipeline = uploadPipeline;
        _uploadResources = uploadResources;
        _videoProcessorPresenter = videoProcessorPresenter;
        _layerCompositor = layerCompositor;
    }

    public bool UploadCpuFrame(
        D3D11DeviceResources deviceResources,
        Func<bool> preparePresentationTarget,
        ID3D11Texture2D? presentationTexture,
        int presentationWidth,
        int presentationHeight,
        ReadOnlySpan<byte> pixelData,
        int width,
        int height,
        int stride,
        double framesPerSecond,
        Stretch? baseStretchOverride,
        D3D11FrameUploadPipeline.BlitToPresentationCallback blitToPresentation,
        Action<int, int> notifyPresented)
    {
        if (!ValidateCpuFrameRequest(deviceResources, presentationTexture, pixelData, width, height, stride))
        {
            return false;
        }

        try
        {
            var uploadStart = Stopwatch.GetTimestamp();
            if (!preparePresentationTarget())
            {
                return false;
            }

            if (!_uploadPipeline.UploadCpuFrame(
                    deviceResources,
                    _uploadResources.TextureCache,
                    presentationTexture!,
                    presentationWidth,
                    presentationHeight,
                    pixelData,
                    width,
                    height,
                    stride,
                    framesPerSecond,
                    baseStretchOverride,
                    blitToPresentation))
            {
                return false;
            }

            RecordUpload(uploadStart, width, height, presentationWidth, presentationHeight);
            notifyPresented(width, height);
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[D3D11FramePresenter] CPU frame presentation failed: {ex.Message}");
            return false;
        }
    }

    public bool UploadCpuFrameWithRetainedLayers(
        D3D11DeviceResources deviceResources,
        Func<bool> preparePresentationTarget,
        ID3D11Texture2D? presentationTexture,
        ID3D11RenderTargetView? presentationRenderTargetView,
        int presentationWidth,
        int presentationHeight,
        ReadOnlySpan<byte> pixelData,
        int width,
        int height,
        int stride,
        double framesPerSecond,
        uint washArgb,
        bool forceBaseUniformToFill,
        bool requireOverlayGraphics,
        D3D11FrameUploadPipeline.BlitToPresentationCallback blitToPresentation,
        Action<int, int> notifyPresented)
    {
        if (!ValidateCpuFrameRequest(deviceResources, presentationTexture, pixelData, width, height, stride))
        {
            return false;
        }

        try
        {
            var uploadStart = Stopwatch.GetTimestamp();
            if (!preparePresentationTarget())
            {
                return false;
            }

            var baseStretchOverride = forceBaseUniformToFill ? Stretch.UniformToFill : (Stretch?)null;
            if (!_uploadPipeline.UploadCpuFrame(
                    deviceResources,
                    _uploadResources.TextureCache,
                    presentationTexture!,
                    presentationWidth,
                    presentationHeight,
                    pixelData,
                    width,
                    height,
                    stride,
                    framesPerSecond,
                    baseStretchOverride,
                    blitToPresentation))
            {
                return false;
            }

            if (!_layerCompositor.CompositeAfterBaseFrame(
                    deviceResources,
                    presentationRenderTargetView,
                    presentationWidth,
                    presentationHeight,
                    washArgb,
                    requireOverlayGraphics))
            {
                // Do not present a video-only frame when overlay graphics were required.
                // The caller can let higher-level fallback paths handle this media frame.
                RecordUpload(uploadStart, width, height, presentationWidth, presentationHeight);
                return false;
            }

            RecordUpload(uploadStart, width, height, presentationWidth, presentationHeight);
            notifyPresented(width, height);
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[D3D11FramePresenter] Retained-layer frame presentation failed: {ex.Message}");
            return false;
        }
    }

    public bool PresentHardwareFrame(
        D3D11DeviceResources deviceResources,
        Func<bool> preparePresentationTarget,
        ID3D11Texture2D? presentationTexture,
        IntPtr sourceTexturePointer,
        int sourceArraySlice,
        int width,
        int height,
        double framesPerSecond,
        int presentationWidth,
        int presentationHeight,
        Stretch presentationStretch,
        Action<int, int> notifyPresented)
    {
        if (deviceResources.Context == null
            || !deviceResources.HasVideoProcessorSupport
            || presentationTexture == null
            || sourceTexturePointer == IntPtr.Zero
            || width <= 0
            || height <= 0)
        {
            return false;
        }

        try
        {
            var presentStart = Stopwatch.GetTimestamp();
            if (!preparePresentationTarget())
            {
                return false;
            }

            var presented = _videoProcessorPresenter.PresentHardwareFrame(
                deviceResources,
                presentationTexture,
                sourceTexturePointer,
                sourceArraySlice,
                width,
                height,
                framesPerSecond,
                presentationWidth,
                presentationHeight,
                presentationStretch);
            if (!presented)
            {
                return false;
            }

            RecordUpload(presentStart, width, height, presentationWidth, presentationHeight);
            notifyPresented(width, height);
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[D3D11FramePresenter] Hardware frame presentation failed: {ex.Message}");
            return false;
        }
    }

    private static bool ValidateCpuFrameRequest(
        D3D11DeviceResources deviceResources,
        ID3D11Texture2D? presentationTexture,
        ReadOnlySpan<byte> pixelData,
        int width,
        int height,
        int stride)
    {
        return deviceResources.Context != null
            && presentationTexture != null
            && !pixelData.IsEmpty
            && width > 0
            && height > 0
            && stride >= width * 4;
    }

    private void RecordUpload(long startTimestamp, int sourceWidth, int sourceHeight, int presentationWidth, int presentationHeight)
    {
        _uploadResources.RecordUpload(
            startTimestamp,
            sourceWidth,
            sourceHeight,
            presentationWidth,
            presentationHeight);
    }
}
