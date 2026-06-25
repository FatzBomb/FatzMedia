using FatzBombMedia.MediaControl.Core.Configuration;
using FatzBombMedia.MediaControl.Core.Abstractions;
using FatzBombMedia.MediaControl.Core.Graphics;
using FatzBombMedia.MediaControl.Core.Video;
using FatzBombMedia.MediaControl.Pipeline;
using System.Diagnostics;
using System.IO;
using System.Windows.Media;

namespace FatzBombMedia.MediaControl.Controllers;

internal sealed class BackgroundVideoRenderer : IDisposable
{
    private const int TargetBufferedFrames = 16;
    private const int MaxPacketsPerDecodePass = 6;

    private readonly OptimizedGraphicsRenderer _renderer;
    private readonly MediaPlayerConfiguration _configuration;
    private readonly Lock _decodeLock = new();
    private readonly PlaybackScheduler _decodeScheduler = new("BackgroundVideoDecode");
    private readonly PlaybackScheduler _presentationScheduler = new("BackgroundVideoPresentation");
    private VideoDecoder? _decoder;
    private string? _tempPath;
    private readonly Stopwatch _playClock = new();
    private double _lastPresentedPosition = double.NaN;
    private bool _isActive;
    private bool _disposed;
    private int _renderDiagnosticTicks;
    private int _renderDiagnosticLockTimeouts;
    private int _renderDiagnosticNoFrameTicks;
    private int _renderDiagnosticPresentedFrames;
    private const int RenderDiagnosticInterval = 120;

    public BackgroundVideoRenderer(MediaPlayerConfiguration configuration)
    {
        _configuration = configuration;
        _renderer = new OptimizedGraphicsRenderer(configuration);
        _renderer.ImageSourceChanged += (_, __) => ImageSourceChanged?.Invoke(this, EventArgs.Empty);
        _renderer.FramePresented += (_, __) => FramePresented?.Invoke(this, EventArgs.Empty);
    }

    public ImageSource? ImageSource => _renderer.ImageSource;
    public bool IsOpen => _decoder != null;
    public double Duration => _decoder?.DurationSeconds ?? 0;

    /// <summary>
    /// When false, the background decoder raises FrameReady for the native compositor but does not
    /// also present through its private WPF/D3DImage renderer.
    /// </summary>
    public bool UseOwnPresentation { get; set; } = true;

    public event EventHandler? ImageSourceChanged;
    public event EventHandler? FramePresented;

    /// <summary>
    /// Raised when a looping background video frame is ready. MediaRenderer uses this to feed the
    /// native visual compositor so CDG + background video can present through the main HWND surface
    /// without using WPF/D3DImage as the visible layer.
    /// </summary>
    public event EventHandler<VideoFrameEventArgs>? FrameReady;

