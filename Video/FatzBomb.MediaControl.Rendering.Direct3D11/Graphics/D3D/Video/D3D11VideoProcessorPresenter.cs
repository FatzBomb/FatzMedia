using System.Diagnostics;
using System.Runtime.InteropServices;
using FatzBomb.MediaControl.Core.Configuration;
using FatzBomb.MediaControl.Core.Graphics.Composition;
using FatzBomb.MediaControl.Core.Graphics.Geometry;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using FatzBomb.MediaControl.Core.Graphics.D3D.Device;
using SharpGen.Runtime;

namespace FatzBomb.MediaControl.Core.Graphics.D3D.Video;

/// <summary>
/// Owns the D3D11 video-processor pipeline used to scale/crop primary or background video
/// into the current presentation target. The coordinator decides when to call it; this class
/// owns the processor enumerator, processor, output view, and rect mapping details.
/// </summary>
internal sealed class D3D11VideoProcessorPresenter(MediaPlayerConfiguration configuration) : IDisposable
{
    private ID3D11VideoProcessorEnumerator? _videoProcessorEnumerator;
    private ID3D11VideoProcessor? _videoProcessor;
    private ID3D11VideoProcessorOutputView? _videoOutputView;
    private int _videoProcessorWidth;
    private int _videoProcessorHeight;
    private int _videoProcessorOutputWidth;
    private int _videoProcessorOutputHeight;
    private double _videoProcessorFps;
    private IntPtr _videoProcessorOutputTexturePointer;
    private (VideoColorSpace ColorSpace, bool Pq)? _appliedColorSpace;
    private readonly D3D11HdrToneMapPresenter _hdrToneMapPresenter = new();
    private bool _hdrPathReported;

    /// <summary>What every presentation and composition texture holds: full-range sRGB-gamma BT.709 RGB.</summary>
    private const ColorSpaceType OutputColorSpace = ColorSpaceType.RgbFullG22NoneP709;

    private readonly VideoProcessorStream[] _streams =
    [
        new VideoProcessorStream
        {
            Enable = true,
            OutputIndex = 0,
            InputFrameOrField = 0,
            InputSurface = null
        }
    ];

    public bool PresentHardwareFrame(
        D3D11DeviceResources deviceResources,
        ID3D11Texture2D presentationTexture,
        IntPtr sourceTexturePointer,
        int sourceArraySlice,
        int width,
        int height,
        double framesPerSecond,
        VideoColorSpace colorSpace,
        HdrToneMapRequest? toneMap,
        int presentationWidth,
        int presentationHeight,
        MediaCompositionStretchMode stretch)
    {
        if (!deviceResources.HasVideoProcessorSupport
            || sourceTexturePointer == IntPtr.Zero
            || width <= 0
            || height <= 0
            || presentationWidth <= 0
            || presentationHeight <= 0)
        {
            return false;
        }

        using ID3D11Texture2D sourceTexture = AddRefAndWrap(sourceTexturePointer, static pointer => new ID3D11Texture2D(pointer));
        return BlitTextureToPresentation(
            deviceResources,
            presentationTexture,
            sourceTexture,
            sourceArraySlice,
            width,
            height,
            framesPerSecond,
            colorSpace,
            toneMap,
            presentationWidth,
            presentationHeight,
            stretch);
    }

