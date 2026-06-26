using FatzBombMedia.MediaControl.Core.Configuration;
using FatzBombMedia.MediaControl.Core.Abstractions;
using FatzBombMedia.MediaControl.Core.Graphics.D3D;
using System.Buffers;
using System.Diagnostics;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace FatzBombMedia.MediaControl.Core.Graphics;

/// <summary>
/// High-performance graphics renderer that automatically selects between GPU and CPU rendering
/// based on system capabilities and content characteristics.
/// Uses modern Vortice.Windows for reliable DirectX 11 integration.
/// </summary>
/// <remarks>
/// Creates a new OptimizedGraphicsRenderer with the specified configuration.
/// The rendering method is determined once at creation and remains consistent.
/// </remarks>
/// <param name="configuration">Immutable configuration determining rendering behavior</param>
public sealed class OptimizedGraphicsRenderer(MediaPlayerConfiguration configuration) : IDisposable
{
    private readonly GpuRenderingMethod _currentMethod = ResolveRenderingMethod(configuration.Rendering);
    private WriteableBitmap? _bitmap;
    private VorticeD3D11Renderer? _d3d11Renderer;
    private int _lastWidth, _lastHeight;
    private int _lastPresentationWidth, _lastPresentationHeight;
    private int _requestedWpfPresentationWidth, _requestedWpfPresentationHeight;
    private int _requestedNativePresentationWidth, _requestedNativePresentationHeight;
    private int _pendingNativePresentationWidth, _pendingNativePresentationHeight;
    private int _nativePresentationSizeUpdateQueued;
    private bool _disposed;
    private readonly Lock _lock = new();
    private readonly Lock _nativeCompositeRenderLock = new();
    private readonly MediaPlayerConfiguration _configuration = configuration;
    private readonly Dispatcher _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
    private int _resourceGeneration;
    private IntPtr _nativePresentationHwnd;
    private Stretch _nativePresentationStretch = Stretch.Uniform;
    private int _nativeResizeInProgress;

    // Native visual compositor state.  The compositor keeps static/base visuals and the latest
    // CDG frame out of WPF/D3DImage/WriteableBitmap when a native HWND target is active.
    // The current implementation composites CPU BGRA buffers, then uploads the final frame to the
    // existing D3D11 native swap-chain.  That deliberately removes WPF/DX9 bitmap presentation from
    // the normal native path while keeping the existing CDG decoder/rasterizer intact.
    private byte[]? _nativeCompositeBaseBgra;
    private int _nativeCompositeBaseWidth;
    private int _nativeCompositeBaseHeight;
    private int _nativeCompositeBaseStride;
    private bool _nativeCompositeBaseUseCdgLayering;
    private int[]? _nativeCompositeCdgArgb;
    private int _nativeCompositeCdgWidth;
    private int _nativeCompositeCdgHeight;
    private byte[]? _nativeCompositeScaledCdgBgra;
    private int _nativeCompositeScaledCdgWidth;
    private int _nativeCompositeScaledCdgHeight;
    private int _nativeCompositeScaledCdgStride;
    private bool _nativeCompositeScaledCdgDirty = true;
    private byte[]? _nativeCompositeOutputBgra;
    private byte[]? _directCompositionCdgOverlayBgra;
    private int _directCompositionCdgOverlayWidth;
    private int _directCompositionCdgOverlayHeight;
    private int _directCompositionCdgOverlayStride;
    private bool _nativeCachedCdgOverlayDirty = true;
    private bool _nativeDirectCompositionLayersRequested;
    private uint _cdgBackgroundTintArgb;

    // CDG packet updates can arrive much faster than the display cadence.  When the base layer is
    // a looping background video, the compositor is driven by the background video's frame cadence;
    // CDG updates only refresh the retained CDG texture/buffer and are picked up on the next video
    // present.  This avoids the obvious cadence mismatch caused by forcing a 30Hz composite cap.
    private const double NativeCompositeStaticMaxPresentFps = 30.0;
    private long _nativeCompositeLastPresentTimestamp;
    private int _nativeCompositePresentQueued;
    private int _nativeCompositeDirty;
    private bool _nativeCompositeBaseIsVideo;

    // Track content type for proper rendering priority
    private bool _hasActiveVideo = false;

    /// <summary>
    /// Gets the current ImageSource for WPF binding.
    /// </summary>
    public ImageSource? ImageSource
    {
        get
        {
            var source = _currentMethod switch
            {
                GpuRenderingMethod.D3D11 => _d3d11Renderer?.ImageSource ?? _bitmap,
                _ => _bitmap
            };

            return source;
        }
    }

    /// <summary>
    /// Event raised when the ImageSource changes.
    /// </summary>
    public event EventHandler<ImageSource?>? ImageSourceChanged;

    /// <summary>
    /// Raised immediately after a video frame has been presented to the active surface.
    /// Used to nudge WPF's CompositionTarget.Rendering scheduler, which otherwise throttles
    /// down when it doesn't detect tracked visual-tree changes from D3DImage pixel updates alone.
    /// </summary>
    public event EventHandler? FramePresented;

    /// <summary>
    /// Gets the current rendering method being used.
    /// </summary>
    public GpuRenderingMethod CurrentRenderingMethod => _currentMethod;

    public string PresentationBackend => _d3d11Renderer?.PresentationBackend
        ?? (_bitmap != null ? "WriteableBitmap" : "None");

    public void SetNativePresentationWindow(IntPtr hwnd)
    {
        if (_disposed) return;

        RunOnDispatcher(() =>
        {
            lock (_lock)
            {
                if (_nativePresentationHwnd == hwnd)
                {
                    return;
                }

                _nativePresentationHwnd = hwnd;
                _nativeDirectCompositionLayersRequested = false;

                // Force the next frame to rebuild presentation resources. This is intentionally
                // not a media clear: the decoder/audio clock remain active; only the output target
                // changes from D3DImage to native HWND swap chain or back.
                CleanupCurrentMethod();
                _lastWidth = 0;
                _lastHeight = 0;
                _lastPresentationWidth = 0;
                _lastPresentationHeight = 0;
                ImageSourceChanged?.Invoke(this, ImageSource);

                if (_configuration.EnableLogging)
                {
                    var backend = hwnd == IntPtr.Zero ? "D3DImage" : $"NativeHwnd requested hwnd=0x{hwnd.ToInt64():X}";
                    Debug.WriteLine($"[OptimizedRenderer] Presentation target changed: {backend}");
                }
            }
        }, DispatcherPriority.Send);
    }

    public void SetNativeResizeInProgress(bool isResizing)
    {
        if (_disposed) return;

        var wasResizing = Volatile.Read(ref _nativeResizeInProgress) != 0;
        Volatile.Write(ref _nativeResizeInProgress, isResizing ? 1 : 0);
        _d3d11Renderer?.SetNativeLiveResizeInProgress(isResizing);

        if (_configuration.EnableVerboseLogging)
        {
            Debug.WriteLine($"[OptimizedRenderer] Native resize {(isResizing ? "started" : "ended")}; native size/static CDG {(isResizing ? "frozen" : "resumed")}, video frames remain live");
        }

        // Static CDG/image composition is intentionally suppressed during live resize.
        // When resize ends, present exactly one latest composite frame instead of letting
        // a backlog of old timer/thread-pool presents catch up after the mouse is released.
        if (wasResizing && !isResizing && Volatile.Read(ref _nativeCompositeDirty) != 0)
        {
            QueueNativeCompositePresent(TimeSpan.Zero);
        }

        // CDG texture uploads were frozen for the entire resize drag.  Refresh the GPU
        // texture now so the very next background-video frame draws CDG on top of video
        // instead of presenting a video-only frame while waiting for the next CDG packet.
        if (wasResizing && !isResizing && HasRetainedNativeCdgFrameUnsafe())
        {
            TryUploadNativeGpuCdgOverlay();
        }
    }

    public void SetNativePresentationStretch(Stretch stretch)
    {
        if (_disposed) return;
        _nativePresentationStretch = stretch;
        _d3d11Renderer?.SetNativePresentationStretch(stretch);
    }

    private bool ShouldFreezeNativePresentationDuringResize()
    {
        // During a user drag resize, do not feed every intermediate WPF/HWND size into DXGI
        // or the static CDG compositor. Continuous video frames are still allowed. Let Windows move/scale the last completed static native frame
        // while the user is dragging, then apply one final size/present when resize settles.
        return Volatile.Read(ref _nativeResizeInProgress) != 0;
    }

    /// <summary>
    /// Supplies the current native child-HWND presentation size in physical pixels.
    /// DirectComposition uses this as its output surface size so layered video/CDG output
    /// follows the host control just like the regular native HWND swap-chain path.
    /// </summary>
    public void SetNativePresentationSize(int pixelWidth, int pixelHeight)
    {
        if (_disposed) return;

        if (ShouldFreezeNativePresentationDuringResize())
        {
            _pendingNativePresentationWidth = Math.Max(0, pixelWidth);
            _pendingNativePresentationHeight = Math.Max(0, pixelHeight);
            if (!_nativeDirectCompositionLayersRequested)
            {
                return;
            }
        }

        pixelWidth = Math.Max(0, pixelWidth);
        pixelHeight = Math.Max(0, pixelHeight);

        // Live WPF resize can produce dozens of child-HWND bounds changes in one dispatcher
        // frame. Do not enqueue/apply every intermediate size: that makes DirectComposition
        // transforms and commits play catch-up for seconds after the drag ends. Keep only the
        // latest requested size and apply it once at Render priority.
        _pendingNativePresentationWidth = pixelWidth;
        _pendingNativePresentationHeight = pixelHeight;

        if (Interlocked.Exchange(ref _nativePresentationSizeUpdateQueued, 1) != 0)
        {
            return;
        }

        RunOnDispatcher(ApplyPendingNativePresentationSize, DispatcherPriority.Render);
    }

