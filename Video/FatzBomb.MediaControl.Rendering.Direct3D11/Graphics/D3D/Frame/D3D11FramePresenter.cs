using System.Diagnostics;
using FatzBomb.MediaControl.Core.Graphics.Composition;
using FatzBomb.MediaControl.Core.Graphics.D3D.Device;
using FatzBomb.MediaControl.Core.Graphics.D3D.Diagnostics;
using FatzBomb.MediaControl.Core.Graphics.D3D.Upload;
using FatzBomb.MediaControl.Core.Graphics.D3D.Video;
using FatzBomb.MediaControl.Core.Graphics.Rendering;
using FatzBomb.MediaControl.Core.Video.Hdr;
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
    private readonly RendererDiagnosticLatch _failure = new();

    /// <summary>Why the last <see cref="PresentHardwareFrame"/> returned false, or null.</summary>
    public string? LastFailureReason { get; private set; }

    public bool PresentHardwareFrame(
        D3D11DeviceResources deviceResources,
        Func<bool> preparePresentationTarget,
        ID3D11Texture2D? presentationTexture,
        IntPtr sourceTexturePointer,
        int sourceArraySlice,
        int width,
        int height,
        double framesPerSecond,
        VideoColorSpace colorSpace,
        HdrToneMapRequest? toneMap,
        int presentationWidth,
        int presentationHeight,
        MediaCompositionStretchMode presentationStretch,
        Func<int, int, bool> notifyPresented,
        HdrOutputDecision? hdrOutput = null)
    {
        if (deviceResources.Context == null
            || !deviceResources.HasVideoProcessorSupport
            || presentationTexture == null
            || sourceTexturePointer == IntPtr.Zero
            || width <= 0
            || height <= 0)
        {
            LastFailureReason = "no D3D11 video processor, target or frame";
            return false;
        }

        LastFailureReason = null;
        string? step = null;
        try
        {
            var presentStart = Stopwatch.GetTimestamp();
            if (!preparePresentationTarget())
            {
                LastFailureReason = "no presentation target";
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
                colorSpace,
                toneMap,
                presentationWidth,
                presentationHeight,
                presentationStretch,
                hdrOutput);
            if (!presented)
            {
                LastFailureReason = $"{videoProcessorPresenter.LastStep} did not draw the frame";
                return false;
            }

            step = "present";
            if (!notifyPresented(width, height))
            {
                LastFailureReason = "swap chain present was not completed";
                return false;
            }

            RecordUpload(presentStart, width, height, presentationWidth, presentationHeight);
            _failure.Reset();
            return true;
        }
        catch (Exception ex)
        {
            // Per frame while it lasts: one diagnostic per episode (ENG-026). The step and, after a
            // GPU reset, the removal reason are named (ENG-039); Core's device probe recovers.
            LastFailureReason = D3D11DeviceLostClassifier.DescribeStepFailure(step ?? videoProcessorPresenter.LastStep, ex, deviceResources.Device);
            _ = _failure.Raise(MediaDiagnosticLevel.Warning, "D3D11", $"Hardware frame presentation failed: {LastFailureReason}", ex);
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
