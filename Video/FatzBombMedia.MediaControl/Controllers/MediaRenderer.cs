using System;
using System.Buffers;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows;
using FatzBombMedia.MediaControl.Core.Abstractions;
using FatzBombMedia.MediaControl.Core.Graphics;
using FatzBombMedia.MediaControl.Core.Configuration;
using FatzBombMedia.MediaControl.Pipeline;
using FatzBombMedia.MediaControl.Controllers.Layering;
using System.IO;

namespace FatzBombMedia.MediaControl.Controllers;

/// <summary>
/// Provides rendered media output for UI binding using natural window refresh.
/// Handles both video frames and CDG karaoke graphics with automatic timing sync.
/// Uses WPF's CompositionTarget.Rendering for optimal performance with GPU acceleration.
/// </summary>
public sealed class MediaRenderer : IDisposable
{
    private readonly IMediaEngine _mediaEngine;
    private readonly OptimizedGraphicsRenderer _graphicsRenderer;
    private readonly BackgroundMediaRenderer _backgroundMediaRenderer;
    private readonly RenderPipeline _renderPipeline;
    private readonly MediaPlayerConfiguration _configuration;
    private readonly bool _enableLogging;
    private readonly bool _enableVerboseLogging;
    private IVideoEngine? _attachedVideo;
    private ICdgEngine? _attachedCdg;
    private ImageSource? _stillImageSource;
    private bool _nativeCompositeBaseIsBackgroundVideo;
    private MediaLayerState _layerState = MediaLayerState.Empty;

    // Natural window refresh rendering
    private bool _isRenderingActive;
    private int _lastCdgPresentLogSecond = -1;
    private bool _nativePresentationRequested;
    private bool _wpfCompositionDrivenVideo;

    private bool _disposed;

    internal MediaRenderer(IMediaEngine mediaEngine)
        : this(mediaEngine, MediaPlayerConfiguration.Auto)
    {
    }

    internal MediaRenderer(IMediaEngine mediaEngine, MediaPlayerConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(mediaEngine);
        _mediaEngine = mediaEngine;
        _graphicsRenderer = new OptimizedGraphicsRenderer(configuration);
        _configuration = configuration;
        _backgroundMediaRenderer = new BackgroundMediaRenderer(configuration);
        _renderPipeline = new RenderPipeline(_mediaEngine);
        _enableLogging = configuration.EnableLogging;
        _enableVerboseLogging = configuration.EnableVerboseLogging;

        // Forward graphics renderer events
        _graphicsRenderer.ImageSourceChanged += (s, e) => ImageSourceChanged?.Invoke(this, EventArgs.Empty);
        _graphicsRenderer.FramePresented += (s, e) => FramePresented?.Invoke(this, EventArgs.Empty);
        _backgroundMediaRenderer.BackgroundChanged += OnBackgroundMediaChanged;
        _backgroundMediaRenderer.FramePresented += (_, __) => FramePresented?.Invoke(this, EventArgs.Empty);
        _backgroundMediaRenderer.VideoFrameReady += OnBackgroundVideoFrameReady;

        AttachVideoIfAvailable();
        AttachCdgIfAvailable();
        AttachImageIfAvailable();
    }

    /// <summary>
    /// Gets the ImageSource for binding to WPF Image controls.
    /// This will show video frames, CDG graphics, or be null for audio-only content.
    /// Priority: Video > CDG > null
    /// </summary>
    public ImageSource? ImageSource => _stillImageSource ?? _graphicsRenderer.ImageSource;

    private ImageSource? _backgroundSource;
    private Color _backgroundColor = Colors.Transparent;
    private Stretch _backgroundStretch = Stretch.Uniform;
    private VideoDisplayMode _videoDisplayMode = VideoDisplayMode.Zoom;
    private bool _backgroundAutoDetect = true;
    private bool _backgroundVisibleWhenStopped = true;

