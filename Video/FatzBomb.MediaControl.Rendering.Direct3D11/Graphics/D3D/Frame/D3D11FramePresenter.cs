using System.Diagnostics;
using FatzBomb.MediaControl.Core.Graphics.Composition;
using FatzBomb.MediaControl.Core.Graphics.D3D.Device;
using FatzBomb.MediaControl.Core.Graphics.D3D.Upload;
using FatzBomb.MediaControl.Core.Graphics.D3D.Video;
using Vortice.Direct3D11;

namespace FatzBomb.MediaControl.Core.Graphics.D3D.Frame;

/// <summary>
/// Coordinates one base media frame through the D3D11 upload/video-processor path,
/// diagnostics and caller-provided presentation
/// notification.  This keeps VorticeD3D11Renderer focused on surface/backend state while
/// this class owns the repeatable frame pipeline:
///
///   upload base frame -> notify/present active backend
///
/// Presentation backend ownership still remains outside this class; the renderer supplies
/// the current target texture, target dimensions, and blit callback for the active backend.
/// </summary>
internal sealed class D3D11FramePresenter(
    D3D11FrameUploadResources uploadResources,
    D3D11VideoProcessorPresenter videoProcessorPresenter)
{
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
        MediaCompositionStretchMode presentationStretch,
        Func<int, int, bool> notifyPresented)
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

            var presented = videoProcessorPresenter.PresentHardwareFrame(
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

            if (!notifyPresented(width, height))
                return false;

            RecordUpload(presentStart, width, height, presentationWidth, presentationHeight);
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[D3D11FramePresenter] Hardware frame presentation failed: {ex.Message}");
            return false;
        }
    }

    private void RecordUpload(long startTimestamp, int sourceWidth, int sourceHeight, int presentationWidth, int presentationHeight) => uploadResources.RecordUpload(
            startTimestamp,
            sourceWidth,
            sourceHeight,
            presentationWidth,
            presentationHeight);
}