    public bool BlitTextureToPresentation(
        D3D11DeviceResources deviceResources,
        ID3D11Texture2D presentationTexture,
        ID3D11Texture2D sourceTexture,
        int sourceArraySlice,
        int width,
        int height,
        double framesPerSecond,
        VideoColorSpace colorSpace,
        HdrToneMapRequest? toneMap,
        int presentationWidth,
        int presentationHeight,
        MediaCompositionStretchMode stretch)
    {
        if (deviceResources.VideoDevice == null
            || deviceResources.VideoContext == null
            || deviceResources.Context == null
            || width <= 0
            || height <= 0
            || presentationWidth <= 0
            || presentationHeight <= 0)
        {
            return false;
        }

        VideoColorSpace resolvedColorSpace = colorSpace.Resolve(width, height);
        if (toneMap is { } request)
        {
            (Vortice.RawRect hdrSource, Vortice.RawRect hdrDestination) = CalculateVideoProcessorRects(width, height, presentationWidth, presentationHeight, stretch);
            if (_hdrToneMapPresenter.TryPresent(
                    deviceResources,
                    sourceTexture,
                    sourceArraySlice,
                    resolvedColorSpace,
                    request,
                    presentationTexture,
                    presentationWidth,
                    presentationHeight,
                    hdrSource,
                    hdrDestination))
            {
                ReportHdrPath(MediaDiagnosticLevel.Info, "HDR10 video is tone mapped to SDR on the GPU (Direct3D11 shader).");
                deviceResources.Context.Flush();
                return true;
            }

            // Hand tone mapping back to the decoder (its CPU mapper); frames already queued
            // go through the video processor, which tone maps only where the driver does.
            var reason = _hdrToneMapPresenter.LastFailure ?? "the GPU path is unavailable";
            request.Fallback?.RequestCpuToneMapping(reason);
            ReportHdrPath(
                MediaDiagnosticLevel.Warning,
                $"HDR10 tone mapping on the GPU is unavailable ({reason}); the engine's CPU tone mapper takes over.");
        }

        if (!EnsureVideoProcessor(deviceResources, presentationTexture, width, height, framesPerSecond, presentationWidth, presentationHeight))
        {
            return false;
        }

        using ID3D11VideoProcessorInputView inputView = deviceResources.VideoDevice.CreateVideoProcessorInputView(
            sourceTexture,
            _videoProcessorEnumerator!,
            new VideoProcessorInputViewDescription
            {
                FourCC = 0,
                ViewDimension = VideoProcessorInputViewDimension.Texture2D,
                Texture2D = new Texture2DVideoProcessorInputView
                {
                    MipSlice = 0,
                    ArraySlice = (uint)Math.Max(0, sourceArraySlice)
                }
            });

        deviceResources.VideoContext.VideoProcessorSetStreamFrameFormat(_videoProcessor!, 0, VideoFrameFormat.Progressive);
        deviceResources.VideoContext.VideoProcessorSetStreamAutoProcessingMode(_videoProcessor!, 0, false);
        ApplyColorSpaces(deviceResources, sourceTexture, presentationTexture, resolvedColorSpace, pq: toneMap != null);
        (Vortice.RawRect Source, Vortice.RawRect Destination) = CalculateVideoProcessorRects(width, height, presentationWidth, presentationHeight, stretch);
        ApplyVideoProcessorRects(deviceResources.VideoContext, streamIndex: 0, Source, Destination);
        ClearPresentationTextureToBlack(deviceResources, presentationTexture, flush: false);

        _streams[0].InputSurface = inputView;

        Result result = deviceResources.VideoContext.VideoProcessorBlt(_videoProcessor!, _videoOutputView!, 0, _streams);
        if (result.Failure)
        {
            Debug.WriteLine($"[VorticeD3D11] Video processor blit failed: {result}");
            return false;
        }

        deviceResources.Context.Flush();
        return true;
    }

    public void Reset()
    {
        _videoOutputView?.Dispose();
        _videoOutputView = null;
        _videoProcessor?.Dispose();
        _videoProcessor = null;
        _videoProcessorEnumerator?.Dispose();
        _videoProcessorEnumerator = null;
        _videoProcessorWidth = 0;
        _videoProcessorHeight = 0;
        _videoProcessorOutputWidth = 0;
        _videoProcessorOutputHeight = 0;
        _videoProcessorOutputTexturePointer = IntPtr.Zero;
        _videoProcessorFps = 0;
        _appliedColorSpace = null;
        _streams[0].InputSurface = null;
        // Swap-chain resizes need every back-buffer reference released; the shaders stay.
        _hdrToneMapPresenter.ReleaseTargetView();
    }

    /// <summary>
    /// <see cref="Reset"/> plus the HDR shader's resources, for a device change: nothing of the
    /// old device stays alive, and a failure on it is forgotten.
    /// </summary>
    public void ResetDeviceResources()
    {
        Reset();
        _hdrToneMapPresenter.Reset();
        _hdrPathReported = false;
    }

    public void Dispose() => ResetDeviceResources();

    private void ReportHdrPath(MediaDiagnosticLevel level, string message)
    {
        if (_hdrPathReported)
            return;

        _hdrPathReported = true;
        DiagnosticRouter.Raise(level, "D3D11.Hdr", message);
    }

