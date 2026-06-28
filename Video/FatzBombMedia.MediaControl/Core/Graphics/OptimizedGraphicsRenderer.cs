using FatzBombMedia.MediaControl.Core.Configuration;
using FatzBombMedia.MediaControl.Core.DxCdg;
using FatzBombMedia.MediaControl.Core.Abstractions;
using FatzBombMedia.MediaControl.Core.Graphics.Composition;
using FatzBombMedia.MediaControl.Core.Graphics.D3D;
using FatzBombMedia.MediaControl.Core.Graphics.Layers;
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
    private readonly OptimizedPresentationRouter _presentationRouter = new(configuration);
    private readonly Dispatcher _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
    private int _resourceGeneration;
    private IntPtr _nativePresentationHwnd;
    private Stretch _nativePresentationStretch = Stretch.Uniform;
    private int _nativeResizeInProgress;

    // Native base state remains only for non-CDG native static presentation. When a CDG
    // overlay session is active, Layer 0 is retained separately and consumed only by
    // the media compositor so video/image/background cannot present through a competing path.
    private readonly NativeCompositeBaseState _nativeCompositeBase = new();
    private byte[]? _compositionBackgroundBgra;
    private int _compositionBackgroundWidth;
    private int _compositionBackgroundHeight;
    private int _compositionBackgroundStride;
    private bool _compositionBackgroundForceZoomToFill;
    private long _compositionBackgroundRevision;
    private readonly WashLayer _washLayer = new();
    private readonly CpuWriteableBitmapRenderer _cpuRenderer = new();
    private bool _nativeDirectCompositionLayersRequested;

    // CDG packet updates can arrive much faster than the display cadence.  When the base layer is
    // a looping background video, the compositor is driven by the background video's frame cadence;
    // CDG updates only refresh the retained CDG texture/buffer and are picked up on the next video
    // present. Static CDG/image invalidations are coalesced by the scheduler.
    private readonly NativeCompositePresentationScheduler _nativeCompositePresentationScheduler = new();

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
                ResetPresentationSizeTracking();
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
        if (wasResizing && !isResizing && _nativeCompositePresentationScheduler.IsDirty)
        {
            RequestNativeCompositePresent(forceImmediate: true);
        }

    }

    public void SetNativePresentationStretch(Stretch stretch)
    {
        if (_disposed) return;

        var changed = _nativePresentationStretch != stretch;
        _nativePresentationStretch = stretch;
        _d3d11Renderer?.SetNativePresentationStretch(stretch);

        if (changed && HasNativeStaticCompositeContent())
        {
            // Standard still images and CDG+image composites are retained static native
            // composites.  Unlike video, they do not receive another frame after the
            // display mode changes, so re-present immediately instead of waiting for a
            // resize/other invalidation to apply Original/Zoom/Stretch/ZoomToFill.
            RequestNativeCompositePresent(forceImmediate: true, allowDuringResize: true);
        }
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

        // Keep native video presentation size live during resize. The old optimization
        // froze this value while the user dragged the window, which made USER32/DWM
        // stretch the last completed swap-chain frame to the transient window shape.
        // That preserved throughput, but it visibly changed the aspect ratio for Zoom,
        // Original, and ZoomToFill modes. Static/CDG compositor work is still throttled
        // elsewhere; viewport size itself must always reflect the actual host size.
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

            // Do not block viewport-size updates during live resize. Video frames are
            // allowed to resize/re-map at frame cadence so aspect policy remains stable.
            // CDG/static composite throttling is controlled by the native-resize flag in
            // the composite paths, not by freezing the viewport dimensions.
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

            // Static native composites have no video frame cadence to naturally redraw
            // when only the viewport changes. Keep their full-viewport/ZoomToFill math
            // live during manual resize by re-presenting the retained composite at the
            // latest native size. Video-driven composites still present on video frames.
            if (IsNativePresentationRequested && HasNativeStaticCompositeContent())
            {
                RequestNativeCompositePresent(forceImmediate: true, allowDuringResize: true);
            }
        }
        finally
        {
            Interlocked.Exchange(ref _nativePresentationSizeUpdateQueued, 0);

            // If a newer size arrived while we were applying the previous one, queue exactly one
            // more render-priority update for the final/latest value.
            if (!_disposed
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
        if (_disposed || _nativePresentationHwnd == IntPtr.Zero)
        {
            return;
        }

        // CDG-only and CDG+image backgrounds do not have a video cadence to naturally
        // repaint after resize. Re-present the retained static composite explicitly.
        // This is allowed during live resize so static background images keep their
        // ZoomToFill mapping instead of letting USER32/DWM stretch an old frame.
        if (HasNativeStaticCompositeContent())
        {
            RequestNativeCompositePresent(forceImmediate: true, allowDuringResize: true);
            return;
        }

        if (ShouldFreezeNativePresentationDuringResize())
        {
            return;
        }

        _d3d11Renderer?.RefreshNativePresentation();
    }

    private bool HasNativeStaticCompositeContent()
    {
        lock (_lock)
        {
            return !_nativeCompositeBase.UseCdgLayering
                && _nativeCompositeBase.HasStaticCompositeContent(hasCdgFrame: false);
        }
    }

    private bool IsDxCdgLayeringActive
    {
        get
        {
            lock (_lock)
            {
                return _nativeCompositeBase.UseCdgLayering;
            }
        }
    }

    public bool IsNativePresentationRequested => _nativePresentationHwnd != IntPtr.Zero;

    /// <summary>
    /// Tells the native compositor whether the current session has a CDG layer.
    /// This is session state, not frame state: background media must keep using
    /// CDG layer rules even between CDG packet invalidations or before the first
    /// DXCDG snapshot has been presented.
    /// </summary>
    public void SetNativeCompositeCdgLayeringActive(bool active)
    {
        var changed = false;
        lock (_lock)
        {
            changed = _nativeCompositeBase.SetCdgLayeringActive(active);
        }

        if (changed && IsNativePresentationRequested)
        {
            if (active)
            {
                // DXCDG now owns the visible CDG presentation target. Do not let the
                // retired native composite scheduler present a static/image background
                // over the composition frame when a CDG layer becomes active.
                _nativeCompositePresentationScheduler.Clear();
            }
            else
            {
                RequestNativeCompositePresent(forceImmediate: true);
            }
        }
    }

    public void SetCompositionLayer0Frame(BitmapSource? source, bool forceZoomToFill = true)
    {
        if (_disposed || source == null)
        {
            ClearCompositionLayer0Frame();
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
                ClearCompositionLayer0Frame();
                return;
            }

            var stride = checked(width * 4);
            var buffer = new byte[checked(stride * height)];
            bgraSource.CopyPixels(buffer, stride, 0);
            SetCompositionLayer0Frame(buffer, width, height, stride, forceZoomToFill);
        }
        catch (Exception ex)
        {
            if (_configuration.EnableLogging)
            {
                Debug.WriteLine($"[DXCDG-TRACE] Layer 0 image conversion failed: {ex.GetType().Name}: {ex.Message}");
            }
            ClearCompositionLayer0Frame();
        }
    }

    public void SetCompositionLayer0Frame(ReadOnlySpan<byte> bgraPixels, int width, int height, int stride, bool forceZoomToFill = true)
    {
        if (_disposed || bgraPixels.IsEmpty || width <= 0 || height <= 0 || stride < width * 4)
        {
            ClearCompositionLayer0Frame();
            return;
        }

        var required = checked(stride * height);
        if (bgraPixels.Length < required)
        {
            ClearCompositionLayer0Frame();
            return;
        }

        lock (_lock)
        {
            if (_compositionBackgroundBgra == null || _compositionBackgroundBgra.Length != required)
            {
                _compositionBackgroundBgra = new byte[required];
            }

            bgraPixels.Slice(0, required).CopyTo(_compositionBackgroundBgra);
            _compositionBackgroundWidth = width;
            _compositionBackgroundHeight = height;
            _compositionBackgroundStride = stride;
            _compositionBackgroundForceZoomToFill = forceZoomToFill;
            _compositionBackgroundRevision++;
        }
    }

    public void ClearCompositionLayer0Frame()
    {
        lock (_lock)
        {
            _compositionBackgroundBgra = null;
            _compositionBackgroundWidth = 0;
            _compositionBackgroundHeight = 0;
            _compositionBackgroundStride = 0;
            _compositionBackgroundForceZoomToFill = false;
            _compositionBackgroundRevision++;
        }

        _d3d11Renderer?.ClearCompositionBackgroundFrame();
    }

    public void SetCompositionBackgroundFrame(BitmapSource? source, bool forceZoomToFill = false)
        => SetCompositionLayer0Frame(source, forceZoomToFill);

    public void SetCompositionBackgroundFrame(ReadOnlySpan<byte> bgraPixels, int width, int height, int stride, bool forceZoomToFill = false)
        => SetCompositionLayer0Frame(bgraPixels, width, height, stride, forceZoomToFill);

    public void ClearCompositionBackgroundFrame() => ClearCompositionLayer0Frame();

    public void PresentEmptyComposition(MediaCompositionClearMode clearMode = MediaCompositionClearMode.Black, uint clearArgb = 0xFF000000, string reason = "empty")
        => PresentMediaComposition(MediaCompositionFrame.Empty(clearMode, clearArgb, reason));

    public void PresentBackgroundComposition(bool forceZoomToFill = false, string reason = "background")
        => PresentMediaComposition(BuildCompositionFrame(null, 0, retainedRefresh: false, reason, forceZoomToFill));

    /// <summary>
    /// Sets the base visual for native composition from a WPF BitmapSource. This is used for
    /// still images and CDG background images while native HWND presentation is active. The pixels
    /// are copied once to BGRA and then uploaded/composited through D3D11, not shown through WPF.
    /// </summary>
    public void SetNativeCompositeBase(BitmapSource? source, bool forceZoomToFill = false)
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
            SetNativeCompositeBase(buffer, width, height, stride, forceZoomToFill);
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
    public void SetNativeCompositeBase(ReadOnlySpan<byte> bgraPixels, int width, int height, int stride, bool forceZoomToFill = false)
        => SetNativeCompositeBaseCore(bgraPixels, width, height, stride, isVideoBase: false, forceZoomToFill: forceZoomToFill);

    /// <summary>
    /// Sets the looping background-video base layer.  CDG updates are retained separately and are
    /// blended on the next background-video present instead of forcing their own full-frame present.
    /// </summary>
    public void SetNativeCompositeVideoBase(ReadOnlySpan<byte> bgraPixels, int width, int height, int stride, bool forceZoomToFill = false)
        => SetNativeCompositeBaseCore(bgraPixels, width, height, stride, isVideoBase: true, forceZoomToFill: forceZoomToFill);

    private void SetNativeCompositeBaseCore(ReadOnlySpan<byte> bgraPixels, int width, int height, int stride, bool isVideoBase, bool forceZoomToFill)
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
            _nativeDirectCompositionLayersRequested = false;
            var dxCdgLayeringActive = false;
            lock (_lock)
            {
                dxCdgLayeringActive = _nativeCompositeBase.UseCdgLayering;
                if (dxCdgLayeringActive)
                {
                    // While CDG is active, background video is retained as the DXCDG
                    // background layer. It must not present through the old video/native
                    // upload path because that competes with the DXCDG target and flickers.
                    _nativeCompositeBase.SetFallbackVideoPixels(bgraPixels.Slice(0, required), width, height, stride, required);
                }
                else
                {
                    _nativeCompositeBase.UpdateVideoMetadata(width, height, stride, forceZoomToFill);
                }
            }

            if (dxCdgLayeringActive)
            {
                _nativeCompositePresentationScheduler.Clear();
                return;
            }

            RenderNativeGpuCompositedVideoFrame(bgraPixels.Slice(0, required), width, height, stride);
            return;
        }

        var cdgLayeringActive = false;
        lock (_nativeCompositeRenderLock)
        {
            lock (_lock)
            {
                cdgLayeringActive = _nativeCompositeBase.UseCdgLayering;
                if (_nativeCompositeBase.SetStaticPixels(bgraPixels, width, height, stride, required, forceZoomToFill))
                {
                }
            }
        }

        if (cdgLayeringActive)
        {
            // The static image/background is now an input to the media compositor,
            // not a standalone native composite present. Presenting it here would
            // overwrite the DXCDG surface and cause image flashes.
            SetCompositionLayer0Frame(bgraPixels.Slice(0, required), width, height, stride, forceZoomToFill);
            _nativeCompositePresentationScheduler.Clear();
            return;
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
                _nativeCompositeBase.ClearBase();
            }
        }

        _d3d11Renderer?.ClearCompositionBackgroundFrame();
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
                    var (desiredWidth, desiredHeight) = _presentationRouter.GetD3DImagePresentationSize(_requestedWpfPresentationWidth, _requestedWpfPresentationHeight, _lastWidth, _lastHeight);
                    if (desiredWidth != _lastPresentationWidth || desiredHeight != _lastPresentationHeight)
                    {
                        CleanupCurrentMethod();
                        ResetPresentationSizeTracking();
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

    private void RetireCurrentSurfaceGeneration()
        => Interlocked.Increment(ref _resourceGeneration);

    private void ResetPresentationSizeTracking()
    {
        _lastWidth = 0;
        _lastHeight = 0;
        _lastPresentationWidth = 0;
        _lastPresentationHeight = 0;
    }

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

        if (IsDxCdgLayeringActive)
        {
            if (_configuration.EnableVerboseLogging)
            {
                Debug.WriteLine($"[DXCDG-TRACE] Suppressed direct video frame present while DXCDG owns the target ({frame.Width}x{frame.Height}, hw={frame.IsD3D11HardwareFrame}).");
            }
            return;
        }

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

        if (IsDxCdgLayeringActive)
        {
            if (_configuration.EnableVerboseLogging)
            {
                Debug.WriteLine($"[DXCDG-TRACE] Suppressed direct BGRA frame present while DXCDG owns the target ({width}x{height}).");
            }
            return;
        }

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
    /// Presents the current DXCDG indexed/palette snapshot through the D3D11 media compositor.
    /// This is now the only active CDG visual path; legacy bitmap CDG code is quarantined.
    /// </summary>
    public void RenderDxCdgSnapshot(DxCdgRenderSnapshot snapshot, bool retainedRefresh = false)
    {
        if (_disposed || snapshot.ColorIndices.IsEmpty || snapshot.PaletteArgb.IsEmpty)
        {
            return;
        }

        PresentMediaComposition(BuildCompositionFrame(
            snapshot,
            snapshot.BackgroundWashArgb,
            retainedRefresh,
            retainedRefresh ? "retained-dxcdg" : "dxcdg",
            forceBackgroundZoomToFill: true));
    }

    public void PresentMediaComposition(MediaCompositionFrame frame)
    {
        if (_disposed)
        {
            return;
        }

        var (sourceWidth, sourceHeight) = GetCompositionSourceSize(frame);
        if (!EnsureD3D11ForComposition(sourceWidth, sourceHeight))
        {
            Debug.WriteLine($"[COMPOSITION-TRACE] OptimizedRenderer skipped reason={frame.Reason}: D3D11 renderer could not be initialized.");
            return;
        }

        var renderer = _d3d11Renderer;
        if (renderer == null || !renderer.IsReady)
        {
            Debug.WriteLine($"[COMPOSITION-TRACE] OptimizedRenderer skipped reason={frame.Reason}: D3D11 renderer is null/not ready.");
            return;
        }

        var (outputWidth, outputHeight) = GetCompositionOutputSize(frame, sourceWidth, sourceHeight);
        if (!frame.RetainedRefresh)
        {
            Debug.WriteLine(
                $"[COMPOSITION-TRACE] OptimizedRenderer present request reason={frame.Reason} " +
                $"source={sourceWidth}x{sourceHeight} output={outputWidth}x{outputHeight} backend={renderer.PresentationBackend} " +
                $"background={frame.HasBackground} wash={frame.HasWash} overlay={frame.HasCdgOverlay}");
        }

        if (!renderer.PresentMediaCompositionVisible(frame, outputWidth, outputHeight))
        {
            Debug.WriteLine($"[COMPOSITION-TRACE] OptimizedRenderer present failed reason={frame.Reason}.");
            return;
        }

        if (!frame.RetainedRefresh)
        {
            Debug.WriteLine($"[COMPOSITION-TRACE] OptimizedRenderer present succeeded reason={frame.Reason}.");
        }
    }

    private MediaCompositionFrame BuildCompositionFrame(
        DxCdgRenderSnapshot? cdgOverlay,
        uint washArgb,
        bool retainedRefresh,
        string reason,
        bool forceBackgroundZoomToFill)
    {
        byte[]? backgroundBgra;
        int backgroundWidth;
        int backgroundHeight;
        int backgroundStride;
        bool backgroundUniformToFill;
        long backgroundRevision;

        lock (_lock)
        {
            backgroundBgra = _compositionBackgroundBgra;
            backgroundWidth = _compositionBackgroundWidth;
            backgroundHeight = _compositionBackgroundHeight;
            backgroundStride = _compositionBackgroundStride;
            backgroundUniformToFill = forceBackgroundZoomToFill || _compositionBackgroundForceZoomToFill;
            backgroundRevision = _compositionBackgroundRevision;
        }

        var hasBackground = backgroundBgra != null
            && backgroundWidth > 0
            && backgroundHeight > 0
            && backgroundStride >= backgroundWidth * 4;

        return new MediaCompositionFrame
        {
            ClearMode = MediaCompositionClearMode.Black,
            ClearArgb = 0xFF000000,
            BackgroundBgra = hasBackground ? backgroundBgra! : ReadOnlyMemory<byte>.Empty,
            BackgroundWidth = hasBackground ? backgroundWidth : 0,
            BackgroundHeight = hasBackground ? backgroundHeight : 0,
            BackgroundStride = hasBackground ? backgroundStride : 0,
            BackgroundUniformToFill = hasBackground && backgroundUniformToFill,
            BackgroundRevision = hasBackground ? backgroundRevision : -1,
            WashArgb = washArgb,
            CdgOverlay = cdgOverlay,
            RetainedRefresh = retainedRefresh,
            Reason = reason
        };
    }

    private static (int Width, int Height) GetCompositionSourceSize(MediaCompositionFrame frame)
    {
        if (frame.CdgOverlay != null)
        {
            return (Math.Max(1, frame.CdgOverlay.Width), Math.Max(1, frame.CdgOverlay.Height));
        }

        if (frame.HasBackground)
        {
            return (Math.Max(1, frame.BackgroundWidth), Math.Max(1, frame.BackgroundHeight));
        }

        return (1, 1);
    }

    private (int Width, int Height) GetCompositionOutputSize(MediaCompositionFrame frame, int sourceWidth, int sourceHeight)
    {
        lock (_lock)
        {
            if (_lastPresentationWidth > 0 && _lastPresentationHeight > 0)
            {
                return (_lastPresentationWidth, _lastPresentationHeight);
            }

            if (_requestedNativePresentationWidth > 0 && _requestedNativePresentationHeight > 0)
            {
                return (_requestedNativePresentationWidth, _requestedNativePresentationHeight);
            }

            if (_requestedWpfPresentationWidth > 0 && _requestedWpfPresentationHeight > 0)
            {
                return (_requestedWpfPresentationWidth, _requestedWpfPresentationHeight);
            }
        }

        return (Math.Max(1, sourceWidth), Math.Max(1, sourceHeight));
    }


    public void SetCdgBackgroundWash(uint washArgb)
    {
        var changed = false;
        lock (_lock)
        {
            changed = _washLayer.SetWash(washArgb);
        }

        if (changed && IsNativePresentationRequested && HasNativeStaticCompositeContent())
        {
            RequestNativeCompositePresent(forceImmediate: true);
        }

        if (changed && _configuration.EnableVerboseLogging)
        {
            Debug.WriteLine($"[OptimizedRenderer] CDG wash updated: 0x{washArgb:X8}");
        }
    }

    private (int Width, int Height) GetDxCdgOutputSize(DxCdgRenderSnapshot snapshot)
    {
        lock (_lock)
        {
            if (_lastPresentationWidth > 0 && _lastPresentationHeight > 0)
            {
                return (_lastPresentationWidth, _lastPresentationHeight);
            }

            if (_requestedNativePresentationWidth > 0 && _requestedNativePresentationHeight > 0)
            {
                return (_requestedNativePresentationWidth, _requestedNativePresentationHeight);
            }

            if (_requestedWpfPresentationWidth > 0 && _requestedWpfPresentationHeight > 0)
            {
                return (_requestedWpfPresentationWidth, _requestedWpfPresentationHeight);
            }
        }

        return (snapshot.Width, snapshot.Height);
    }

    /// <summary>
    /// Clears all content flags - call when switching media.
    /// </summary>
    public void ClearAllContent()
    {
        RetireCurrentSurfaceGeneration();
        lock (_lock)
        {
            _hasActiveVideo = false;
            ClearNativeCompositeStateUnsafe();
        }

        RunOnDispatcher(() =>
        {
            lock (_lock)
            {
                // Native swap-chain and DirectComposition targets can visually retain the
                // last presented frame after their resources are released.  Explicitly present
                // a black frame before cleanup when switching to audio-only/no-visual media.
                _d3d11Renderer?.ClearPresentedFrame();
                CleanupCurrentMethod();
                ResetPresentationSizeTracking();
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
                    ResetPresentationSizeTracking();
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

    private bool EnsureD3D11ForComposition(int width, int height)
    {
        if (_disposed || width <= 0 || height <= 0)
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
                ResetPresentationSizeTracking();
                return false;
            }

            _lastWidth = width;
            _lastHeight = height;
            (_lastPresentationWidth, _lastPresentationHeight) = GetPresentationSizeForActiveTarget(width, height);
            ImageSourceChanged?.Invoke(this, ImageSource);
            return _d3d11Renderer?.IsReady == true;
        }
        catch (Exception ex)
        {
            if (_configuration.EnableLogging)
            {
                Debug.WriteLine($"[OptimizedRenderer] Failed to initialize D3D11 for DXCDG: {ex.Message}");
            }

            ResetPresentationSizeTracking();
            return false;
        }
    }

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
        return _presentationRouter.CreateActiveD3D11Surfaces(
            renderer,
            _nativePresentationHwnd,
            _nativeDirectCompositionLayersRequested,
            _requestedNativePresentationWidth,
            _requestedNativePresentationHeight,
            _requestedWpfPresentationWidth,
            _requestedWpfPresentationHeight,
            width,
            height);
    }

    private bool PresentationSurfaceChanged(int sourceWidth, int sourceHeight)
    {
        return _presentationRouter.PresentationSurfaceChanged(
            _d3d11Renderer,
            _nativePresentationHwnd,
            _nativeDirectCompositionLayersRequested,
            _requestedNativePresentationWidth,
            _requestedNativePresentationHeight,
            _requestedWpfPresentationWidth,
            _requestedWpfPresentationHeight,
            _lastPresentationWidth,
            _lastPresentationHeight,
            sourceWidth,
            sourceHeight);
    }

    private (int width, int height) GetNativeOutputSize(int sourceWidth, int sourceHeight)
    {
        return _presentationRouter.GetNativeOutputSize(
            _requestedNativePresentationWidth,
            _requestedNativePresentationHeight,
            sourceWidth,
            sourceHeight);
    }

    private (int width, int height) GetPresentationSizeForActiveTarget(int sourceWidth, int sourceHeight)
    {
        return _presentationRouter.GetPresentationSizeForActiveTarget(
            _nativePresentationHwnd,
            _nativeDirectCompositionLayersRequested,
            _requestedNativePresentationWidth,
            _requestedNativePresentationHeight,
            _requestedWpfPresentationWidth,
            _requestedWpfPresentationHeight,
            sourceWidth,
            sourceHeight);
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
                ResetPresentationSizeTracking();
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
        _bitmap = _cpuRenderer.CreateBitmap(width, height);
        return _bitmap != null;
    }

    private void RenderWithCpuAsync(ReadOnlySpan<byte> pixelData, int width, int height, int stride)
    {
        var bitmap = _bitmap;
        if (bitmap == null) return;

        _cpuRenderer.RenderBgraAsync(
            _dispatcher,
            bitmap,
            CurrentResourceGeneration,
            pixelData,
            width,
            height,
            stride,
            IsCurrentCpuSurface,
            RaiseFramePresented);
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
                    ResetPresentationSizeTracking();
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

    private void PresentNativeCompositeVideoFallback(ReadOnlySpan<byte> pixelData, int width, int height, int stride)
    {
        if (_disposed || pixelData.IsEmpty || width <= 0 || height <= 0 || stride < width * 4)
        {
            return;
        }

        _d3d11Renderer?.UploadFrame(pixelData, width, height, stride);
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

        _d3d11Renderer?.UploadFrame(pixelData, width, height, stride);
    }

    private void RequestNativeCompositePresent(bool forceImmediate = false, bool allowDuringResize = false)
    {
        if (IsDxCdgLayeringActive)
        {
            // DXCDG owns the visible target while a CDG layer is active. Any queued native
            // composite/base present would race the media compositor on the same swap-chain.
            _nativeCompositePresentationScheduler.Clear();
            return;
        }

        _nativeCompositePresentationScheduler.RequestPresent(
            forceImmediate,
            allowDuringResize,
            () => _disposed,
            () => IsNativePresentationRequested,
            ShouldFreezeNativePresentationDuringResize,
            PresentNativeComposite);
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
        if (IsDxCdgLayeringActive)
        {
            _nativeCompositePresentationScheduler.Clear();
            return;
        }

        byte[]? baseBgra;
        int baseWidth;
        int baseHeight;
        int baseStride;

        lock (_lock)
        {
            baseBgra = _nativeCompositeBase.Bgra;
            baseWidth = _nativeCompositeBase.Width;
            baseHeight = _nativeCompositeBase.Height;
            baseStride = _nativeCompositeBase.Stride;
        }

        if (baseBgra == null || baseWidth <= 0 || baseHeight <= 0 || baseStride < baseWidth * 4)
        {
            return;
        }

        RenderVideoFrame(baseBgra.AsSpan(), baseWidth, baseHeight, baseStride);
    }

    private void ClearNativeCompositeStateUnsafe()
    {
        _nativeCompositeBase.ClearAll();
        _compositionBackgroundBgra = null;
        _compositionBackgroundWidth = 0;
        _compositionBackgroundHeight = 0;
        _compositionBackgroundStride = 0;
        _compositionBackgroundForceZoomToFill = false;
        _compositionBackgroundRevision++;
        _washLayer.Clear();
        _nativeDirectCompositionLayersRequested = false;
        _nativeCompositePresentationScheduler.Clear();
    }

    private void FallbackToSoftwareRendering(ReadOnlySpan<byte> pixelData, int width, int height, int stride)
    {
        // Don't change the configured method, just render with CPU this time
        RenderWithCpuAsync(pixelData, width, height, stride);
    }

    #endregion

    #region Resource Management

    private void CleanupCurrentMethod()
    {
        // Retire the current rendering surface before releasing it. Any queued CPU/CDG/D3D work
        // captured against the previous surface generation will no-op when it reaches the dispatcher.
        RetireCurrentSurfaceGeneration();

        // Clean up D3D resources
        _d3d11Renderer?.FramePresented -= OnD3D11FramePresented;
        _d3d11Renderer?.Dispose();
        _d3d11Renderer = null;

        // Clean up CPU resources
        _cpuRenderer.Release(ref _bitmap);
    }


    public void Dispose()
    {
        if (_disposed) return;

        RetireCurrentSurfaceGeneration();
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