    private void ApplyPendingNativePresentationSize()
    {
        try
        {
            if (_disposed) return;

            if (ShouldFreezeNativePresentationDuringResize())
            {
                if (!_nativeDirectCompositionLayersRequested)
                {
                    return;
                }
            }

            var pixelWidth = Math.Max(0, _pendingNativePresentationWidth);
            var pixelHeight = Math.Max(0, _pendingNativePresentationHeight);

            lock (_lock)
            {
                if (_requestedNativePresentationWidth == pixelWidth
                    && _requestedNativePresentationHeight == pixelHeight)
                {
                    return;
                }

                _requestedNativePresentationWidth = pixelWidth;
                _requestedNativePresentationHeight = pixelHeight;

                if (_nativeDirectCompositionLayersRequested)
                {
                    // DirectComposition layered mode keeps media-layer swap-chains source-sized and
                    // scales the root visual to the current native child-HWND rectangle.
                    _d3d11Renderer?.SetDirectCompositionOutputSize(pixelWidth, pixelHeight);

                    if (_configuration.EnableVerboseLogging)
                    {
                        Debug.WriteLine($"[OptimizedRenderer] Native DComp output size changed by transform: {pixelWidth}x{pixelHeight}");
                    }
                }
                else if (_nativePresentationHwnd != IntPtr.Zero)
                {
                    // VLC-style native HWND path: record the latest host/client size, but do not
                    // call DXGI ResizeBuffers from the WPF/layout notification. The D3D renderer
                    // applies the latest size at the next real content blit so we never present a
                    // black-only resize frame for each intermediate drag size.
                    _d3d11Renderer?.QueueNativeHwndPresentationResize(pixelWidth, pixelHeight);
                    _lastPresentationWidth = Math.Max(1, pixelWidth);
                    _lastPresentationHeight = Math.Max(1, pixelHeight);

                    if (_configuration.EnableVerboseLogging)
                    {
                        Debug.WriteLine($"[OptimizedRenderer] Native HWND output size queued: {pixelWidth}x{pixelHeight}");
                    }
                }
            }
        }
        finally
        {
            Interlocked.Exchange(ref _nativePresentationSizeUpdateQueued, 0);

            // If a newer size arrived while we were applying the previous one, queue exactly one
            // more render-priority update for the final/latest value.
            if (!_disposed
                && !ShouldFreezeNativePresentationDuringResize()
                && (_requestedNativePresentationWidth != _pendingNativePresentationWidth
                    || _requestedNativePresentationHeight != _pendingNativePresentationHeight)
                && Interlocked.Exchange(ref _nativePresentationSizeUpdateQueued, 1) == 0)
            {
                RunOnDispatcher(ApplyPendingNativePresentationSize, DispatcherPriority.Render);
            }
        }
    }

    public void RefreshNativePresentation()
    {
        if (_disposed || _nativePresentationHwnd == IntPtr.Zero || ShouldFreezeNativePresentationDuringResize())
        {
            return;
        }

        // CDG-only and CDG+image backgrounds do not have a video cadence to naturally
        // repaint after resize. Re-present the retained static composite explicitly.
        if (HasNativeStaticCompositeContent())
        {
            RequestNativeCompositePresent(forceImmediate: true);
            return;
        }

        _d3d11Renderer?.RefreshNativePresentation();
    }

    private bool HasNativeStaticCompositeContent()
    {
        lock (_lock)
        {
            return !_nativeCompositeBaseIsVideo
                && (_nativeCompositeBaseBgra != null || _nativeCompositeCdgArgb != null);
        }
    }

    public bool IsNativePresentationRequested => _nativePresentationHwnd != IntPtr.Zero;

    /// <summary>
    /// Tells the native compositor whether the current session has a CDG layer.
    /// This is session state, not frame state: background media must keep using
    /// CDG layer rules even between CDG packet invalidations or before the first
    /// CDG overlay texture has been uploaded.
    /// </summary>
    public void SetNativeCompositeCdgLayeringActive(bool active)
    {
        var changed = false;
        lock (_lock)
        {
            if (_nativeCompositeBaseUseCdgLayering != active)
            {
                _nativeCompositeBaseUseCdgLayering = active;
                changed = true;
            }
        }

        if (changed && IsNativePresentationRequested)
        {
            RequestNativeCompositePresent(forceImmediate: true);
        }
    }