    private bool EnsureVideoProcessor(
        D3D11DeviceResources deviceResources,
        ID3D11Texture2D presentationTexture,
        int width,
        int height,
        double framesPerSecond,
        int presentationWidth,
        int presentationHeight)
    {
        if (deviceResources.VideoDevice == null || deviceResources.VideoContext == null)
        {
            return false;
        }

        var normalizedFps = double.IsFinite(framesPerSecond) && framesPerSecond > 0 ? framesPerSecond : 60.0;
        if (_videoProcessor != null
            && _videoOutputView != null
            && _videoProcessorEnumerator != null
            && _videoProcessorWidth == width
            && _videoProcessorHeight == height
            && _videoProcessorOutputWidth == presentationWidth
            && _videoProcessorOutputHeight == presentationHeight
            && _videoProcessorOutputTexturePointer == presentationTexture.NativePointer
            && Math.Abs(_videoProcessorFps - normalizedFps) < 0.01)
        {
            return true;
        }

        Reset();

        Rational frameRate = ToRational(normalizedFps);
        var contentDescription = new VideoProcessorContentDescription
        {
            InputFrameFormat = VideoFrameFormat.Progressive,
            InputFrameRate = frameRate,
            InputWidth = (uint)width,
            InputHeight = (uint)height,
            OutputFrameRate = frameRate,
            OutputWidth = (uint)presentationWidth,
            OutputHeight = (uint)presentationHeight,
            Usage = VideoUsage.OptimalSpeed
        };

        _videoProcessorEnumerator = deviceResources.VideoDevice.CreateVideoProcessorEnumerator(contentDescription);
        _videoProcessor = deviceResources.VideoDevice.CreateVideoProcessor(_videoProcessorEnumerator, 0);
        _videoOutputView = deviceResources.VideoDevice.CreateVideoProcessorOutputView(
            presentationTexture,
            _videoProcessorEnumerator,
            new VideoProcessorOutputViewDescription
            {
                ViewDimension = VideoProcessorOutputViewDimension.Texture2D,
                Texture2D = new Texture2DVideoProcessorOutputView
                {
                    MipSlice = 0
                }
            });

        _videoProcessorWidth = width;
        _videoProcessorHeight = height;
        _videoProcessorOutputWidth = presentationWidth;
        _videoProcessorOutputHeight = presentationHeight;
        _videoProcessorOutputTexturePointer = presentationTexture.NativePointer;
        _videoProcessorFps = normalizedFps;
        if (configuration.EnableLogging)
        {
            Debug.WriteLine($"[VorticeD3D11] Created video processor source={width}x{height}, output={presentationWidth}x{presentationHeight} @ {normalizedFps:F2}fps");
        }
        return true;
    }

    /// <summary>
    /// Tells the processor the frame's YCbCr matrix and range and asks for full-range RGB.
    /// Left unset, both are the driver's choice: some assume full-range input or limited-range
    /// output (a GPU control panel's video "dynamic range" setting can decide it), which shows
    /// video washed out, and all of them use BT.601 for HD video. A conversion the driver does
    /// not offer through the DXGI color spaces (checked first) uses the older description,
    /// which has no BT.2020 and maps it to BT.709.
    /// </summary>
    private void ApplyColorSpaces(
        D3D11DeviceResources deviceResources,
        ID3D11Texture2D sourceTexture,
        ID3D11Texture2D presentationTexture,
        VideoColorSpace colorSpace,
        bool pq)
    {
        if (_videoProcessor == null || _appliedColorSpace == (colorSpace, pq))
        {
            return;
        }

        // HDR10 the shader could not take: declare PQ, so a driver that tone maps can.
        ColorSpaceType inputColorSpace = pq ? ColorSpaceType.YcbcrStudioG2084LeftP2020 : ToDxgiColorSpace(colorSpace);
        if (deviceResources.VideoContext1 is { } videoContext1
            && SupportsConversion(sourceTexture, presentationTexture, inputColorSpace))
        {
            videoContext1.VideoProcessorSetStreamColorSpace1(_videoProcessor, 0, inputColorSpace);
            videoContext1.VideoProcessorSetOutputColorSpace1(_videoProcessor, OutputColorSpace);
        }
        else if (deviceResources.VideoContext is { } videoContext)
        {
            videoContext.VideoProcessorSetStreamColorSpace(_videoProcessor, 0, new VideoProcessorColorSpace
            {
                YCbCr_Matrix = colorSpace.Matrix == VideoColorMatrix.Bt601 ? 0u : 1u,
                Nominal_Range = (uint)(colorSpace.Range == VideoColorRange.Full
                    ? VideoProcessorNominalRange.Range_0_255
                    : VideoProcessorNominalRange.Range_16_235)
            });
            videoContext.VideoProcessorSetOutputColorSpace(_videoProcessor, new VideoProcessorColorSpace { RGB_Range = 0 });
        }

        _appliedColorSpace = (colorSpace, pq);
    }

