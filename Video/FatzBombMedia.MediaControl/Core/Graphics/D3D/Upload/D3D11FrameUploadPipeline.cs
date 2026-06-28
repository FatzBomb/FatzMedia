using System.Diagnostics;
using System.Windows.Media;
using Vortice.Direct3D11;
using FatzBombMedia.MediaControl.Core.Graphics.D3D.Device;

namespace FatzBombMedia.MediaControl.Core.Graphics.D3D.Upload;

/// <summary>
/// Shared CPU-frame upload path for normal video/image frames and frames that
/// receive retained overlay layers afterward.  The coordinator decides the media
/// role and any post-base draw work; this class only uploads the base BGRA frame
/// into the current presentation target.
/// </summary>
internal sealed class D3D11FrameUploadPipeline
{
    public delegate bool BlitToPresentationCallback(
        ID3D11Texture2D sourceTexture,
        int sourceArraySlice,
        int sourceWidth,
        int sourceHeight,
        double framesPerSecond,
        Stretch? stretchOverride);

    public bool UploadCpuFrame(
        D3D11DeviceResources deviceResources,
        D3D11TextureUploadCache uploadCache,
        ID3D11Texture2D? presentationTexture,
        int presentationWidth,
        int presentationHeight,
        ReadOnlySpan<byte> pixelData,
        int width,
        int height,
        int stride,
        double framesPerSecond,
        Stretch? stretchOverride,
        BlitToPresentationCallback blitToPresentation)
    {
        ID3D11Device? device = deviceResources.Device;
        ID3D11DeviceContext? context = deviceResources.Context;
        if (device == null
            || context == null
            || presentationTexture == null
            || pixelData.IsEmpty
            || width <= 0
            || height <= 0
            || stride < width * 4
            || presentationWidth <= 0
            || presentationHeight <= 0)
        {
            return false;
        }

        try
        {
            if (width == presentationWidth && height == presentationHeight)
            {
                context.UpdateSubresource(pixelData, presentationTexture, 0, (uint)stride, 0, null);
                context.Flush();
                return true;
            }

            if (!uploadCache.Ensure(device, width, height) || uploadCache.Texture == null)
            {
                return false;
            }

            context.UpdateSubresource(pixelData, uploadCache.Texture, 0, (uint)stride, 0, null);
            return blitToPresentation(uploadCache.Texture, 0, width, height, framesPerSecond, stretchOverride);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[D3D11FrameUploadPipeline] CPU frame upload failed: {ex.Message}");
            return false;
        }
    }
}