    /// <summary>
    /// Sets the base visual for native composition from a WPF BitmapSource. This is used for
    /// still images and CDG background images while native HWND presentation is active. The pixels
    /// are copied once to BGRA and then uploaded/composited through D3D11, not shown through WPF.
    /// </summary>
    public void SetNativeCompositeBase(BitmapSource? source)
    {
        if (_disposed || source == null)
        {
            ClearNativeCompositeBase();
            return;
        }

        try
        {
            var bgraSource = source.Format == PixelFormats.Bgra32
                ? source
                : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);

            var width = bgraSource.PixelWidth;
            var height = bgraSource.PixelHeight;
            if (width <= 0 || height <= 0)
            {
                ClearNativeCompositeBase();
                return;
            }

            var stride = checked(width * 4);
            var buffer = new byte[checked(stride * height)];
            bgraSource.CopyPixels(buffer, stride, 0);
            SetNativeCompositeBase(buffer, width, height, stride);
        }
        catch (Exception ex)
        {
            if (_configuration.EnableLogging)
            {
                Debug.WriteLine($"[OptimizedRenderer] Native composite base image failed: {ex.Message}");
            }
            ClearNativeCompositeBase();
        }
    }

    /// <summary>
    /// Sets the base visual for native composition from BGRA pixels.
    /// Static images/backgrounds use the normal coalesced static cadence.  Looping background video
    /// uses <see cref="SetNativeCompositeVideoBase"/> so the final native present remains driven by
    /// the actual video frame cadence instead of a separate compositor timer.
    /// </summary>
    public void SetNativeCompositeBase(ReadOnlySpan<byte> bgraPixels, int width, int height, int stride)
        => SetNativeCompositeBaseCore(bgraPixels, width, height, stride, isVideoBase: false);

    /// <summary>
    /// Sets the looping background-video base layer.  CDG updates are retained separately and are
    /// blended on the next background-video present instead of forcing their own full-frame present.
    /// </summary>
    public void SetNativeCompositeVideoBase(ReadOnlySpan<byte> bgraPixels, int width, int height, int stride)
        => SetNativeCompositeBaseCore(bgraPixels, width, height, stride, isVideoBase: true);

    private void SetNativeCompositeBaseCore(ReadOnlySpan<byte> bgraPixels, int width, int height, int stride, bool isVideoBase)
    {
        if (_disposed || width <= 0 || height <= 0 || stride < width * 4 || bgraPixels.IsEmpty)
        {
            ClearNativeCompositeBase();
            return;
        }

        var required = checked(stride * height);
        if (bgraPixels.Length < required)
        {
            ClearNativeCompositeBase();
            return;
        }

        if (isVideoBase && IsNativePresentationRequested)
        {
            // Experimental single-swap-chain GPU compositor path.  Keep the proven
            // NativeHwndSwapChain backend, upload the CDG overlay as a retained texture,
            // and draw video+CDG into one presentation surface/present.
            _nativeDirectCompositionLayersRequested = false;
            lock (_lock)
            {
                if (_nativeCompositeBaseWidth != width || _nativeCompositeBaseHeight != height)
                {
                    _nativeCompositeScaledCdgDirty = true;
                }

                _nativeCompositeBaseWidth = width;
                _nativeCompositeBaseHeight = height;
                _nativeCompositeBaseStride = stride;
                _nativeCompositeBaseIsVideo = true;
            }

            RenderNativeGpuCompositedVideoFrame(bgraPixels.Slice(0, required), width, height, stride);
            return;
        }

        lock (_nativeCompositeRenderLock)
        {
            lock (_lock)
            {
                if (_nativeCompositeBaseWidth != width || _nativeCompositeBaseHeight != height)
                {
                    _nativeCompositeScaledCdgDirty = true;
                }

                _nativeCompositeBaseWidth = width;
                _nativeCompositeBaseHeight = height;
                _nativeCompositeBaseStride = stride;
                if (_nativeCompositeBaseBgra == null || _nativeCompositeBaseBgra.Length != required)
                {
                    _nativeCompositeBaseBgra = new byte[required];
                }
                bgraPixels.Slice(0, required).CopyTo(_nativeCompositeBaseBgra);
                _nativeCompositeBaseIsVideo = isVideoBase;
            }
        }

        // Background video frames already arrive on the background video's own decode/clock cadence.
        // Present immediately when a new background frame is selected, but do not run a separate
        // steady presenter here.  The 60Hz steady presenter repeated the full CPU-composited
        // 1920x1080 frame even when the background frame had not changed, which made CDG +
        // background video visibly choppy.  CDG-only/static-image updates still use coalescing.
        RequestNativeCompositePresent(forceImmediate: isVideoBase);
    }

    public void ClearNativeCompositeBase()
    {
        lock (_nativeCompositeRenderLock)
        {
            lock (_lock)
            {
                _nativeCompositeBaseBgra = null;
                _nativeCompositeBaseWidth = 0;
                _nativeCompositeBaseHeight = 0;
                _nativeCompositeBaseStride = 0;
                _nativeCompositeBaseIsVideo = false;
            }
        }
    }

    /// <summary>
    /// Supplies the desired WPF D3DImage presentation surface size in physical pixels.
    /// Hosts should set this to the actual media rectangle inside the control, after
    /// applying DPI, Stretch, and aspect-ratio rules. A value of 0 resets to source size.
    /// </summary>
    public void SetWpfPresentationSize(int pixelWidth, int pixelHeight)
    {
        if (_disposed) return;

        // SourceNative and SourceCapped deliberately do not recreate or resize the shared
        // D3DImage surface when the WPF control changes size. WPF performs final layout scaling,
        // which avoids the resize flicker/stutter caused by repeated D3D9 shared-surface rebuilds.
        if (_configuration.WpfD3DImageSurfaceSizingMode != WpfD3DImageSurfaceSizingMode.DisplayRectangle)
        {
            return;
        }

        pixelWidth = Math.Max(0, pixelWidth);
        pixelHeight = Math.Max(0, pixelHeight);

        RunOnDispatcher(() =>
        {
            lock (_lock)
            {
                if (_requestedWpfPresentationWidth == pixelWidth
                    && _requestedWpfPresentationHeight == pixelHeight)
                {
                    return;
                }

                _requestedWpfPresentationWidth = pixelWidth;
                _requestedWpfPresentationHeight = pixelHeight;

                // Native HWND owns its own swap-chain sizing. For WPF D3DImage, the stable
                // default is SourceNative: keep the shared surface at the decoded media size and let
                // WPF lay it out. DisplayRectangle remains an experimental mode because recreating
                // D3D9 shared surfaces during live resize caused visible flicker/stutter in testing.
                if (_configuration.WpfD3DImageSurfaceSizingMode == WpfD3DImageSurfaceSizingMode.DisplayRectangle
                    && _nativePresentationHwnd == IntPtr.Zero
                    && _d3d11Renderer != null)
                {
                    var (desiredWidth, desiredHeight) = GetD3DImagePresentationSize(_lastWidth, _lastHeight);
                    if (desiredWidth != _lastPresentationWidth || desiredHeight != _lastPresentationHeight)
                    {
                        CleanupCurrentMethod();
                        _lastWidth = 0;
                        _lastHeight = 0;
                        _lastPresentationWidth = 0;
                        _lastPresentationHeight = 0;
                        ImageSourceChanged?.Invoke(this, ImageSource);
                    }
                }
            }
        }, DispatcherPriority.Render);
    }

    /// <summary>
    /// Creates a new OptimizedGraphicsRenderer with automatic configuration.
    /// </summary>
    public OptimizedGraphicsRenderer() : this(MediaPlayerConfiguration.Auto) { }

    /// <summary>
    /// Resolves the actual rendering method based on configuration and system capabilities.
    /// This is called once at creation time to ensure consistent behavior.
    /// </summary>
    private static GpuRenderingMethod ResolveRenderingMethod(RenderingMethod renderingMethod)
    {
        var capabilities = GpuRenderingCapabilities.Capabilities;

        return renderingMethod switch
        {
            RenderingMethod.Software => GpuRenderingMethod.WriteableBitmap,
            RenderingMethod.DirectX11 when capabilities.SupportsD3D11 => GpuRenderingMethod.D3D11,
            RenderingMethod.DirectX11 => GpuRenderingMethod.WriteableBitmap,
            _ => capabilities.SupportsD3D11
                ? GpuRenderingMethod.D3D11
                : GpuRenderingMethod.WriteableBitmap
        };
    }

    /// <summary>
    /// Runs <paramref name="action"/> on the dispatcher (UI) thread. When the caller is already on
    /// that thread the action runs inline; otherwise it is queued without blocking the caller.
    /// <para>
    /// This is critical for deadlock safety. Frame presentation and decode run on worker threads
    /// that hold playback locks (notably <c>_presentationLock</c> in <c>VideoPipeline</c>). A
    /// synchronous <see cref="Dispatcher.Invoke(Delegate, DispatcherPriority, object[])"/> from one
    /// of those threads will block waiting for the UI thread; if the UI thread is simultaneously
    /// inside a synchronous Seek (which waits on the very lock the worker is holding), the two
    /// threads wait on each other forever and the application hangs, then is killed by Windows as
    /// non-responsive. Queuing with <c>BeginInvoke</c> from worker threads breaks that cycle: the
    /// worker releases its lock promptly and the queued work runs once the UI thread is free.
    /// </para>
    /// </summary>
    private void RunOnDispatcher(Action action, DispatcherPriority priority)
    {
        if (_disposed)
        {
            return;
        }

        if (_dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            _dispatcher.BeginInvoke(action, priority);
        }
    }

    private int CurrentResourceGeneration => Volatile.Read(ref _resourceGeneration);

    private bool IsCurrentCpuSurface(WriteableBitmap bitmap, int generation, int width, int height)
    {
        return !_disposed
            && generation == CurrentResourceGeneration
            && ReferenceEquals(_bitmap, bitmap)
            && width == _lastWidth
            && height == _lastHeight
            && bitmap.PixelWidth == width
            && bitmap.PixelHeight == height;
    }

    /// <summary>
    /// Renders a decoded video frame. Hardware D3D11 frames are kept on the GPU when possible;
    /// CPU BGRA frames use the existing upload/writeable-bitmap paths.
    /// </summary>
    public void RenderVideoFrame(VideoFrameEventArgs frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (_disposed || frame.Width <= 0 || frame.Height <= 0) return;

        if (frame.IsD3D11HardwareFrame
            && _currentMethod == GpuRenderingMethod.D3D11
            && !ShouldUseCpuForContent(frame.Width, frame.Height))
        {
            RenderD3D11HardwareFrame(frame);
            return;
        }

        if (!frame.PixelData.IsEmpty)
        {
            RenderVideoFrame(frame.PixelData.Span, frame.Width, frame.Height, frame.Stride);
        }
    }

    /// <summary>
    /// Renders video frame data using the predetermined rendering method.
    /// </summary>
    /// <param name="pixelData">Frame pixel data (BGRA format)</param>
    /// <param name="width">Frame width</param>
    /// <param name="height">Frame height</param>
    /// <param name="stride">Frame stride</param>
    public void RenderVideoFrame(ReadOnlySpan<byte> pixelData, int width, int height, int stride)
    {
        if (_disposed || pixelData.IsEmpty || width <= 0 || height <= 0) return;

        if (width != _lastWidth || height != _lastHeight || PresentationSurfaceChanged(width, height))
        {
            InitializeRenderingResources(width, height);
        }

        lock (_lock)
        {
            try
            {
                // Check if we should skip GPU for small content (performance optimization)
                // Native HWND presentation must never fall back to the WPF/WriteableBitmap path
                // simply because the content is small. CDG/image/background compositor frames are
                // intentionally uploaded to the native swap-chain even when the source is tiny.
                var shouldUseCpu = !IsNativePresentationRequested && ShouldUseCpuForContent(width, height);
                var effectiveMethod = (shouldUseCpu && _currentMethod != GpuRenderingMethod.WriteableBitmap)
                    ? GpuRenderingMethod.WriteableBitmap
                    : _currentMethod;

                // Render using the effective method
                switch (effectiveMethod)
                {
                    case GpuRenderingMethod.D3D11:
                        RenderWithD3D11(pixelData, width, height, stride);
                        break;
                    default:
                        RenderWithCpuAsync(pixelData, width, height, stride);
                        break;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[OptimizedRenderer] Render error: {ex.Message}");
                FallbackToSoftwareRendering(pixelData, width, height, stride);
            }
        }
    }

    /// <summary>
    /// Renders CDG graphics with optimized ARGB to BGRA conversion.
    /// Now uses packet-driven updates - only called when CDG content actually changes.
    /// </summary>
    /// <param name="argbData">CDG frame data in ARGB format</param>
    /// <param name="width">Frame width</param>
    /// <param name="height">Frame height</param>
    public void SetCdgBackgroundTint(uint tintArgb)
    {
        // DIAGNOSTIC: Layer 2 (CDG background/color wash) is disabled for flicker testing.
        // Restore the original assignment below when re-enabling Layer 2:
        // _cdgBackgroundTintArgb = tintArgb;
        _cdgBackgroundTintArgb = 0;
        Debug.WriteLine($"[CDG-TINT] Layer 2 disabled; ignored SetCdgBackgroundTint: 0x{tintArgb:X8}");
    }

    public void RenderCdgFrame(ReadOnlySpan<int> argbData, int width, int height)
    {
        if (_disposed || argbData.IsEmpty || width <= 0 || height <= 0) return;

        if (IsNativePresentationRequested)
        {
            if (Volatile.Read(ref _nativeResizeInProgress) != 0)
            {
                return;
            }
            RenderCdgWithNativeComposite(argbData, width, height);
            return;
        }

        lock (_lock)
        {
            if (_hasActiveVideo)
            {
                return;
            }
        }

        try
        {
            EnsureCdgBitmap(width, height);
            RenderCdgWithCpuAsync(argbData, width, height);
        }
        catch (Exception ex)
        {
            if (_configuration.EnableLogging)
            {
                Debug.WriteLine($"[OptimizedRenderer] CDG error: {ex.Message}");
            }
            RenderCdgWithCpuAsync(argbData, width, height);
        }
    }

    private void EnsureCdgBitmap(int width, int height)
    {
        if (_bitmap != null && width == _lastWidth && height == _lastHeight)
            return;

        var generation = CurrentResourceGeneration;
        RunOnDispatcher(() =>
        {
            lock (_lock)
            {
                if (_disposed
                    || generation != CurrentResourceGeneration
                    || (_bitmap != null && width == _lastWidth && height == _lastHeight))
                {
                    return;
                }

                CleanupCurrentMethod();
                _bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
                _lastWidth = width;
                _lastHeight = height;
                _lastPresentationWidth = width;
                _lastPresentationHeight = height;
                if (_configuration.EnableLogging)
                {
                    Debug.WriteLine($"[OptimizedRenderer] Initialized CPU bitmap for CDG {width}x{height}");
                }
                ImageSourceChanged?.Invoke(this, ImageSource);
            }
        }, DispatcherPriority.Normal);
    }

    /// <summary>
    /// Clears all content flags - call when switching media.
    /// </summary>
    public void ClearAllContent()
    {
        Interlocked.Increment(ref _resourceGeneration);
        lock (_lock)
        {
            _hasActiveVideo = false;
            ClearNativeCompositeStateUnsafe();
        }

        RunOnDispatcher(() =>
        {
            lock (_lock)
            {
                CleanupCurrentMethod();
                _lastWidth = 0;
                _lastHeight = 0;
                _lastPresentationWidth = 0;
                _lastPresentationHeight = 0;
                ImageSourceChanged?.Invoke(this, null);
            }
        }, DispatcherPriority.Send);
    }

    /// <summary>
    /// Determines whether to use CPU rendering for the given content size,
    /// even if GPU rendering is configured. This is a performance optimization.
    /// </summary>
    private bool ShouldUseCpuForContent(int width, int height)
    {
        var pixelCount = width * height;
        return pixelCount < _configuration.GpuRenderingMinResolution;
    }

    private void InitializeRenderingResources(int width, int height)
    {
        var pixelCount = width * height;
        var shouldForceCpu = pixelCount < _configuration.GpuRenderingMinResolution;
        var generation = CurrentResourceGeneration;

        RunOnDispatcher(() =>
        {
            lock (_lock)
            {
                // A frame at this size may already have been initialized by an earlier queued
                // pass (several frames can enqueue before the first runs); skip redundant churn.
                var (presentationWidth, presentationHeight) = GetPresentationSizeForActiveTarget(width, height);
                if (_disposed
                    || generation != CurrentResourceGeneration
                    || (width == _lastWidth
                        && height == _lastHeight
                        && presentationWidth == _lastPresentationWidth
                        && presentationHeight == _lastPresentationHeight))
                {
                    return;
                }

                CleanupCurrentMethod();

                bool success;
                if (shouldForceCpu && _nativePresentationHwnd == IntPtr.Zero)
                {
                    success = InitializeCpu(width, height);
                }
                else
                {
                    success = InitializeRenderingMethod(_currentMethod, width, height);
                    if (!success && _nativePresentationHwnd == IntPtr.Zero)
                    {
                        success = InitializeRenderingMethod(GpuRenderingMethod.WriteableBitmap, width, height);
                    }
                    else if (!success)
                    {
                        if (_configuration.EnableLogging)
                        {
                            Debug.WriteLine("[OptimizedRenderer] Native HWND presentation requested, but D3D11 native target could not initialize. Not falling back to D3DImage/bitmap for this strict diagnostic mode.");
                        }
                    }
                }

                if (success)
                {
                    _lastWidth = width;
                    _lastHeight = height;
                    (_lastPresentationWidth, _lastPresentationHeight) = GetPresentationSizeForActiveTarget(width, height);
                }
                else
                {
                    _lastWidth = 0;
                    _lastHeight = 0;
                    _lastPresentationWidth = 0;
                    _lastPresentationHeight = 0;
                }

                ImageSourceChanged?.Invoke(this, ImageSource);
            }
        }, DispatcherPriority.Normal);
    }

    private bool InitializeRenderingMethod(GpuRenderingMethod method, int width, int height)
    {
        try
        {
            return method switch
            {
                GpuRenderingMethod.D3D11 => InitializeD3D11(width, height),
                _ => InitializeCpu(width, height)
            };
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[OptimizedRenderer] Failed to initialize {method}: {ex.Message}");
            return false;
        }
    }

    #region D3D11 Rendering Implementation

    private bool InitializeD3D11(int width, int height)
    {
        _d3d11Renderer = new VorticeD3D11Renderer(_configuration);
        if (!_d3d11Renderer.Initialize())
        {
            _d3d11Renderer.Dispose();
            _d3d11Renderer = null;
            return false;
        }

        _d3d11Renderer.SetNativePresentationStretch(_nativePresentationStretch);

        if (!CreateActiveD3D11Surfaces(_d3d11Renderer, width, height))
        {
            _d3d11Renderer.Dispose();
            _d3d11Renderer = null;
            return false;
        }

        _d3d11Renderer.FramePresented += OnD3D11FramePresented;
        return true;
    }

    private bool CreateActiveD3D11Surfaces(VorticeD3D11Renderer renderer, int width, int height)
    {
        var hwnd = _nativePresentationHwnd;
        if (hwnd != IntPtr.Zero)
        {
            var success = false;
            if (_nativeDirectCompositionLayersRequested)
            {
                // Keep DirectComposition layer swap-chains at the media/source size. Live control
                // resizing is handled by DComp visual transforms, not by recreating swap-chains.
                success = renderer.CreateDirectCompositionHwndSurfaces(hwnd, width, height);
                if (success)
                {
                    var (outputWidth, outputHeight) = GetNativeOutputSize(width, height);
                    renderer.SetDirectCompositionOutputSize(outputWidth, outputHeight);
                    if (_configuration.EnableLogging)
                    {
                        Debug.WriteLine($"[OptimizedRenderer] Active presentation backend: DirectCompositionNativeLayers, source={width}x{height}, surface={width}x{height}, output={outputWidth}x{outputHeight}");
                    }
                }
            }

            if (!success)
            {
                var (outputWidth, outputHeight) = GetNativeOutputSize(width, height);
                success = renderer.CreateNativeHwndSurfaces(hwnd, outputWidth, outputHeight);
                if (success && _configuration.EnableLogging)
                {
                    Debug.WriteLine($"[OptimizedRenderer] Active presentation backend: NativeHwndSwapChain, source={width}x{height}, output={outputWidth}x{outputHeight}");
                }
            }
            return success;
        }

        var (surfaceWidth, surfaceHeight) = GetD3DImagePresentationSize(width, height);
        var d3dImageSuccess = renderer.CreateSurfaces(surfaceWidth, surfaceHeight);
        if (d3dImageSuccess && _configuration.EnableLogging)
        {
            Debug.WriteLine($"[OptimizedRenderer] Active presentation backend: D3DImage, source={width}x{height}, surface={surfaceWidth}x{surfaceHeight}");
        }
        return d3dImageSuccess;
    }

    private bool PresentationSurfaceChanged(int sourceWidth, int sourceHeight)
    {
        if (_nativePresentationHwnd != IntPtr.Zero)
        {
            if (_nativeDirectCompositionLayersRequested
                && _d3d11Renderer?.IsDirectCompositionActive != true)
            {
                return true;
            }

            if (!_nativeDirectCompositionLayersRequested)
            {
                // Host-size changes are handled by the existing native swap-chain at the next
                // frame boundary. Do not tear down/recreate the renderer just because WPF layout
                // produced another intermediate size during manual resize.
                return false;
            }
        }

        var (desiredWidth, desiredHeight) = GetPresentationSizeForActiveTarget(sourceWidth, sourceHeight);
        if (desiredWidth == _lastPresentationWidth && desiredHeight == _lastPresentationHeight)
        {
            return false;
        }

        return true;
    }

    private (int width, int height) GetNativeOutputSize(int sourceWidth, int sourceHeight)
    {
        if (_requestedNativePresentationWidth > 0 && _requestedNativePresentationHeight > 0)
        {
            return (_requestedNativePresentationWidth, _requestedNativePresentationHeight);
        }

        return (Math.Max(1, sourceWidth), Math.Max(1, sourceHeight));
    }

    private (int width, int height) GetPresentationSizeForActiveTarget(int sourceWidth, int sourceHeight)
    {
        sourceWidth = Math.Max(1, sourceWidth);
        sourceHeight = Math.Max(1, sourceHeight);

        if (_nativePresentationHwnd != IntPtr.Zero)
        {
            if (_nativeDirectCompositionLayersRequested)
            {
                // DirectComposition layer swap-chains stay source-sized; host/control resize is
                // handled by DComp visual transforms.
                return (sourceWidth, sourceHeight);
            }

            // Native HWND swap-chain uses a stable HWND but a host-sized backbuffer.  This lets
            // D3D own the viewport/letterbox rectangle instead of letting USER32/DXGI stretch a
            // source-sized swap-chain during WPF live resize.
            return GetNativeOutputSize(sourceWidth, sourceHeight);
        }

        return GetD3DImagePresentationSize(sourceWidth, sourceHeight);
    }

    private (int width, int height) GetD3DImagePresentationSize(int sourceWidth, int sourceHeight)
    {
        sourceWidth = Math.Max(1, sourceWidth);
        sourceHeight = Math.Max(1, sourceHeight);

        if (_configuration.WpfD3DImageSurfaceSizingMode == WpfD3DImageSurfaceSizingMode.DisplayRectangle
            && _requestedWpfPresentationWidth > 0
            && _requestedWpfPresentationHeight > 0)
        {
            return (_requestedWpfPresentationWidth, _requestedWpfPresentationHeight);
        }

        if (_configuration.WpfD3DImageSurfaceSizingMode == WpfD3DImageSurfaceSizingMode.SourceCapped)
        {
            return CapPresentationSize(sourceWidth, sourceHeight);
        }

        return (sourceWidth, sourceHeight);
    }

    private (int width, int height) CapPresentationSize(int sourceWidth, int sourceHeight)
    {
        var maxWidth = _configuration.MaxWpfD3DImageSurfaceWidth;
        var maxHeight = _configuration.MaxWpfD3DImageSurfaceHeight;
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

    private void OnD3D11FramePresented(object? sender, EventArgs e)
    {
        RaiseFramePresented();
    }

    private void RaiseFramePresented()
    {
        if (_disposed)
        {
            return;
        }

        // Native HWND video does not require a WPF visual-tree nudge for every frame.
        // During a manual window resize, dispatching FramePresented at video cadence
        // competes with WPF's layout/resize loop and is a major source of slow drag
        // and post-drag catch-up.  The native swap-chain has already presented.
        if (IsNativePresentationRequested && Volatile.Read(ref _nativeResizeInProgress) != 0)
        {
            return;
        }

        if (_dispatcher.CheckAccess())
        {
            FramePresented?.Invoke(this, EventArgs.Empty);
            return;
        }

        try
        {
            _dispatcher.BeginInvoke(new Action(() =>
            {
                if (!_disposed)
                {
                    FramePresented?.Invoke(this, EventArgs.Empty);
                }
            }), DispatcherPriority.Render);
        }
        catch
        {
            // Dispatcher shutdown/race during teardown. Presentation itself has already completed.
        }
    }

    private void RenderWithD3D11(ReadOnlySpan<byte> pixelData, int width, int height, int stride)
    {
        if (_d3d11Renderer?.IsReady == true)
        {
            _d3d11Renderer.UploadFrame(pixelData, width, height, stride);
            return;
        }

        if (IsNativePresentationRequested)
        {
            // Native CDG-only/static-image content may not have a continuous video frame cadence.
            // The previous async dispatcher initialization skipped the first native frame and then
            // waited for another media frame that sometimes never arrived. Initialize the native
            // D3D11 target synchronously at the first real native blit so CDG-only files appear.
            if (EnsureNativeD3D11ReadyForFrame(width, height) && _d3d11Renderer?.IsReady == true)
            {
                _d3d11Renderer.UploadFrame(pixelData, width, height, stride);
            }
            else if (_configuration.EnableVerboseLogging)
            {
                Debug.WriteLine("[OptimizedRenderer] Native D3D11 surface not ready; frame skipped instead of WPF fallback.");
            }
            return;
        }

        // Fallback to CPU only for the explicit WPF fallback path.
        FallbackToSoftwareRendering(pixelData, width, height, stride);
    }

    private bool EnsureNativeD3D11ReadyForFrame(int width, int height)
    {
        if (!IsNativePresentationRequested || _disposed || width <= 0 || height <= 0)
        {
            return false;
        }

        if (_d3d11Renderer?.IsReady == true)
        {
            return true;
        }

        try
        {
            CleanupCurrentMethod();

            if (!InitializeD3D11(width, height))
            {
                _lastWidth = 0;
                _lastHeight = 0;
                _lastPresentationWidth = 0;
                _lastPresentationHeight = 0;
                return false;
            }

            _lastWidth = width;
            _lastHeight = height;
            (_lastPresentationWidth, _lastPresentationHeight) = GetPresentationSizeForActiveTarget(width, height);

            // Native HWND presentation has no WPF ImageSource to swap, but raising the event keeps
            // attachment/layout bookkeeping in sync with the first real native presentation.
            ImageSourceChanged?.Invoke(this, ImageSource);
            return true;
        }
        catch (Exception ex)
        {
            if (_configuration.EnableLogging)
            {
                Debug.WriteLine($"[OptimizedRenderer] Native D3D11 immediate initialization failed: {ex.Message}");
            }
            return false;
        }
    }

    #endregion

    #region CPU Rendering Implementation

    private bool InitializeCpu(int width, int height)
    {
        try
        {
            _bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[OptimizedRenderer] Failed to initialize CPU resources: {ex.Message}");
            return false;
        }
    }

    private void RenderWithCpuAsync(ReadOnlySpan<byte> pixelData, int width, int height, int stride)
    {
        var bitmap = _bitmap;
        if (bitmap == null) return;

        var generation = CurrentResourceGeneration;
        var pixelArray = ArrayPool<byte>.Shared.Rent(pixelData.Length);
        pixelData.CopyTo(pixelArray);

        if (_dispatcher.CheckAccess())
        {
            try
            {
                if (WriteCpuFrame(bitmap, generation, pixelArray, width, height, stride))
                {
                    RaiseFramePresented();
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(pixelArray);
            }

            return;
        }

        try
        {
            _dispatcher.BeginInvoke(() =>
            {
                try
                {
                    if (WriteCpuFrame(bitmap, generation, pixelArray, width, height, stride))
                    {
                        RaiseFramePresented();
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(pixelArray);
                }
            }, DispatcherPriority.Render);
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(pixelArray);
            throw;
        }
    }

    private void RenderD3D11HardwareFrame(VideoFrameEventArgs frame)
    {
        if (frame.D3D11DevicePointer == IntPtr.Zero || frame.D3D11TexturePointer == IntPtr.Zero)
        {
            return;
        }

        if (_d3d11Renderer?.NativeDevicePointer != frame.D3D11DevicePointer
            || frame.Width != _lastWidth
            || frame.Height != _lastHeight
            || PresentationSurfaceChanged(frame.Width, frame.Height))
        {
            InitializeHardwareD3D11(frame);
        }

        lock (_lock)
        {
            if (_d3d11Renderer?.IsReady != true)
            {
                if (_configuration.EnableLogging) Debug.WriteLine("[OptimizedRenderer] D3D11 hardware renderer is not ready; frame skipped.");
                return;
            }

            var presented = _d3d11Renderer.PresentHardwareFrame(
                frame.D3D11TexturePointer,
                frame.D3D11ArraySlice,
                frame.Width,
                frame.Height,
                frame.FramesPerSecond);

            if (!presented)
            {
                if (_configuration.EnableLogging) Debug.WriteLine("[OptimizedRenderer] D3D11 hardware frame presentation failed; frame skipped.");
            }
        }
    }

    private void InitializeHardwareD3D11(VideoFrameEventArgs frame)
    {
        var generation = CurrentResourceGeneration;
        RunOnDispatcher(() =>
        {
            lock (_lock)
            {
                var (presentationWidth, presentationHeight) = GetPresentationSizeForActiveTarget(frame.Width, frame.Height);

                // Skip if an earlier queued pass already initialized this exact device/source/presentation size.
                if (_disposed
                    || generation != CurrentResourceGeneration
                    || (_d3d11Renderer?.NativeDevicePointer == frame.D3D11DevicePointer
                        && frame.Width == _lastWidth
                        && frame.Height == _lastHeight
                        && presentationWidth == _lastPresentationWidth
                        && presentationHeight == _lastPresentationHeight))
                {
                    return;
                }

                CleanupCurrentMethod();
                _d3d11Renderer = new VorticeD3D11Renderer(_configuration);
                var success = _d3d11Renderer.InitializeFromNativeDevice(
                    frame.D3D11DevicePointer,
                    frame.D3D11DeviceContextPointer);

                if (success)
                {
                    success = CreateActiveD3D11Surfaces(_d3d11Renderer, frame.Width, frame.Height);
                }

                if (!success)
                {
                    _d3d11Renderer.Dispose();
                    _d3d11Renderer = null;
                    _lastWidth = 0;
                    _lastHeight = 0;
                    _lastPresentationWidth = 0;
                    _lastPresentationHeight = 0;
                    if (_configuration.EnableLogging)
                    {
                        Debug.WriteLine("[OptimizedRenderer] Failed to initialize hardware D3D11 presentation surface.");
                    }
                    ImageSourceChanged?.Invoke(this, ImageSource);
                    return;
                }

                _d3d11Renderer.FramePresented += OnD3D11FramePresented;
                _lastWidth = frame.Width;
                _lastHeight = frame.Height;
                (_lastPresentationWidth, _lastPresentationHeight) = GetPresentationSizeForActiveTarget(frame.Width, frame.Height);
                ImageSourceChanged?.Invoke(this, ImageSource);
            }
        }, DispatcherPriority.Normal);
    }

    private bool WriteCpuFrame(WriteableBitmap bitmap, int generation, byte[] pixelArray, int width, int height, int stride)
    {
        try
        {
            if (!IsCurrentCpuSurface(bitmap, generation, width, height))
            {
                return false;
            }

            bitmap.Lock();
            try
            {
                if (!IsCurrentCpuSurface(bitmap, generation, width, height))
                {
                    return false;
                }

                bitmap.WritePixels(new Int32Rect(0, 0, width, height), pixelArray, stride, 0);
                return true;
            }
            finally
            {
                bitmap.Unlock();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[OptimizedRenderer] CPU render failed: {ex.Message}");
            return false;
        }
    }

    private void RenderCdgWithCpuAsync(ReadOnlySpan<int> argbData, int width, int height)
    {
        var bitmap = _bitmap;
        if (bitmap == null) return;

        var generation = CurrentResourceGeneration;
        var expectedSize = argbData.Length * 4;
        var renderBuffer = ArrayPool<byte>.Shared.Rent(expectedSize);

        ConvertArgbToBgraInPlace(argbData, renderBuffer);

        try
        {
            _dispatcher.BeginInvoke(() =>
            {
                try
                {
                    var stride = width * 4;
                    if (WriteCpuFrame(bitmap, generation, renderBuffer, width, height, stride))
                    {
                        // The WriteableBitmap instance is stable; only the pixels changed.
                        // Reassigning Image.Source every CDG packet costs WPF work and can reduce frame cadence.
                        RaiseFramePresented();
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[OptimizedRenderer] CDG render failed: {ex.Message}");
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(renderBuffer);
                }
            }, DispatcherPriority.Render);
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(renderBuffer);
            throw;
        }
    }

    private void RenderCdgWithNativeComposite(ReadOnlySpan<int> argbData, int width, int height)
    {
        try
        {
            lock (_nativeCompositeRenderLock)
            {
                lock (_lock)
                {
                    var count = checked(width * height);
                    if (_nativeCompositeCdgArgb == null || _nativeCompositeCdgArgb.Length != count)
                    {
                        _nativeCompositeCdgArgb = new int[count];
                    }

                    argbData.Slice(0, count).CopyTo(_nativeCompositeCdgArgb);
                    _nativeCompositeCdgWidth = width;
                    _nativeCompositeCdgHeight = height;
                    _nativeCompositeScaledCdgDirty = true;
                    _nativeCachedCdgOverlayDirty = true;
                }
            }

            if (_nativeDirectCompositionLayersRequested)
            {
                TryUploadDirectCompositionCdgOverlay();
                return;
            }

            // Background video + CDG uses a single native HWND swap-chain compositor.  CDG
            // updates refresh the retained overlay texture; the next background-video frame
            // performs the one final GPU composite/present.
            TryUploadNativeGpuCdgOverlay();

            // If a looping video is the compositor base, the background-video presentation loop owns
            // the final present cadence.  This CDG update is retained and blended on the next video
            // frame, preventing CDG invalidations from changing the apparent background-video FPS.
            bool videoDriven;
            lock (_lock)
            {
                videoDriven = _nativeCompositeBaseIsVideo;
            }

            if (!videoDriven)
            {
                // CDG-only and CDG+static-image backgrounds have no video frame cadence.
                // Queue the static composite immediately so the first CDG frame does not wait
                // for a background-video renderer that may never exist.
                RequestNativeCompositePresent(forceImmediate: true);
            }
        }
        catch (Exception ex)
        {
            if (_configuration.EnableLogging)
            {
                Debug.WriteLine($"[OptimizedRenderer] Native CDG composite failed: {ex.Message}");
            }
        }
    }


    private bool HasRetainedNativeCdgFrame()
    {
        lock (_lock)
        {
            return HasRetainedNativeCdgFrameUnsafe();
        }
    }

    private bool HasRetainedNativeCdgFrameUnsafe()
    {
        return _nativeCompositeCdgArgb != null
            && _nativeCompositeCdgWidth > 0
            && _nativeCompositeCdgHeight > 0;
    }

    private void PresentNativeCompositeVideoFallback(ReadOnlySpan<byte> pixelData, int width, int height, int stride)
    {
        if (_disposed || pixelData.IsEmpty || width <= 0 || height <= 0 || stride < width * 4)
        {
            return;
        }

        var required = checked(stride * height);
        if (pixelData.Length < required)
        {
            return;
        }

        lock (_nativeCompositeRenderLock)
        {
            lock (_lock)
            {
                if (_nativeCompositeBaseBgra == null || _nativeCompositeBaseBgra.Length != required)
                {
                    _nativeCompositeBaseBgra = new byte[required];
                }

                pixelData.Slice(0, required).CopyTo(_nativeCompositeBaseBgra);
                _nativeCompositeBaseWidth = width;
                _nativeCompositeBaseHeight = height;
                _nativeCompositeBaseStride = stride;
                _nativeCompositeBaseIsVideo = true;
                _nativeCompositeScaledCdgDirty = true;
            }

            PresentNativeCompositeCore();
        }
    }


    private void RenderNativeGpuCompositedVideoFrame(ReadOnlySpan<byte> pixelData, int width, int height, int stride)
    {
        if (_disposed || pixelData.IsEmpty || width <= 0 || height <= 0 || stride < width * 4)
        {
            return;
        }

        if (width != _lastWidth || height != _lastHeight || PresentationSurfaceChanged(width, height))
        {
            InitializeRenderingResources(width, height);
        }

        if (_nativeDirectCompositionLayersRequested)
        {
            _d3d11Renderer?.UploadFrame(pixelData, width, height, stride);
            return;
        }

        var useFallbackComposite = false;
        var uploadPlainFrame = false;

        // Ensure the CDG GPU texture exists before compositing — otherwise
        // UploadFrameWithCachedCdgOverlay silently skips the CDG+etint draw pass
        // and returns true, producing a video-only frame.
        if (_d3d11Renderer?.HasCdgOverlayTexture != true && HasRetainedNativeCdgFrameUnsafe())
        {
            TryUploadNativeGpuCdgOverlay();
        }

        try
        {
            lock (_lock)
            {
                if (_d3d11Renderer?.IsReady != true)
                {
                    if (_configuration.EnableVerboseLogging)
                    {
                        Debug.WriteLine("[OptimizedRenderer] Native cached-CDG surface not ready yet; background frame skipped.");
                    }
                    return;
                }

                // DIAGNOSTIC: Layer 2 (CDG background/color wash) is disabled for flicker testing.
                // Original behavior read _cdgBackgroundTintArgb and defaulted to opaque black
                // while CDG layering was active. Keep the background wash transparent for now.
                var tintArgb = 0u;
                var cdgLayeringActive = _nativeCompositeBaseUseCdgLayering;
                var fallbackUsed = false;
                // Restore this original fallback when re-enabling Layer 2:
                // if (tintArgb == 0 && cdgLayeringActive)
                // {
                //     tintArgb = 0xFF000000u;
                //     fallbackUsed = true;
                // }
                Debug.WriteLine($"[CDG-TINT] Layer 2 disabled; RenderNativeCompositedVideo: tint=0x{tintArgb:X8} fallback={fallbackUsed} cdgLayering={cdgLayeringActive} hasCdgFrame={HasRetainedNativeCdgFrameUnsafe()}");
                if (!_d3d11Renderer.UploadFrameWithCachedCdgOverlay(pixelData, width, height, stride, 60.0, tintArgb, cdgLayeringActive))
                {
                    // Do not call the fallback compositor while holding _lock.  The fallback path
                    // takes _nativeCompositeRenderLock and then _lock, so calling it here can
                    // self-deadlock the player as soon as a CDG frame starts.
                    if (HasRetainedNativeCdgFrameUnsafe())
                    {
                        useFallbackComposite = true;
                    }
                    else
                    {
                        uploadPlainFrame = true;
                    }
                }
            }

            if (useFallbackComposite)
            {
                PresentNativeCompositeVideoFallback(pixelData, width, height, stride);
            }
            else if (uploadPlainFrame)
            {
                _d3d11Renderer?.UploadFrame(pixelData, width, height, stride);
            }
        }
        catch (Exception ex)
        {
            if (_configuration.EnableLogging)
            {
                Debug.WriteLine($"[OptimizedRenderer] Native cached-CDG video frame failed: {ex.Message}");
            }
        }
    }

    private bool TryUploadNativeGpuCdgOverlay(int destinationWidth = 0, int destinationHeight = 0)
    {
        if (_disposed || !IsNativePresentationRequested)
        {
            return false;
        }

        try
        {
            lock (_nativeCompositeRenderLock)
            {
                lock (_lock)
                {
                    if (_d3d11Renderer?.IsReady != true)
                    {
                        return false;
                    }

                    if (_nativeCompositeCdgArgb == null
                        || _nativeCompositeCdgWidth <= 0
                        || _nativeCompositeCdgHeight <= 0)
                    {
                        _d3d11Renderer.ClearCdgOverlayTexture();
                        return false;
                    }

                    // Keep the CDG overlay source-sized.  The D3D renderer scales this cached
                    // alpha texture in the final draw pass.  This avoids allocating/updating a
                    // full 1080p/4K BGRA overlay buffer just because CDG is active.
                    var overlayWidth = _nativeCompositeCdgWidth;
                    var overlayHeight = _nativeCompositeCdgHeight;
                    var overlayStride = checked(overlayWidth * 4);
                    var required = checked(overlayStride * overlayHeight);

                    if (_directCompositionCdgOverlayBgra == null
                        || _directCompositionCdgOverlayBgra.Length != required
                        || _directCompositionCdgOverlayWidth != overlayWidth
                        || _directCompositionCdgOverlayHeight != overlayHeight
                        || _directCompositionCdgOverlayStride != overlayStride
                        || _nativeCachedCdgOverlayDirty)
                    {
                        if (_directCompositionCdgOverlayBgra == null || _directCompositionCdgOverlayBgra.Length != required)
                        {
                            _directCompositionCdgOverlayBgra = new byte[required];
                        }

                        ConvertArgbToBgraInPlace(_nativeCompositeCdgArgb.AsSpan(0, overlayWidth * overlayHeight), _directCompositionCdgOverlayBgra);
                        _directCompositionCdgOverlayWidth = overlayWidth;
                        _directCompositionCdgOverlayHeight = overlayHeight;
                        _directCompositionCdgOverlayStride = overlayStride;
                        _nativeCachedCdgOverlayDirty = false;

                        return _d3d11Renderer.UploadCdgOverlayTexture(
                            _directCompositionCdgOverlayBgra.AsSpan(0, required),
                            overlayWidth,
                            overlayHeight,
                            overlayStride);
                    }

                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            if (_configuration.EnableLogging)
            {
                Debug.WriteLine($"[OptimizedRenderer] Native cached CDG texture upload failed: {ex.Message}");
            }
            return false;
        }
    }

    private bool TryUploadDirectCompositionCdgOverlay()
    {
        if (_disposed || !_nativeDirectCompositionLayersRequested)
        {
            if (_configuration.EnableVerboseLogging)
            {
                Debug.WriteLine($"[CDG-DC-TRACE] TryUploadDirectCompositionCdgOverlay -> false (_disposed={_disposed}, _layersRequested={_nativeDirectCompositionLayersRequested})");
            }
            return false;
        }

        var resizeInProgress = Volatile.Read(ref _nativeResizeInProgress) != 0;

        try
        {
            lock (_nativeCompositeRenderLock)
            {
                lock (_lock)
                {
                    if (_d3d11Renderer?.IsDirectCompositionActive != true)
                    {
                        if (_configuration.EnableVerboseLogging)
                        {
                            Debug.WriteLine($"[CDG-DC-TRACE] TryUploadDirectCompositionCdgOverlay -> false (IsDirectCompositionActive != true, resize={resizeInProgress})");
                        }
                        return false;
                    }

                    if (_nativeCompositeCdgArgb == null
                        || _nativeCompositeCdgWidth <= 0
                        || _nativeCompositeCdgHeight <= 0)
                    {
                        if (_configuration.EnableVerboseLogging)
                        {
                            Debug.WriteLine($"[CDG-DC-TRACE] TryUploadDirectCompositionCdgOverlay -> false (no CDG data: Argb={(object?)_nativeCompositeCdgArgb}, w={_nativeCompositeCdgWidth}, h={_nativeCompositeCdgHeight}, resize={resizeInProgress})");
                        }
                        return false;
                    }

                    // The CDG overlay swap-chain is source-sized. DirectComposition scales
                    // the whole overlay visual to the current native output size, so CDG resizes
                    // live with the background video without rebuilding or reuploading a giant
                    // output-sized overlay on every host resize.
                    var destinationWidth = _lastWidth > 0 ? _lastWidth : _nativeCompositeBaseWidth;
                    var destinationHeight = _lastHeight > 0 ? _lastHeight : _nativeCompositeBaseHeight;

                    if (destinationWidth <= 0 || destinationHeight <= 0)
                    {
                        if (_configuration.EnableVerboseLogging)
                        {
                            Debug.WriteLine($"[CDG-DC-TRACE] TryUploadDirectCompositionCdgOverlay -> false (no dest: _lastW={_lastWidth}, _lastH={_lastHeight}, baseW={_nativeCompositeBaseWidth}, baseH={_nativeCompositeBaseHeight}, resize={resizeInProgress})");
                        }
                        return false;
                    }

                    var destinationStride = checked(destinationWidth * 4);
                    var required = checked(destinationStride * destinationHeight);
                    var scaled = false;
                    if (_directCompositionCdgOverlayBgra == null
                        || _directCompositionCdgOverlayBgra.Length != required
                        || _directCompositionCdgOverlayWidth != destinationWidth
                        || _directCompositionCdgOverlayHeight != destinationHeight
                        || _directCompositionCdgOverlayStride != destinationStride
                        || _nativeCompositeScaledCdgDirty)
                    {
                        scaled = true;
                        if (_directCompositionCdgOverlayBgra == null || _directCompositionCdgOverlayBgra.Length != required)
                        {
                            _directCompositionCdgOverlayBgra = new byte[required];
                        }

                        Array.Clear(_directCompositionCdgOverlayBgra, 0, required);
                        var cdgRect = CalculateAspectFitRect(
                            destinationWidth,
                            destinationHeight,
                            _nativeCompositeCdgWidth,
                            _nativeCompositeCdgHeight);

                        ScaleArgbToPremultipliedBgraOverlay(
                            _nativeCompositeCdgArgb,
                            _nativeCompositeCdgWidth,
                            _nativeCompositeCdgHeight,
                            _directCompositionCdgOverlayBgra,
                            destinationWidth,
                            destinationHeight,
                            destinationStride,
                            cdgRect.x,
                            cdgRect.y,
                            cdgRect.width,
                            cdgRect.height);

                        _directCompositionCdgOverlayWidth = destinationWidth;
                        _directCompositionCdgOverlayHeight = destinationHeight;
                        _directCompositionCdgOverlayStride = destinationStride;
                        _nativeCompositeScaledCdgDirty = false;
                    }

                    var result = _d3d11Renderer.UploadDirectCompositionOverlayFrame(
                        _directCompositionCdgOverlayBgra.AsSpan(0, required),
                        destinationWidth,
                        destinationHeight,
                        destinationStride);

                    if (_configuration.EnableVerboseLogging)
                    {
                        Debug.WriteLine($"[CDG-DC-TRACE] TryUploadDirectCompositionCdgOverlay -> {result} (dest={destinationWidth}x{destinationHeight}, scaled={scaled}, resize={resizeInProgress})");
                    }

                    return result;
                }
            }
        }
        catch (Exception ex)
        {
            if (_configuration.EnableLogging)
            {
                Debug.WriteLine($"[OptimizedRenderer] DirectComposition CDG overlay upload failed: {ex.Message}");
            }
            if (_configuration.EnableVerboseLogging)
            {
                Debug.WriteLine($"[CDG-DC-TRACE] TryUploadDirectCompositionCdgOverlay -> exception: {ex.Message}, resize={resizeInProgress}");
            }
            return false;
        }
    }

    private void RequestNativeCompositePresent(bool forceImmediate = false)
    {
        if (_disposed || !IsNativePresentationRequested)
        {
            return;
        }

        Volatile.Write(ref _nativeCompositeDirty, 1);

        if (ShouldFreezeNativePresentationDuringResize())
        {
            return;
        }

        if (forceImmediate)
        {
            QueueNativeCompositePresent(TimeSpan.Zero);
            return;
        }

        var now = Stopwatch.GetTimestamp();
        var minTicks = Math.Max(1L, (long)(Stopwatch.Frequency / NativeCompositeStaticMaxPresentFps));
        var last = Volatile.Read(ref _nativeCompositeLastPresentTimestamp);
        var elapsed = last <= 0 ? minTicks : now - last;

        if (elapsed >= minTicks)
        {
            QueueNativeCompositePresent(TimeSpan.Zero);
            return;
        }

        var delayTicks = minTicks - elapsed;
        var delay = TimeSpan.FromSeconds(delayTicks / (double)Stopwatch.Frequency);
        QueueNativeCompositePresent(delay);
    }

    private void QueueNativeCompositePresent(TimeSpan delay)
    {
        if (Interlocked.CompareExchange(ref _nativeCompositePresentQueued, 1, 0) != 0)
        {
            return;
        }

        if (delay <= TimeSpan.Zero)
        {
            ThreadPool.QueueUserWorkItem(_ => RunQueuedNativeCompositePresent());
            return;
        }

        _ = Task.Delay(delay).ContinueWith(
            _ => RunQueuedNativeCompositePresent(),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void RunQueuedNativeCompositePresent()
    {
        try
        {
            if (_disposed || !IsNativePresentationRequested || ShouldFreezeNativePresentationDuringResize())
            {
                return;
            }

            Volatile.Write(ref _nativeCompositeDirty, 0);
            PresentNativeComposite();
            Volatile.Write(ref _nativeCompositeLastPresentTimestamp, Stopwatch.GetTimestamp());
        }
        finally
        {
            Volatile.Write(ref _nativeCompositePresentQueued, 0);

            // If another layer update arrived while this presentation was running, queue one more
            // coalesced pass. This preserves responsiveness without presenting once per CDG packet.
            if (!_disposed && IsNativePresentationRequested && Volatile.Read(ref _nativeCompositeDirty) != 0)
            {
                RequestNativeCompositePresent();
            }
        }
    }

    private void PresentNativeComposite()
    {
        lock (_nativeCompositeRenderLock)
        {
            PresentNativeCompositeCore();
        }
    }

    private void PresentNativeCompositeCore()
    {
        byte[]? baseBgra;
        int baseWidth;
        int baseHeight;
        int baseStride;
        int[]? cdgArgb;
        int cdgWidth;
        int cdgHeight;
        bool cdgLayeringActive;

        lock (_lock)
        {
            baseBgra = _nativeCompositeBaseBgra;
            baseWidth = _nativeCompositeBaseWidth;
            baseHeight = _nativeCompositeBaseHeight;
            baseStride = _nativeCompositeBaseStride;
            cdgArgb = _nativeCompositeCdgArgb;
            cdgWidth = _nativeCompositeCdgWidth;
            cdgHeight = _nativeCompositeCdgHeight;
            cdgLayeringActive = _nativeCompositeBaseUseCdgLayering;

            if (baseBgra == null && cdgArgb == null)
            {
                return;
            }
        }

        var outputWidth = baseBgra != null ? baseWidth : cdgWidth;
        var outputHeight = baseBgra != null ? baseHeight : cdgHeight;
        var hasCdgOverlay = cdgArgb != null && cdgWidth > 0 && cdgHeight > 0;
        var shouldZoomStaticBaseForCdg = baseBgra != null && cdgLayeringActive && !baseBgra.AsSpan().IsEmpty;
        if (shouldZoomStaticBaseForCdg)
        {
            (outputWidth, outputHeight) = GetNativeOutputSize(outputWidth, outputHeight);
        }

        if (outputWidth <= 0 || outputHeight <= 0)
        {
            return;
        }

        var outputStride = checked(outputWidth * 4);
        var required = checked(outputStride * outputHeight);
        byte[] output;

        lock (_lock)
        {
            if (_nativeCompositeOutputBgra == null || _nativeCompositeOutputBgra.Length != required)
            {
                _nativeCompositeOutputBgra = new byte[required];
            }
            output = _nativeCompositeOutputBgra;
        }

        if (baseBgra != null)
        {
            if (shouldZoomStaticBaseForCdg)
            {
                CopyBgraFrameUniformToFill(baseBgra, baseWidth, baseHeight, baseStride, output, outputWidth, outputHeight, outputStride);
            }
            else
            {
                CopyBgraFrame(baseBgra, baseWidth, baseHeight, baseStride, output, outputWidth, outputHeight, outputStride);
            }
        }
        else
        {
            Array.Clear(output, 0, required);
        }

        if (cdgArgb != null && cdgWidth > 0 && cdgHeight > 0)
        {
            var scaledCdg = EnsureNativeCompositeScaledCdgOverlay(cdgArgb, cdgWidth, cdgHeight, outputWidth, outputHeight, outputStride);
            if (scaledCdg != null)
            {
                BlendPreScaledBgraOverBgra(scaledCdg, output, required);
            }
        }

        RenderVideoFrame(output.AsSpan(0, required), outputWidth, outputHeight, outputStride);
    }

    private byte[]? EnsureNativeCompositeScaledCdgOverlay(
        int[] sourceArgb,
        int sourceWidth,
        int sourceHeight,
        int destinationWidth,
        int destinationHeight,
        int destinationStride)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0 || destinationWidth <= 0 || destinationHeight <= 0)
        {
            return null;
        }

        var required = checked(destinationStride * destinationHeight);
        lock (_lock)
        {
            if (_nativeCompositeScaledCdgBgra != null
                && _nativeCompositeScaledCdgBgra.Length == required
                && _nativeCompositeScaledCdgWidth == destinationWidth
                && _nativeCompositeScaledCdgHeight == destinationHeight
                && _nativeCompositeScaledCdgStride == destinationStride
                && !_nativeCompositeScaledCdgDirty)
            {
                return _nativeCompositeScaledCdgBgra;
            }

            if (_nativeCompositeScaledCdgBgra == null || _nativeCompositeScaledCdgBgra.Length != required)
            {
                _nativeCompositeScaledCdgBgra = new byte[required];
            }

            Array.Clear(_nativeCompositeScaledCdgBgra, 0, required);
            var cdgRect = CalculateAspectFitRect(destinationWidth, destinationHeight, sourceWidth, sourceHeight);

            ScaleArgbToBgraOverlay(
                sourceArgb,
                sourceWidth,
                sourceHeight,
                _nativeCompositeScaledCdgBgra,
                destinationWidth,
                destinationHeight,
                destinationStride,
                cdgRect.x,
                cdgRect.y,
                cdgRect.width,
                cdgRect.height);

            _nativeCompositeScaledCdgWidth = destinationWidth;
            _nativeCompositeScaledCdgHeight = destinationHeight;
            _nativeCompositeScaledCdgStride = destinationStride;
            _nativeCompositeScaledCdgDirty = false;
            return _nativeCompositeScaledCdgBgra;
        }
    }

    private static void CopyBgraFrame(
        byte[] source,
        int sourceWidth,
        int sourceHeight,
        int sourceStride,
        byte[] destination,
        int destinationWidth,
        int destinationHeight,
        int destinationStride)
    {
        if (sourceWidth == destinationWidth && sourceHeight == destinationHeight && sourceStride == destinationStride)
        {
            Buffer.BlockCopy(source, 0, destination, 0, Math.Min(source.Length, destination.Length));
            return;
        }

        // Nearest-neighbor resize. This is only used for the temporary CPU compositor path.
        // D3D11 presentation still performs the final window scaling through the native swap-chain.
        for (var y = 0; y < destinationHeight; y++)
        {
            var sy = Math.Clamp((int)((long)y * sourceHeight / destinationHeight), 0, sourceHeight - 1);
            var sourceRow = sy * sourceStride;
            var destinationRow = y * destinationStride;
            for (var x = 0; x < destinationWidth; x++)
            {
                var sx = Math.Clamp((int)((long)x * sourceWidth / destinationWidth), 0, sourceWidth - 1);
                var si = sourceRow + sx * 4;
                var di = destinationRow + x * 4;
                destination[di] = source[si];
                destination[di + 1] = source[si + 1];
                destination[di + 2] = source[si + 2];
                destination[di + 3] = 255;
            }
        }
    }


    private static void CopyBgraFrameUniformToFill(
        byte[] source,
        int sourceWidth,
        int sourceHeight,
        int sourceStride,
        byte[] destination,
        int destinationWidth,
        int destinationHeight,
        int destinationStride)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0 || destinationWidth <= 0 || destinationHeight <= 0)
        {
            return;
        }

        var scale = Math.Max(
            destinationWidth / (double)sourceWidth,
            destinationHeight / (double)sourceHeight);

        var scaledWidth = Math.Max(1.0, sourceWidth * scale);
        var scaledHeight = Math.Max(1.0, sourceHeight * scale);
        var cropX = Math.Max(0.0, (scaledWidth - destinationWidth) / 2.0);
        var cropY = Math.Max(0.0, (scaledHeight - destinationHeight) / 2.0);

        for (var y = 0; y < destinationHeight; y++)
        {
            var sy = Math.Clamp((int)Math.Floor((y + cropY) / scale), 0, sourceHeight - 1);
            var sourceRow = sy * sourceStride;
            var destinationRow = y * destinationStride;
            for (var x = 0; x < destinationWidth; x++)
            {
                var sx = Math.Clamp((int)Math.Floor((x + cropX) / scale), 0, sourceWidth - 1);
                var si = sourceRow + sx * 4;
                var di = destinationRow + x * 4;
                destination[di] = source[si];
                destination[di + 1] = source[si + 1];
                destination[di + 2] = source[si + 2];
                destination[di + 3] = 255;
            }
        }
    }

    private static (int x, int y, int width, int height) CalculateAspectFitRect(
        int destinationWidth,
        int destinationHeight,
        int sourceWidth,
        int sourceHeight)
    {
        destinationWidth = Math.Max(1, destinationWidth);
        destinationHeight = Math.Max(1, destinationHeight);
        sourceWidth = Math.Max(1, sourceWidth);
        sourceHeight = Math.Max(1, sourceHeight);

        var scale = Math.Min(
            destinationWidth / (double)sourceWidth,
            destinationHeight / (double)sourceHeight);

        var width = Math.Max(1, (int)Math.Round(sourceWidth * scale));
        var height = Math.Max(1, (int)Math.Round(sourceHeight * scale));
        var x = Math.Max(0, (destinationWidth - width) / 2);
        var y = Math.Max(0, (destinationHeight - height) / 2);
        return (x, y, width, height);
    }

    private static void ScaleArgbToBgraOverlay(
        int[] sourceArgb,
        int sourceWidth,
        int sourceHeight,
        byte[] destinationBgra,
        int destinationWidth,
        int destinationHeight,
        int destinationStride,
        int destinationX,
        int destinationY,
        int scaledWidth,
        int scaledHeight)
    {
        if (sourceWidth <= 0
            || sourceHeight <= 0
            || destinationWidth <= 0
            || destinationHeight <= 0
            || scaledWidth <= 0
            || scaledHeight <= 0)
        {
            return;
        }

        destinationX = Math.Clamp(destinationX, 0, destinationWidth - 1);
        destinationY = Math.Clamp(destinationY, 0, destinationHeight - 1);
        scaledWidth = Math.Min(scaledWidth, destinationWidth - destinationX);
        scaledHeight = Math.Min(scaledHeight, destinationHeight - destinationY);

        for (var y = 0; y < scaledHeight; y++)
        {
            var sy = Math.Clamp((int)((long)y * sourceHeight / scaledHeight), 0, sourceHeight - 1);
            var destinationRow = (destinationY + y) * destinationStride;
            var sourceRow = sy * sourceWidth;
            for (var x = 0; x < scaledWidth; x++)
            {
                var sx = Math.Clamp((int)((long)x * sourceWidth / scaledWidth), 0, sourceWidth - 1);
                var argb = sourceArgb[sourceRow + sx];
                var di = destinationRow + (destinationX + x) * 4;

                destinationBgra[di] = (byte)(argb & 0xFF);
                destinationBgra[di + 1] = (byte)((argb >> 8) & 0xFF);
                destinationBgra[di + 2] = (byte)((argb >> 16) & 0xFF);
                destinationBgra[di + 3] = (byte)((argb >> 24) & 0xFF);
            }
        }
    }


    private static void ScaleArgbToPremultipliedBgraOverlay(
        int[] sourceArgb,
        int sourceWidth,
        int sourceHeight,
        byte[] destinationBgra,
        int destinationWidth,
        int destinationHeight,
        int destinationStride,
        int destinationX,
        int destinationY,
        int scaledWidth,
        int scaledHeight)
    {
        if (sourceWidth <= 0
            || sourceHeight <= 0
            || destinationWidth <= 0
            || destinationHeight <= 0
            || scaledWidth <= 0
            || scaledHeight <= 0)
        {
            return;
        }

        destinationX = Math.Clamp(destinationX, 0, destinationWidth - 1);
        destinationY = Math.Clamp(destinationY, 0, destinationHeight - 1);
        scaledWidth = Math.Min(scaledWidth, destinationWidth - destinationX);
        scaledHeight = Math.Min(scaledHeight, destinationHeight - destinationY);

        for (var y = 0; y < scaledHeight; y++)
        {
            var sy = Math.Clamp((int)((long)y * sourceHeight / scaledHeight), 0, sourceHeight - 1);
            var destinationRow = (destinationY + y) * destinationStride;
            var sourceRow = sy * sourceWidth;
            for (var x = 0; x < scaledWidth; x++)
            {
                var sx = Math.Clamp((int)((long)x * sourceWidth / scaledWidth), 0, sourceWidth - 1);
                var argb = sourceArgb[sourceRow + sx];
                var a = (byte)((argb >> 24) & 0xFF);
                var r = (byte)((argb >> 16) & 0xFF);
                var g = (byte)((argb >> 8) & 0xFF);
                var b = (byte)(argb & 0xFF);
                var di = destinationRow + (destinationX + x) * 4;

                // DirectComposition expects premultiplied alpha for the overlay swap-chain.
                destinationBgra[di] = (byte)((b * a + 127) / 255);
                destinationBgra[di + 1] = (byte)((g * a + 127) / 255);
                destinationBgra[di + 2] = (byte)((r * a + 127) / 255);
                destinationBgra[di + 3] = a;
            }
        }
    }


    private static void BlendPreScaledBgraOverBgra(byte[] overlayBgra, byte[] destinationBgra, int byteCount)
    {
        var limit = Math.Min(byteCount, Math.Min(overlayBgra.Length, destinationBgra.Length));
        for (var di = 0; di + 3 < limit; di += 4)
        {
            var a = overlayBgra[di + 3];
            if (a == 0)
            {
                continue;
            }

            if (a == 255)
            {
                destinationBgra[di] = overlayBgra[di];
                destinationBgra[di + 1] = overlayBgra[di + 1];
                destinationBgra[di + 2] = overlayBgra[di + 2];
                destinationBgra[di + 3] = 255;
                continue;
            }

            var invA = 255 - a;
            destinationBgra[di] = (byte)((overlayBgra[di] * a + destinationBgra[di] * invA + 127) / 255);
            destinationBgra[di + 1] = (byte)((overlayBgra[di + 1] * a + destinationBgra[di + 1] * invA + 127) / 255);
            destinationBgra[di + 2] = (byte)((overlayBgra[di + 2] * a + destinationBgra[di + 2] * invA + 127) / 255);
            destinationBgra[di + 3] = 255;
        }
    }

    private void ClearNativeCompositeStateUnsafe()
    {
        _nativeCompositeBaseBgra = null;
        _nativeCompositeBaseWidth = 0;
        _nativeCompositeBaseHeight = 0;
        _nativeCompositeBaseStride = 0;
        _nativeCompositeBaseIsVideo = false;
        _nativeCompositeBaseUseCdgLayering = false;
        _nativeCompositeCdgArgb = null;
        _nativeCompositeCdgWidth = 0;
        _nativeCompositeCdgHeight = 0;
        _nativeCompositeScaledCdgBgra = null;
        _nativeCompositeScaledCdgWidth = 0;
        _nativeCompositeScaledCdgHeight = 0;
        _nativeCompositeScaledCdgStride = 0;
        _nativeCompositeScaledCdgDirty = true;
        _nativeCompositeOutputBgra = null;
        _directCompositionCdgOverlayBgra = null;
        _directCompositionCdgOverlayWidth = 0;
        _directCompositionCdgOverlayHeight = 0;
        _directCompositionCdgOverlayStride = 0;
        _nativeCachedCdgOverlayDirty = true;
        _nativeDirectCompositionLayersRequested = false;
        Volatile.Write(ref _nativeCompositeDirty, 0);
        Volatile.Write(ref _nativeCompositeLastPresentTimestamp, 0);
    }

    private void FallbackToSoftwareRendering(ReadOnlySpan<byte> pixelData, int width, int height, int stride)
    {
        // Don't change the configured method, just render with CPU this time
        RenderWithCpuAsync(pixelData, width, height, stride);
    }

    /// <summary>
    /// High-performance in-place ARGB to BGRA conversion with buffer reuse.
    /// </summary>
    private unsafe void ConvertArgbToBgraInPlace(ReadOnlySpan<int> argbData, byte[] bgraBuffer)
    {
        fixed (int* argbPtr = argbData)
        fixed (byte* bgraPtr = bgraBuffer)
        {
            var argbInt = argbPtr;
            var bgraByte = bgraPtr;
            var length = argbData.Length;

            // Vectorized processing - 4 pixels at once
            var fullVectors = length / 4;
            var remainder = length % 4;

            // Process 4 ARGB pixels to 16 BGRA bytes
            for (int i = 0; i < fullVectors; i++)
            {
                var baseIndex = i * 16; // 4 pixels  4 bytes

                // Unroll loop for better performance
                var argb1 = *(argbInt++);
                bgraByte[baseIndex] = (byte)(argb1 & 0xFF);
                bgraByte[baseIndex + 1] = (byte)((argb1 >> 8) & 0xFF);
                bgraByte[baseIndex + 2] = (byte)((argb1 >> 16) & 0xFF);
                bgraByte[baseIndex + 3] = (byte)((argb1 >> 24) & 0xFF);

                var argb2 = *(argbInt++);
                bgraByte[baseIndex + 4] = (byte)(argb2 & 0xFF);
                bgraByte[baseIndex + 5] = (byte)((argb2 >> 8) & 0xFF);
                bgraByte[baseIndex + 6] = (byte)((argb2 >> 16) & 0xFF);
                bgraByte[baseIndex + 7] = (byte)((argb2 >> 24) & 0xFF);

                var argb3 = *(argbInt++);
                bgraByte[baseIndex + 8] = (byte)(argb3 & 0xFF);
                bgraByte[baseIndex + 9] = (byte)((argb3 >> 8) & 0xFF);
                bgraByte[baseIndex + 10] = (byte)((argb3 >> 16) & 0xFF);
                bgraByte[baseIndex + 11] = (byte)((argb3 >> 24) & 0xFF);

                var argb4 = *(argbInt++);
                bgraByte[baseIndex + 12] = (byte)(argb4 & 0xFF);
                bgraByte[baseIndex + 13] = (byte)((argb4 >> 8) & 0xFF);
                bgraByte[baseIndex + 14] = (byte)((argb4 >> 16) & 0xFF);
                bgraByte[baseIndex + 15] = (byte)((argb4 >> 24) & 0xFF);
            }

            // Handle remaining pixels
            var remainingBase = fullVectors * 16;
            for (int i = 0; i < remainder; i++)
            {
                var argb = *(argbInt++);
                var baseIndex = remainingBase + (i * 4);

                bgraByte[baseIndex] = (byte)(argb & 0xFF);
                bgraByte[baseIndex + 1] = (byte)((argb >> 8) & 0xFF);
                bgraByte[baseIndex + 2] = (byte)((argb >> 16) & 0xFF);
                bgraByte[baseIndex + 3] = (byte)((argb >> 24) & 0xFF);
            }
        }
    }

    #endregion

    #region Resource Management

    private void CleanupCurrentMethod()
    {
        // Retire the current rendering surface before releasing it. Any queued CPU/CDG/D3D work
        // captured against the previous surface generation will no-op when it reaches the dispatcher.
        Interlocked.Increment(ref _resourceGeneration);

        // Clean up D3D resources
        _d3d11Renderer?.FramePresented -= OnD3D11FramePresented;
        _d3d11Renderer?.Dispose();
        _d3d11Renderer = null;

        // Clean up CPU resources
        if (_bitmap != null)
        {
            try
            {
                _bitmap.Freeze(); // Thread-safe cleanup
                _bitmap = null;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[OptimizedRenderer] Cleanup error: {ex.Message}");
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;

        Interlocked.Increment(ref _resourceGeneration);
        lock (_lock)
        {
            _disposed = true;

            // Use BeginInvoke for non-blocking cleanup
            _dispatcher.BeginInvoke(() =>
            {
                CleanupCurrentMethod();
            }, DispatcherPriority.Background);
        }
    }

    #endregion
}