    private bool SupportsConversion(ID3D11Texture2D sourceTexture, ID3D11Texture2D presentationTexture, ColorSpaceType inputColorSpace)
    {
        using ID3D11VideoProcessorEnumerator1? enumerator1 = _videoProcessorEnumerator?.QueryInterfaceOrNull<ID3D11VideoProcessorEnumerator1>();
        return enumerator1 != null && enumerator1.CheckVideoProcessorFormatConversion(
            sourceTexture.Description.Format,
            inputColorSpace,
            presentationTexture.Description.Format,
            OutputColorSpace);
    }

    internal static ColorSpaceType ToDxgiColorSpace(VideoColorSpace colorSpace) => (colorSpace.Matrix, colorSpace.Range == VideoColorRange.Full) switch
    {
        (VideoColorMatrix.Bt601, false) => ColorSpaceType.YcbcrStudioG22LeftP601,
        (VideoColorMatrix.Bt601, true) => ColorSpaceType.YcbcrFullG22LeftP601,
        (VideoColorMatrix.Bt2020, false) => ColorSpaceType.YcbcrStudioG22LeftP2020,
        (VideoColorMatrix.Bt2020, true) => ColorSpaceType.YcbcrFullG22LeftP2020,
        (_, true) => ColorSpaceType.YcbcrFullG22LeftP709,
        _ => ColorSpaceType.YcbcrStudioG22LeftP709
    };

    private static (Vortice.RawRect Source, Vortice.RawRect Destination) CalculateVideoProcessorRects(
        int sourceWidth,
        int sourceHeight,
        int presentationWidth,
        int presentationHeight,
        MediaCompositionStretchMode stretch)
    {
        ViewportMapping mapping = ViewportGeometry.MapSourceToViewport(
            sourceWidth,
            sourceHeight,
            Math.Max(1, presentationWidth),
            Math.Max(1, presentationHeight),
            stretch);

        return (
            ToRawRect(mapping.Source, Math.Max(1, sourceWidth), Math.Max(1, sourceHeight)),
            ToRawRect(mapping.Destination, Math.Max(1, presentationWidth), Math.Max(1, presentationHeight)));
    }

    private void ApplyVideoProcessorRects(ID3D11VideoContext videoContext, int streamIndex, Vortice.RawRect sourceRect, Vortice.RawRect destinationRect)
    {
        if (_videoProcessor == null)
        {
            return;
        }

        videoContext.VideoProcessorSetStreamSourceRect(_videoProcessor, (uint)streamIndex, true, sourceRect);
        videoContext.VideoProcessorSetStreamDestRect(_videoProcessor, (uint)streamIndex, true, destinationRect);
    }

    private static Vortice.RawRect ToRawRect(RectD rect, int maxWidth, int maxHeight)
    {
        var left = Math.Clamp((int)Math.Round(rect.Left), 0, Math.Max(0, maxWidth - 1));
        var top = Math.Clamp((int)Math.Round(rect.Top), 0, Math.Max(0, maxHeight - 1));
        var right = Math.Clamp((int)Math.Round(rect.Right), left + 1, Math.Max(1, maxWidth));
        var bottom = Math.Clamp((int)Math.Round(rect.Bottom), top + 1, Math.Max(1, maxHeight));
        return new Vortice.RawRect(left, top, right, bottom);
    }

    private static Rational ToRational(double framesPerSecond)
    {
        var numerator = (uint)Math.Clamp(Math.Round(framesPerSecond * 1000.0), 1, uint.MaxValue);
        return new Rational(numerator, 1000);
    }

    private static void ClearPresentationTextureToBlack(D3D11DeviceResources deviceResources, ID3D11Texture2D presentationTexture, bool flush)
    {
        if (deviceResources.Device == null || deviceResources.Context == null)
        {
            return;
        }

        using ID3D11RenderTargetView renderTargetView = deviceResources.Device.CreateRenderTargetView(presentationTexture);
        deviceResources.Context.ClearRenderTargetView(renderTargetView, new Color4(0f, 0f, 0f, 1f));
        if (flush)
        {
            deviceResources.Context.Flush();
        }
    }

    private static T AddRefAndWrap<T>(IntPtr pointer, Func<IntPtr, T> factory)
    {
        _ = Marshal.AddRef(pointer);
        return factory(pointer);
    }
}
