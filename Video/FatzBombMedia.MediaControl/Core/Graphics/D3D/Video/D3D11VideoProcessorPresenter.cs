using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Media;
using FatzBombMedia.MediaControl.Core.Configuration;
using FatzBombMedia.MediaControl.Core.Graphics.Geometry;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using FatzBombMedia.MediaControl.Core.Graphics.D3D.Device;

namespace FatzBombMedia.MediaControl.Core.Graphics.D3D.Video;

/// <summary>
/// Owns the D3D11 video-processor pipeline used to scale/crop primary or background video
/// into the current presentation target. The coordinator decides when to call it; this class
/// owns the processor enumerator, processor, output view, and rect mapping details.
/// </summary>
internal sealed class D3D11VideoProcessorPresenter : IDisposable
{
    private readonly MediaPlayerConfiguration _configuration;
    private ID3D11VideoProcessorEnumerator? _videoProcessorEnumerator;
    private ID3D11VideoProcessor? _videoProcessor;
    private ID3D11VideoProcessorOutputView? _videoOutputView;
    private int _videoProcessorWidth;
    private int _videoProcessorHeight;
    private int _videoProcessorOutputWidth;
    private int _videoProcessorOutputHeight;
    private double _videoProcessorFps;

    public D3D11VideoProcessorPresenter(MediaPlayerConfiguration configuration)
    {
        _configuration = configuration;
    }

    public bool PresentHardwareFrame(
        D3D11DeviceResources deviceResources,
        ID3D11Texture2D presentationTexture,
        IntPtr sourceTexturePointer,
        int sourceArraySlice,
        int width,
        int height,
        double framesPerSecond,
        int presentationWidth,
        int presentationHeight,
        Stretch stretch)
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

        using var sourceTexture = AddRefAndWrap(sourceTexturePointer, static pointer => new ID3D11Texture2D(pointer));
        return BlitTextureToPresentation(
            deviceResources,
            presentationTexture,
            sourceTexture,
            sourceArraySlice,
            width,
            height,
            framesPerSecond,
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
        int presentationWidth,
        int presentationHeight,
        Stretch stretch)
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

        if (!EnsureVideoProcessor(deviceResources, presentationTexture, width, height, framesPerSecond, presentationWidth, presentationHeight))
        {
            return false;
        }

        using var inputView = deviceResources.VideoDevice.CreateVideoProcessorInputView(
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
        var rects = CalculateVideoProcessorRects(width, height, presentationWidth, presentationHeight, stretch);
        ApplyVideoProcessorRects(deviceResources.VideoContext, streamIndex: 0, rects.Source, rects.Destination);
        ClearPresentationTextureToBlack(deviceResources, presentationTexture, flush: false);

        var streams = new[]
        {
            new VideoProcessorStream
            {
                Enable = true,
                OutputIndex = 0,
                InputFrameOrField = 0,
                InputSurface = inputView
            }
        };

        var result = deviceResources.VideoContext.VideoProcessorBlt(_videoProcessor!, _videoOutputView!, 0, streams);
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
        _videoProcessorFps = 0;
    }

    public void Dispose() => Reset();

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
            && Math.Abs(_videoProcessorFps - normalizedFps) < 0.01)
        {
            return true;
        }

        Reset();

        var frameRate = ToRational(normalizedFps);
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
        _videoProcessorFps = normalizedFps;
        if (_configuration.EnableLogging)
        {
            Debug.WriteLine($"[VorticeD3D11] Created video processor source={width}x{height}, output={presentationWidth}x{presentationHeight} @ {normalizedFps:F2}fps");
        }
        return true;
    }

    private static (Vortice.RawRect Source, Vortice.RawRect Destination) CalculateVideoProcessorRects(
        int sourceWidth,
        int sourceHeight,
        int presentationWidth,
        int presentationHeight,
        Stretch stretch)
    {
        var mapping = ViewportGeometry.MapSourceToViewport(
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

        using var renderTargetView = deviceResources.Device.CreateRenderTargetView(presentationTexture);
        deviceResources.Context.ClearRenderTargetView(renderTargetView, new Color4(0f, 0f, 0f, 1f));
        if (flush)
        {
            deviceResources.Context.Flush();
        }
    }

    private static T AddRefAndWrap<T>(IntPtr pointer, Func<IntPtr, T> factory)
    {
        Marshal.AddRef(pointer);
        return factory(pointer);
    }
}