    public bool Open(MediaBackgroundCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (candidate.Kind != MediaBackgroundKind.Video)
        {
            return false;
        }

        Clear();

        var path = candidate.Path;
        if (string.IsNullOrWhiteSpace(path))
        {
            if (candidate.Data == null || candidate.Data.Length == 0)
            {
                return false;
            }

            path = CreateTempMediaPath(candidate.Data, candidate.DisplayName);
            _tempPath = path;
        }

        // Background video is a visual-only looping layer. Decode to BGRA for the native
        // compositor so CDG can be alpha-blended over it without routing through WPF/D3DImage.
        // Primary video still uses the normal hardware-texture path.
        var decoder = new VideoDecoder(
            VideoDecodingMethod.Software,
            enableHardwareFrameOutput: false,
            _configuration.EnableLogging,
            _configuration.EnablePerformanceLogging,
            _configuration.EnableVerboseLogging);
        if (_configuration.EnableLogging)
        {
            Debug.WriteLine($"[BackgroundVideoRenderer] Opening visual-only looping background video as BGRA compositor source: {Path.GetFileName(path)}");
        }
        if (!decoder.Open(path))
        {
            decoder.Dispose();
            Clear();
            return false;
        }

        if (!DecodeUntilFrameAvailable(decoder))
        {
            var fallbackDecoder = TryFallbackToSoftware(decoder, path);
            if (fallbackDecoder == null || !DecodeUntilFrameAvailable(fallbackDecoder))
            {
                fallbackDecoder?.Dispose();
                Clear();
                return false;
            }

            decoder = fallbackDecoder;
        }

        _decoder = decoder;
        ImageSourceChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void Start()
    {
        if (_disposed || _decoder == null || _isActive)
        {
            return;
        }

        _isActive = true;
        _playClock.Restart();
        Seek(0);
        StartDecodeLoop();
        StartPresentationLoop();
    }

    public void Stop(bool clearFrame)
    {
        if (!_isActive && !clearFrame)
        {
            return;
        }

        _isActive = false;
        _playClock.Stop();
        StopPresentationLoop();
        StopDecodeLoop();

        if (clearFrame)
        {
            _renderer.ClearAllContent();
            ImageSourceChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Render()
    {
        if (_disposed || _decoder == null || !_isActive)
        {
            return;
        }

        var target = NormalizePosition(_playClock.Elapsed.TotalSeconds);
        if (ShouldSeek(target))
        {
            Seek(target);
        }

        _renderDiagnosticTicks++;

        // Only select/copy the decoded frame while holding the decoder lock.  Do not notify
        // compositor/render subscribers under this lock: the native compositor may blend/upload
        // a full BGRA frame, and doing that while the background decoder is locked produces the
        // same visible hitching we were trying to avoid by moving to native HWND presentation.
        VideoFrameEventArgs? readyFrame = null;
        if (!_decodeLock.TryEnter(TimeSpan.FromMilliseconds(4)))
        {
            _renderDiagnosticLockTimeouts++;
            LogRenderDiagnosticsIfDue();
            return;
        }

        try
        {
            readyFrame = TakeFrameForPosition(target);
        }
        finally
        {
            _decodeLock.Exit();
        }

        if (readyFrame != null)
        {
            PresentReadyFrame(readyFrame);
        }

        LogRenderDiagnosticsIfDue();
    }

    private void LogRenderDiagnosticsIfDue()
    {
        if (_renderDiagnosticTicks < RenderDiagnosticInterval)
        {
            return;
        }

        if (_configuration.EnablePerformanceLogging)
        {
            Debug.WriteLine(
                $"[BackgroundVideoRenderer:Perf] ticks={_renderDiagnosticTicks}, presented={_renderDiagnosticPresentedFrames}, " +
                $"noFrame={_renderDiagnosticNoFrameTicks}, lockTimeouts={_renderDiagnosticLockTimeouts}");
        }
        _renderDiagnosticTicks = 0;
        _renderDiagnosticPresentedFrames = 0;
        _renderDiagnosticNoFrameTicks = 0;
        _renderDiagnosticLockTimeouts = 0;
    }

    public void Clear()
    {
        Stop(clearFrame: true);
        lock (_decodeLock)
        {
            _decoder?.Dispose();
            _decoder = null;
            _lastPresentedPosition = double.NaN;
        }

        _renderer.ClearAllContent();
        CleanupTempPath();
        ImageSourceChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        Clear();
        _decodeScheduler.Dispose();
        _presentationScheduler.Dispose();
        _renderer.Dispose();
    }

    private VideoDecoder? TryFallbackToSoftware(VideoDecoder decoder, string path)
    {
        if (string.IsNullOrWhiteSpace(decoder.HardwareAccelerationMethod))
            return decoder;

        if (_configuration.EnableLogging) Debug.WriteLine($"[BackgroundVideoRenderer] Hardware decode produced no usable startup frame; falling back to software for {Path.GetFileName(path)}.");
        decoder.Dispose();

        var softwareDecoder = new VideoDecoder(VideoDecodingMethod.Software, false, _configuration.EnableLogging, _configuration.EnablePerformanceLogging, _configuration.EnableVerboseLogging);
        if (!softwareDecoder.Open(path))
        {
            softwareDecoder.Dispose();
            return null;
        }

        return softwareDecoder;
    }

    private void StartDecodeLoop()
    {
        StopDecodeLoop();
        _decodeScheduler.Start(DecodeLoop);
    }

    private void StopDecodeLoop()
    {
        _decodeScheduler.Stop();
    }

    private void StartPresentationLoop()
    {
        StopPresentationLoop();
        _presentationScheduler.Start(PresentationLoop);
    }

    private void StopPresentationLoop()
    {
        _presentationScheduler.Stop();
    }

    private void PresentationLoop(CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var nextTick = stopwatch.Elapsed.TotalSeconds;

        while (!cancellationToken.IsCancellationRequested)
        {
            Render();

            nextTick += GetPresentationIntervalSeconds();
            var waitSeconds = nextTick - stopwatch.Elapsed.TotalSeconds;
            if (waitSeconds <= 0)
            {
                nextTick = stopwatch.Elapsed.TotalSeconds;
                continue;
            }

            cancellationToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(waitSeconds));
        }
    }

    private void DecodeLoop(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var decoded = DecodeVideoBatch();
            cancellationToken.WaitHandle.WaitOne(decoded ? 2 : 20);
        }
    }

    private bool DecodeVideoBatch()
    {
        lock (_decodeLock)
        {
            if (_decoder == null || _decoder.FrameCount >= TargetBufferedFrames)
            {
                return false;
            }

            var decoded = false;
            var packets = 0;
            while (_decoder.FrameCount < TargetBufferedFrames && packets++ < MaxPacketsPerDecodePass)
            {
                decoded |= _decoder.DecodeNextFrame();
            }

            return decoded;
        }
    }

    private static bool DecodeUntilFrameAvailable(VideoDecoder decoder)
    {
        var attempts = 0;
        while (decoder.FrameCount == 0 && attempts++ < 240)
        {
            decoder.DecodeNextFrame();
        }

        return decoder.FrameCount > 0;
    }

    private VideoFrameEventArgs? TakeFrameForPosition(double position)
    {
        if (_decoder == null)
        {
            return null;
        }

        VideoFrameData? frameToPresent = null;
        var maxPresentationTime = position + GetFrameLeadTolerance();
        while (_decoder.PeekNextFrame() is { } nextFrame)
        {
            if (nextFrame.PresentationTime > maxPresentationTime)
            {
                break;
            }

            var candidateFrame = _decoder.GetNextFrame();
            if (candidateFrame == null)
            {
                break;
            }

            if (frameToPresent != null)
            {
                _decoder.ReturnFrame(frameToPresent);
            }

            frameToPresent = candidateFrame;
        }

        if (frameToPresent == null)
        {
            _renderDiagnosticNoFrameTicks++;
            return null;
        }

        _lastPresentedPosition = frameToPresent.PresentationTime;
        _renderDiagnosticPresentedFrames++;

        try
        {
            if (frameToPresent.IsD3D11HardwareFrame)
            {
                // Background video currently opens as software/BGRA for native CDG composition,
                // but keep this path safe for future hardware background texture work.
                return new VideoFrameEventArgs
                {
                    PixelData = ReadOnlyMemory<byte>.Empty,
                    Width = frameToPresent.Width,
                    Height = frameToPresent.Height,
                    Stride = frameToPresent.Stride,
                    PtsSeconds = frameToPresent.PresentationTime,
                    FramesPerSecond = _decoder.FramesPerSecond,
                    D3D11DevicePointer = frameToPresent.D3D11DevicePointer,
                    D3D11DeviceContextPointer = frameToPresent.D3D11DeviceContextPointer,
                    D3D11TexturePointer = frameToPresent.D3D11TexturePointer,
                    D3D11ArraySlice = frameToPresent.D3D11ArraySlice
                };
            }

            if (frameToPresent.Data == null || frameToPresent.Length <= 0)
            {
                _renderDiagnosticNoFrameTicks++;
                return null;
            }

            // Copy the frame while the decoder lock is held, then release the decoder frame before
            // notifying compositor subscribers.  This avoids holding the decoder queue hostage while
            // the native compositor blends/uploads a full frame.
            var copy = new byte[frameToPresent.Length];
            Buffer.BlockCopy(frameToPresent.Data, 0, copy, 0, frameToPresent.Length);
            return new VideoFrameEventArgs
            {
                PixelData = copy,
                Width = frameToPresent.Width,
                Height = frameToPresent.Height,
                Stride = frameToPresent.Stride,
                PtsSeconds = frameToPresent.PresentationTime,
                FramesPerSecond = _decoder.FramesPerSecond
            };
        }
        finally
        {
            _decoder?.ReturnFrame(frameToPresent);
        }
    }

    private void PresentReadyFrame(VideoFrameEventArgs frameArgs)
    {
        FrameReady?.Invoke(this, frameArgs);
        if (!UseOwnPresentation)
        {
            return;
        }

        if (frameArgs.IsD3D11HardwareFrame)
        {
            _renderer.RenderVideoFrame(frameArgs);
        }
        else if (!frameArgs.PixelData.IsEmpty)
        {
            _renderer.RenderVideoFrame(frameArgs.PixelData.Span, frameArgs.Width, frameArgs.Height, frameArgs.Stride);
        }
    }

    private bool ShouldSeek(double target)
    {
        if (!double.IsFinite(_lastPresentedPosition))
        {
            return false;
        }

        return target + 0.05 < _lastPresentedPosition;
    }

    private void Seek(double target)
    {
        lock (_decodeLock)
        {
            _decoder?.Seek(target);
            _lastPresentedPosition = double.NaN;
        }
    }

    private double NormalizePosition(double mediaPositionSeconds)
    {
        var target = Math.Max(0, mediaPositionSeconds);
        var duration = Duration;
        if (duration > 0.2)
        {
            target %= duration;
        }

        return target;
    }

    private double GetFrameLeadTolerance()
    {
        return VideoPresentationTiming.GetBackgroundFrameLeadTolerance(_decoder?.FramesPerSecond ?? 0);
    }

    private double GetPresentationIntervalSeconds()
    {
        return VideoPresentationTiming.GetBackgroundPresentationInterval(_decoder?.FramesPerSecond ?? 0);
    }

    private void CleanupTempPath()
    {
        if (string.IsNullOrWhiteSpace(_tempPath))
        {
            return;
        }

        try
        {
            if (File.Exists(_tempPath))
            {
                File.Delete(_tempPath);
            }
        }
        catch
        {
        }
        finally
        {
            _tempPath = null;
        }
    }

    private static string CreateTempMediaPath(byte[] data, string displayName)
    {
        var extension = Path.GetExtension(displayName);
        if (string.IsNullOrWhiteSpace(extension))
        {
            extension = ".media";
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "FatzBombMediaControl", "Backgrounds");
        Directory.CreateDirectory(tempDir);
        var tempPath = Path.Combine(tempDir, Guid.NewGuid().ToString("N") + extension);
        File.WriteAllBytes(tempPath, data);
        return tempPath;
    }
}
