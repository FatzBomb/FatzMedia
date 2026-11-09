using System.Diagnostics;
using FatzMedia.Video.Core.Graphics.D3D.Device;
using FatzMedia.Video.Core.Graphics.D3D.Diagnostics;
using FatzMedia.Video.Core.Graphics.D3D.Presentation;
using FatzMedia.Video.Core.Graphics.D3D.Upload;
using FatzMedia.Video.Core.Graphics.D3D.Video;
using FatzMedia.Video.Core.Graphics.Rendering;
using FatzMedia.Video.Core.Video.Hdr;

namespace FatzMedia.Video.Core.Graphics.D3D.Frame;

/// <summary>
/// Presents one hardware-decoded frame on the active primary target:
///
///   active target (applying a pending native resize) -> video processor blit -> present -> record upload
///
/// The renderer decides when frames arrive and how HDR output is chosen; this class owns the
/// per-frame plumbing and names the failed step in <see cref="LastFailureReason"/>.
/// </summary>
sealed class D3D11HardwareFramePresenter(
    D3D11FrameUploadResources uploadResources,
    D3D11VideoProcessorPresenter videoProcessorPresenter,
    ID3D11PresentationSurfaceProvider presentationSurfaces,
    D3D11PresentationState presentationState)
{
    readonly RendererDiagnosticLatch _failure = new();

    /// <summary>Why the last <see cref="PresentHardwareFrame"/> returned false, or null (ENG-039).</summary>
    public string? LastFailureReason { get; private set; }

    public bool PresentHardwareFrame(
        D3D11DeviceResources deviceResources,
        bool isDisposed,
        IntPtr sourceTexturePointer,
        int sourceArraySlice,
        int width,
        int height,
        double framesPerSecond,
        VideoColorSpace colorSpace,
        HdrToneMapRequest? toneMap,
        Func<int, int, bool> notifyPresented,
        HdrOutputDecision? hdrOutput = null)
    {
        if (isDisposed
            || deviceResources.Context == null
            || !deviceResources.HasVideoProcessorSupport
            || sourceTexturePointer == IntPtr.Zero
            || width <= 0
            || height <= 0)
        {
            LastFailureReason = "no D3D11 video processor or frame";
            return false;
        }

        if (!presentationSurfaces.TryGetActiveTargetSnapshot(
                applyPendingNativeResize: true,
                reason: "hardware frame presentation",
                out var target))
        {
            LastFailureReason = "no active D3D11 presentation target";
            return false;
        }

        if (target.Texture == null)
        {
            LastFailureReason = "no D3D11 video processor, target or frame";
            return false;
        }

        LastFailureReason = null;
        string? step = null;
        try
        {
            var presentStart = Stopwatch.GetTimestamp();
            var presented = videoProcessorPresenter.PresentHardwareFrame(
                deviceResources,
                target.Texture,
                sourceTexturePointer,
                sourceArraySlice,
                width,
                height,
                framesPerSecond,
                colorSpace,
                toneMap,
                target.Width,
                target.Height,
                presentationState.NativePresentationStretch,
                // Only an scRGB target takes scRGB (the snapshot is taken after the output was chosen).
                target.OutputMode == HdrOutputMode.ScRgb ? hdrOutput : null);
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

            uploadResources.RecordUpload(presentStart, width, height, target.Width, target.Height);
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
}