    /// <summary>
    /// Gets or sets the optional host-rendered background image shown behind the media surface.
    /// </summary>
    public ImageSource? BackgroundSource
    {
        get => _backgroundSource;
        set
        {
            if (ReferenceEquals(_backgroundSource, value)) return;

            _backgroundSource = value;
            UpdateLayerState("manual background source changed");
            RefreshNativeCompositeBaseLayer();
            BackgroundChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Gets or sets the background color shown behind media and background images.
    /// </summary>
    public Color BackgroundColor
    {
        get => _backgroundColor;
        set
        {
            if (_backgroundColor == value) return;

            _backgroundColor = value;
            BackgroundChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Gets or sets how the background image should fill the host background surface.
    /// </summary>
    public Stretch BackgroundStretch
    {
        get => _backgroundStretch;
        set
        {
            if (_backgroundStretch == value) return;

            _backgroundStretch = value;
            BackgroundChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Gets or sets how primary video content is displayed inside the viewport.
    /// Defaults to <see cref="VideoDisplayMode.Zoom"/>, matching the previous video-only behavior.
    /// Background media behind overlays ignores this setting and always uses zoom-to-fill.
    /// </summary>
    public VideoDisplayMode VideoDisplayMode
    {
        get => _videoDisplayMode;
        set
        {
            if (_videoDisplayMode == value) return;

            _videoDisplayMode = value;
            _graphicsRenderer.SetNativePresentationStretch(VideoDisplayModeMapper.ToStretch(_videoDisplayMode));
            VideoDisplayModeChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Gets or sets whether karaoke backgrounds should be detected from sidecar or archive media.
    /// </summary>
    public bool BackgroundAutoDetect
    {
        get => _backgroundAutoDetect;
        set
        {
            if (_backgroundAutoDetect == value) return;

            _backgroundAutoDetect = value;
            if (!_backgroundAutoDetect)
            {
                ClearAutoBackground();
            }
        }
    }

    /// <summary>
    /// Gets or sets whether the last background video frame remains visible after rendering stops.
    /// When false, the background video frame is hidden and decoding remains stopped.
    /// </summary>
    public bool BackgroundVisibleWhenStopped
    {
        get => _backgroundVisibleWhenStopped;
        set
        {
            if (_backgroundVisibleWhenStopped == value) return;

            _backgroundVisibleWhenStopped = value;
            if (!_isRenderingActive)
            {
                _backgroundMediaRenderer.Stop(clearFrame: !_backgroundVisibleWhenStopped);
            }
        }
    }

    /// <summary>
    /// Gets the currently displayed background image source after auto-detection is applied.
    /// </summary>
    public ImageSource? EffectiveBackgroundSource =>
        _backgroundMediaRenderer.ImageSource ?? _backgroundSource;

    /// <summary>
    /// Gets whether a CDG layer is currently attached. Background media uses
    /// CDG layering rules when this is true.
    /// </summary>
    public bool HasCdgContent => _layerState.ForegroundOverlay == ForegroundOverlayKind.Cdg;

    /// <summary>
    /// Gets the stable layer role classification for the current media session. This is
    /// decided from media/background roles and must not depend on CDG frame invalidations.
    /// </summary>
    internal MediaLayerState LayerState => _layerState;

    /// <summary>
    /// Gets whether the current media session has a foreground overlay such as CDG.
    /// </summary>
    public bool HasOverlaySession => _layerState.HasOverlaySession;

    /// <summary>
    /// Gets whether the current media session has background media assigned by role.
    /// </summary>
    public bool HasBackgroundMedia => _layerState.HasBackgroundMedia;

    /// <summary>
    /// Diagnostic string for verifying media/layer classification during refactor testing.
    /// </summary>
    public string LayerStateDebug => _layerState.ToString();

    /// <summary>
    /// Gets whether foreground overlay content is layered over separate background media.
    /// In this mode the native/WPF presentation surface must use the full viewport as its
    /// output target; overlay layers are aspect-fitted inside that viewport later.
    /// </summary>
    public bool HasOverlayBackgroundLayer => _layerState.UsesBackgroundPresentation;

    /// <summary>
    /// Gets the background stretch mode after applying overlay layering rules.
    /// Background images/videos fill the viewport for overlay sessions; video-only
    /// playback continues to use the normal media stretch path.
    /// </summary>
    public Stretch EffectiveBackgroundStretch => _layerState.UsesBackgroundPresentation
        ? Stretch.UniformToFill
        : _backgroundStretch;

    /// <summary>
    /// Event raised when the ImageSource changes (new video frame, CDG update, etc.).
    /// </summary>
    public event EventHandler? ImageSourceChanged;

    /// <summary>
    /// Event raised when the background source or stretch mode changes.
    /// </summary>
    public event EventHandler? BackgroundChanged;

    /// <summary>
    /// Event raised when the primary video display mode changes.
    /// </summary>
    public event EventHandler? VideoDisplayModeChanged;

    /// <summary>
    /// Raised immediately after a video frame has been presented. Hosts should invalidate
    /// the surface element on this event to keep WPF's CompositionTarget.Rendering scheduler
    /// running at full display refresh rate; without a real visual-tree invalidation signal,
    /// WPF throttles the render tick down since D3DImage pixel updates alone aren't tracked.
    /// </summary>
    public event EventHandler? FramePresented;

    /// <summary>
    /// Raised when the renderer has attached a different set of primary media engines.
    /// WPF host surfaces use this to switch between native video presentation and
    /// WPF ImageSource presentation for still images/CDG without waiting for a video frame.
    /// </summary>
    public event EventHandler? AttachmentsChanged;

    /// <summary>
    /// Gets the current GPU rendering method being used.
    /// </summary>
    public GpuRenderingMethod CurrentRenderingMethod => _graphicsRenderer.CurrentRenderingMethod;

    /// <summary>
    /// Gets a diagnostic name for the active presentation backend.
    /// Expected values include D3DImage, NativeHwndSwapChain, WriteableBitmap, or None.
    /// </summary>
    public string PresentationBackend => _graphicsRenderer.PresentationBackend;
    /// <summary>
    /// Gets the current primary visual width. For CDG-only content this is the
    /// native CDG frame width, so host/native presentation layout preserves CDG aspect.
    /// </summary>
    public int VideoWidth => _attachedVideo?.Width
        ?? (_stillImageSource as BitmapSource)?.PixelWidth
        ?? _attachedCdg?.FrameWidth
        ?? 0;

    /// <summary>
    /// Gets the current primary visual height. For CDG-only content this is the
    /// native CDG frame height, so host/native presentation layout preserves CDG aspect.
    /// </summary>
    public int VideoHeight => _attachedVideo?.Height
        ?? (_stillImageSource as BitmapSource)?.PixelHeight
        ?? _attachedCdg?.FrameHeight
        ?? 0;


    /// <summary>
    /// Requests native child-HWND swap-chain presentation. Pass IntPtr.Zero to return to the
    /// normal WPF D3DImage presentation path. This is intentionally strict: when native is
    /// requested, failures are logged instead of silently falling back to D3DImage, so A/B tests
    /// actually prove whether WPF/D3DImage is involved.
    /// </summary>
    public void SetNativePresentationWindow(IntPtr hwnd)
    {
        _nativePresentationRequested = hwnd != IntPtr.Zero;
        _backgroundMediaRenderer.UseOwnVideoPresentation = !_nativePresentationRequested;
        _graphicsRenderer.SetNativePresentationWindow(hwnd);
        ApplyVideoPresentationSchedulingMode();
        if (_nativePresentationRequested)
        {
            RefreshNativeCompositeBaseLayer();
            RenderCurrentCdgFrame(force: true);
        }
        if (_enableLogging)
        {
            System.Diagnostics.Debug.WriteLine(hwnd == IntPtr.Zero
                ? $"[MediaRenderer] Presentation backend requested: WpfFallback ({(_wpfCompositionDrivenVideo ? "WPF-driven" : "independent-loop")})"
                : $"[MediaRenderer] Presentation backend requested: NativeHwnd hwnd=0x{hwnd.ToInt64():X}");
        }
    }

    public void SetNativePresentationSize(int pixelWidth, int pixelHeight)
    {
        _graphicsRenderer.SetNativePresentationSize(pixelWidth, pixelHeight);
    }

    public void SetNativePresentationStretch(Stretch stretch)
    {
        _graphicsRenderer.SetNativePresentationStretch(stretch);
    }

    public void SetNativeResizeInProgress(bool isResizing)
    {
        _graphicsRenderer.SetNativeResizeInProgress(isResizing);
    }

    public void RefreshNativePresentation()
    {
        _graphicsRenderer.RefreshNativePresentation();
    }


    private void ApplyVideoPresentationSchedulingMode()
    {
        var wpfDriven = !_nativePresentationRequested
            && _configuration.WpfD3DImagePresentationMode == WpfD3DImagePresentationMode.CompositionTargetRendering;

        _wpfCompositionDrivenVideo = wpfDriven;
        _attachedVideo?.SetWpfCompositionDrivenPresentation(wpfDriven);
    }

    private bool ShouldRenderVideoOnWindowRefresh()
    {
        return _wpfCompositionDrivenVideo && _mediaEngine.Content.HasFlag(MediaContent.Video);
    }

    /// <summary>
    /// Supplies the exact WPF D3DImage presentation size in physical pixels. This lets
    /// D3D11 scale into the displayed video rectangle and keeps WPF overlays airspace-safe
    /// without forcing WPF to scale the video Image every frame. Pass 0,0 to use source size.
    /// </summary>
    public void SetWpfPresentationSize(int pixelWidth, int pixelHeight)
    {
        _graphicsRenderer.SetWpfPresentationSize(pixelWidth, pixelHeight);
    }

    /// <summary>
    /// Supplies the actual refresh rate (Hz) of the monitor displaying this player's surface,
    /// so video presentation timing matches the real display instead of a DWM-wide guess.
    /// Particularly relevant on multi-monitor setups with mismatched refresh rates. Hosts
    /// should call this when the surface is attached and again whenever the hosting window
    /// moves to a different monitor. Pass null to fall back to automatic detection.
    /// </summary>
    public void SetDisplayRefreshRate(double? refreshHz)
    {
        _mediaEngine.SetDisplayRefreshRate(refreshHz);
    }

    private void OnBackgroundMediaChanged(object? sender, EventArgs e)
    {
        UpdateLayerState("background media changed");
        RefreshNativeCompositeBaseLayer();
        BackgroundChanged?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateLayerState(string reason)
    {
        var backgroundKind = _backgroundMediaRenderer.Kind;
        if (backgroundKind == MediaBackgroundKind.None && _backgroundSource != null)
        {
            backgroundKind = MediaBackgroundKind.Image;
        }

        var newState = MediaLayerState.From(_mediaEngine.Content, backgroundKind);
        if (newState.Equals(_layerState))
        {
            return;
        }

        _layerState = newState;
        _graphicsRenderer.SetNativeCompositeCdgLayeringActive(newState.ForegroundOverlay == ForegroundOverlayKind.Cdg);

        if (_enableLogging)
        {
            System.Diagnostics.Debug.WriteLine($"[MediaRenderer] Layer state ({reason}): {newState}");
        }
    }

    internal void ApplyAutoBackground(MediaBackgroundCandidate? background)
    {
        if (!_backgroundAutoDetect || background == null)
        {
            ClearAutoBackground();
            return;
        }

        try
        {
            var applied = _backgroundMediaRenderer.Apply(background);
            _nativeCompositeBaseIsBackgroundVideo = applied && background.Kind == MediaBackgroundKind.Video;
            UpdateLayerState($"auto background applied: {(applied ? background.Kind.ToString() : "none")}");
            RefreshNativeCompositeBaseLayer();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MediaRenderer] Failed to apply auto background '{background.DisplayName}': {ex.Message}");
            ClearAutoBackground();
            return;
        }

        BackgroundChanged?.Invoke(this, EventArgs.Empty);
    }

    internal void ClearAutoBackground()
    {
        _backgroundMediaRenderer.Clear();
        _nativeCompositeBaseIsBackgroundVideo = false;
        UpdateLayerState("auto background cleared");
        RefreshNativeCompositeBaseLayer();
        BackgroundChanged?.Invoke(this, EventArgs.Empty);
    }

    private void AttachVideoIfAvailable()
    {
        // Clean up previous video attachment
        if (_attachedVideo != null)
        {
            try
            {
                _attachedVideo.SetWpfCompositionDrivenPresentation(false);
                _attachedVideo.FrameReady -= OnVideoFrameReady;
            }
            catch { }
            _attachedVideo = null;
        }

        // Attach to new video if available
        if (_mediaEngine.Video != null)
        {
            _attachedVideo = _mediaEngine.Video;
            _attachedVideo.FrameReady += OnVideoFrameReady;
            ApplyVideoPresentationSchedulingMode();
            if (_enableLogging) System.Diagnostics.Debug.WriteLine($"[MediaRenderer] Video attached: {_attachedVideo.Width}x{_attachedVideo.Height}");
        }
    }

    private void AttachCdgIfAvailable()
    {
        // Clean up previous CDG attachment
        if (_attachedCdg != null)
        {
            try
            {
                _attachedCdg.FrameInvalidated -= OnCdgFrameInvalidated;
                _attachedCdg.BackgroundWashChanged -= OnCdgBackgroundWashChanged;
            }
            catch { }
            _attachedCdg = null;
        }

        // Attach to CDG if available. CDG lyrics and the background wash are now
        // separate renderer layers; subscribing here only drives the lyric layer.
        if (_mediaEngine.Cdg != null)
        {
            _attachedCdg = _mediaEngine.Cdg;

            _attachedCdg.FrameInvalidated += OnCdgFrameInvalidated;
            _attachedCdg.BackgroundWashChanged += OnCdgBackgroundWashChanged;
            ApplyCdgBackgroundWash();

            // Set initial CDG image if available and force the zero-time frame into the renderer.
            _renderPipeline.Attach(_attachedVideo, _attachedCdg);
            _renderPipeline.RenderCdgAtCurrentPosition(HasActiveVideo(), force: true);

            if (_enableLogging)
            {
                System.Diagnostics.Debug.WriteLine($"[MediaRenderer] CDG attached: {_attachedCdg.FrameWidth}x{_attachedCdg.FrameHeight}");
            }
        }
        else
        {
            if (_enableLogging) System.Diagnostics.Debug.WriteLine("[MediaRenderer] No CDG content available");
        }

        UpdateLayerState("CDG attachment refreshed");
        BackgroundChanged?.Invoke(this, EventArgs.Empty);
    }

    private void AttachImageIfAvailable()
    {
        _stillImageSource = null;

        if (!_mediaEngine.Content.HasFlag(MediaContent.Image) || string.IsNullOrWhiteSpace(_mediaEngine.ImagePath))
            return;

        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(Path.GetFullPath(_mediaEngine.ImagePath), UriKind.Absolute);
            image.EndInit();
            image.Freeze();

            _stillImageSource = image;
            RefreshNativeCompositeBaseLayer();
            ImageSourceChanged?.Invoke(this, EventArgs.Empty);
            FramePresented?.Invoke(this, EventArgs.Empty);
            if (_enableLogging) System.Diagnostics.Debug.WriteLine($"[MediaRenderer] Still image attached: {_mediaEngine.ImagePath}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MediaRenderer] Failed to attach image: {ex.Message}");
        }
    }

    /// <summary>
    /// Starts natural window refresh rendering.
    /// Works for audio+video, video-only, CDG-only, or audio-only content.
    /// </summary>
    internal void StartRendering()
    {
        if (_isRenderingActive) return;

        _lastWindowRefreshTimestamp = 0;
        _windowRefreshDiagnosticFrames = 0;
        _windowRefreshDiagnosticMaxGapMs = 0;
        _windowRefreshDiagnosticMaxWorkMs = 0;

        // Use WPF's natural rendering cycle for CDG/background/UI-driven updates. Primary video
        // presentation is handled by VideoPipeline; in native HWND mode these ticks are diagnostic
        // only and are not the video swap-chain cadence.
        CompositionTarget.Rendering += OnWindowRefresh;
        _backgroundMediaRenderer.Start();
        _isRenderingActive = true;

        var method = _graphicsRenderer.CurrentRenderingMethod;
        if (_enableLogging) System.Diagnostics.Debug.WriteLine($"[MediaRenderer] Started natural window refresh rendering with {method}");
    }

    /// <summary>
    /// Stops natural window refresh rendering.
    /// </summary>
    internal void StopRendering()
    {
        if (!_isRenderingActive) return;

        CompositionTarget.Rendering -= OnWindowRefresh;
        _backgroundMediaRenderer.Stop(clearFrame: !_backgroundVisibleWhenStopped);
        _isRenderingActive = false;

        if (_enableLogging) System.Diagnostics.Debug.WriteLine("[MediaRenderer] Stopped natural window refresh rendering");
    }

    private long _lastWindowRefreshTimestamp;
    private double _windowRefreshDiagnosticMaxGapMs;
    private double _windowRefreshDiagnosticMaxWorkMs;
    private int _windowRefreshDiagnosticFrames;
    private const int WindowRefreshDiagnosticsInterval = 120;

    /// <summary>
    /// Called on every WPF render frame (monitor refresh rate).
    /// Handles video, CDG, and audio-only content without timers.
    /// </summary>
    private void OnWindowRefresh(object? sender, EventArgs e)
    {
        if (_disposed || !_mediaEngine.IsOpen) return;

        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_lastWindowRefreshTimestamp != 0)
        {
            var gapMs = (now - _lastWindowRefreshTimestamp) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            _windowRefreshDiagnosticMaxGapMs = Math.Max(_windowRefreshDiagnosticMaxGapMs, gapMs);
        }
        _lastWindowRefreshTimestamp = now;

        var workStart = System.Diagnostics.Stopwatch.GetTimestamp();

        try
        {
            if (ShouldRenderVideoOnWindowRefresh())
            {
                _renderPipeline.RenderVideoAtCurrentPosition(allowWhilePlaying: true);
            }

            _renderPipeline.RenderCdgAtCurrentPosition(HasActiveVideo());
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MediaRenderer] Window refresh error: {ex.Message}");
        }

        var workMs = (System.Diagnostics.Stopwatch.GetTimestamp() - workStart) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        _windowRefreshDiagnosticMaxWorkMs = Math.Max(_windowRefreshDiagnosticMaxWorkMs, workMs);
        _windowRefreshDiagnosticFrames++;

        if (_windowRefreshDiagnosticFrames >= WindowRefreshDiagnosticsInterval)
        {
            if (_enableVerboseLogging)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[MediaRenderer:Perf] wpfTicks={_windowRefreshDiagnosticFrames}, maxWpfTickGap={_windowRefreshDiagnosticMaxGapMs:F1}ms, " +
                    $"maxHandlerWork={_windowRefreshDiagnosticMaxWorkMs:F1}ms (WPF callback cadence, not primary video frame cadence)");
            }

            _windowRefreshDiagnosticFrames = 0;
            _windowRefreshDiagnosticMaxGapMs = 0;
            _windowRefreshDiagnosticMaxWorkMs = 0;
        }
    }

    private void OnVideoFrameReady(object? sender, VideoFrameEventArgs e)
    {
        try
        {
            // Check if the dispatcher is shutting down before attempting to invoke
            if (Application.Current?.Dispatcher?.HasShutdownStarted == true)
                return;

            _graphicsRenderer.RenderVideoFrame(e);
        }
        catch (Exception ex) when (ex is TaskCanceledException || ex is InvalidOperationException)
        {
            // Silent handling during shutdown
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MediaRenderer] OnVideoFrameReady error: {ex.Message}");
        }
    }

    private void OnCdgBackgroundWashChanged(object? sender, EventArgs e) => ApplyCdgBackgroundWash();

    private void ApplyCdgBackgroundWash()
    {
        var cdg = _attachedCdg;
        if (cdg == null)
        {
            _graphicsRenderer.SetCdgBackgroundWash(0);
            return;
        }

        _graphicsRenderer.SetCdgBackgroundWash(cdg.BackgroundWashArgb);
    }

    private void OnCdgFrameInvalidated(object? sender, EventArgs e)
    {
        try
        {
            // Check if the dispatcher is shutting down before attempting to invoke
            if (Application.Current?.Dispatcher?.HasShutdownStarted == true)
                return;

            // Always update CDG - renderer will handle priority
            var currentPosition = _renderPipeline.CurrentPosition;
            var logSecond = (int)currentPosition;
            if (logSecond != _lastCdgPresentLogSecond)
            {
                _lastCdgPresentLogSecond = logSecond;
                if (_enableLogging) System.Diagnostics.Debug.WriteLine($"[MediaRenderer] CDG frame invalidated at {currentPosition:F3}s");
            }
            UpdateImageSourceFromCdg();
        }
        catch (Exception ex) when (ex is TaskCanceledException || ex is InvalidOperationException)
        {
            // Ignore dispatcher-related exceptions durante shutdown
        }
    }

    private void UpdateImageSourceFromCdg() => RenderCurrentCdgFrame(force: false);

    private void RenderCurrentCdgFrame(bool force)
    {
        if (_attachedCdg == null) return;

        try
        {
            var width = _attachedCdg.FrameWidth;
            var height = _attachedCdg.FrameHeight;
            var pixelCount = width * height;
            if (pixelCount <= 0)
            {
                return;
            }

            // In native mode the CDG frame is now a compositor layer, not a WPF bitmap/ImageSource.
            // The existing CDG engine remains the authoritative rasterizer; the renderer uploads the
            // final native-composited BGRA frame to the D3D11 HWND surface.
            var frameBuffer = ArrayPool<int>.Shared.Rent(pixelCount);
            try
            {
                var frameData = frameBuffer.AsSpan(0, pixelCount);
                _attachedCdg.CopyFrameArgb(frameData);

                _graphicsRenderer.RenderCdgFrame(frameData, width, height);
            }
            finally
            {
                ArrayPool<int>.Shared.Return(frameBuffer);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MediaRenderer] Failed to update CDG frame: {ex.Message}");
        }
    }

    private void OnBackgroundVideoFrameReady(object? sender, VideoFrameEventArgs e)
    {
        if (!_nativePresentationRequested || e.PixelData.IsEmpty || e.Width <= 0 || e.Height <= 0)
        {
            return;
        }

        try
        {
            _nativeCompositeBaseIsBackgroundVideo = true;
            _graphicsRenderer.SetNativeCompositeVideoBase(e.PixelData.Span, e.Width, e.Height, e.Stride);
        }
        catch (Exception ex)
        {
            if (_enableLogging) System.Diagnostics.Debug.WriteLine($"[MediaRenderer] Native background video composite failed: {ex.Message}");
        }
    }

    private void RefreshNativeCompositeBaseLayer()
    {
        if (!_nativePresentationRequested)
        {
            return;
        }

        if (_stillImageSource is BitmapSource stillImage)
        {
            _nativeCompositeBaseIsBackgroundVideo = false;
            _graphicsRenderer.SetNativeCompositeBase(stillImage);
            if (_enableLogging) System.Diagnostics.Debug.WriteLine($"[MediaRenderer] Native compositor base: still image {stillImage.PixelWidth}x{stillImage.PixelHeight}");
            return;
        }

        if (_nativeCompositeBaseIsBackgroundVideo || _backgroundMediaRenderer.HasVideo)
        {
            // BackgroundMediaRenderer/BackgroundVideoRenderer will push video frames through
            // OnBackgroundVideoFrameReady.  The background-video role is stable even before the
            // first frame arrives, so do not clear/reclassify the native base here.
            return;
        }

        if ((_backgroundMediaRenderer.ImageSource ?? _backgroundSource) is BitmapSource backgroundImage)
        {
            _graphicsRenderer.SetNativeCompositeBase(backgroundImage);
            if (_enableLogging) System.Diagnostics.Debug.WriteLine($"[MediaRenderer] Native compositor base: background image {backgroundImage.PixelWidth}x{backgroundImage.PixelHeight}");
            return;
        }

        _graphicsRenderer.ClearNativeCompositeBase();
    }

    private bool HasActiveVideo()
    {
        // Native HWND presentation does not expose a WPF ImageSource, so the presence of an
        // attached video engine is the correct priority signal here.
        return _attachedVideo != null;
    }

    /// <summary>
    /// Refreshes all attachments when media changes.
    /// Called internally when new media is loaded.
    /// </summary>
    internal void RefreshAttachments()
    {
        if (_enableLogging) System.Diagnostics.Debug.WriteLine("[MediaRenderer] Refreshing attachments - cleaning up previous media");

        // Remember if rendering was active before stopping
        bool wasRenderingActive = _isRenderingActive;

        // Stop any active rendering first
        StopRendering();

        // Clear all content flags in graphics renderer FIRST
        _graphicsRenderer.ClearAllContent();
        _stillImageSource = null;
        _nativeCompositeBaseIsBackgroundVideo = false;
        _lastCdgPresentLogSecond = -1;

        // Force cleanup of previous attachments
        DetachAllEngines();

        // Reattach engines
        AttachVideoIfAvailable();
        AttachCdgIfAvailable();
        AttachImageIfAvailable();
        _renderPipeline.Attach(_attachedVideo, _attachedCdg);
        UpdateLayerState("attachments refreshed");
        RefreshNativeCompositeBaseLayer();

        if (wasRenderingActive || _mediaEngine.State == MediaPlayerState.Playing)
        {
            StartRendering();
            if (_enableLogging) System.Diagnostics.Debug.WriteLine("[MediaRenderer] Rendering restarted after media switch");
        }

        AttachmentsChanged?.Invoke(this, EventArgs.Empty);

        var capabilities = GpuRenderingCapabilities.Capabilities;
        if (_enableLogging) System.Diagnostics.Debug.WriteLine($"[MediaRenderer] Attachments refreshed - Video: {_attachedVideo != null}, CDG: {_attachedCdg != null}, Image: {_stillImageSource != null}, GPU: {capabilities}");
    }

    /// <summary>
    /// Forces detachment of all engines - used for clean switching between media.
    /// </summary>
    private void DetachAllEngines()
    {
        if (_attachedVideo != null)
        {
            try
            {
                _attachedVideo.SetWpfCompositionDrivenPresentation(false);
                _attachedVideo.FrameReady -= OnVideoFrameReady;
            }
            catch { }
            _attachedVideo = null;
            _renderPipeline.Attach(_attachedVideo, _attachedCdg);
            if (_enableLogging) System.Diagnostics.Debug.WriteLine("[MediaRenderer] Video engine detached");
        }

        if (_attachedCdg != null)
        {
            try
            {
                _attachedCdg.FrameInvalidated -= OnCdgFrameInvalidated;
                _attachedCdg.BackgroundWashChanged -= OnCdgBackgroundWashChanged;
            }
            catch { }
            _attachedCdg = null;
            _graphicsRenderer.SetCdgBackgroundWash(0);
            _renderPipeline.Attach(_attachedVideo, _attachedCdg);
            UpdateLayerState("CDG engine detached");
            BackgroundChanged?.Invoke(this, EventArgs.Empty);
            if (_enableLogging) System.Diagnostics.Debug.WriteLine("[MediaRenderer] CDG engine detached");
        }

        if (_stillImageSource != null)
        {
            _stillImageSource = null;
            ImageSourceChanged?.Invoke(this, EventArgs.Empty);
        }

        _renderPipeline.Clear();
    }

    /// <summary>
    /// Disposes of all resources and stops rendering.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;

        StopRendering();

        DetachAllEngines();

        _backgroundMediaRenderer.VideoFrameReady -= OnBackgroundVideoFrameReady;
        _graphicsRenderer?.Dispose();
        _backgroundMediaRenderer.Dispose();

        _disposed = true;
        if (_enableLogging) System.Diagnostics.Debug.WriteLine("[MediaRenderer] Disposed with GPU acceleration support");
    }

}
