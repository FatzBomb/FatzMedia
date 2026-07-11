using FatzBombMedia.MediaControl.Controllers;
using FatzBombMedia.MediaControl.Core.Abstractions;
using FatzBombMedia.MediaControl.Core.Configuration;
using FatzBombMedia.MediaControl.Core.Cdg;
using FatzBombMedia.MediaControl.Core.Graphics.Composition;
using FatzBombMedia.MediaControl.Core.Graphics.D3D;
using FatzBombMedia.MediaControl.Core.Subtitle;
using FatzBombMedia.MediaControl.Core.Threading;
using FatzBombMedia.MediaControl.Core.Video;
using FatzBombMedia.MediaControl.Pipeline;
using System.Buffers;
using System.Diagnostics;

namespace FatzBombMedia.MediaControl.Core.Graphics;

internal sealed class NativeMediaPresentationCoordinator : IMediaRenderCoordinator, IDisposable
{
    private enum CompositionPresentResult
    {
        Failed,
        Presented,
        Deferred
    }

    private const int CdgPacketsPerSector = 4;
    private const int CdgPresentationCadenceHz = CdgConstants.PacketsPerSecond / CdgPacketsPerSector;
    private static readonly long CdgPresentationIntervalTicks = Math.Max(1L, Stopwatch.Frequency / CdgPresentationCadenceHz);

    private const string DeviceLostDiagnosticCategory = "D3D11.DeviceLost";
    private const string DeviceLostRecoveryFailedDiagnosticCategory = "D3D11.DeviceLost.RecoveryFailed";
    private const int DeviceLostRecoveryMaxAttempts = 3;
    private static readonly TimeSpan[] DeviceLostRecoveryBackoffDelays =
    [
        TimeSpan.FromMilliseconds(250),
        TimeSpan.FromMilliseconds(500),
        TimeSpan.FromSeconds(1),
    ];

    // Dedicated pool for per-present background pixel snapshots (007A.2). Sized to pool up to ~4K BGRA
    // frames; ArrayPool<byte>.Shared caps pooled arrays at 1 MB and would allocate large video/background
    // frames on every present. Buffers are rented under _lock during build and returned after the present
    // completes in PresentSoftwareComposition.
    private static readonly ArrayPool<byte> CompositionBackgroundPool =
        ArrayPool<byte>.Create(maxArrayLength: 64 * 1024 * 1024, maxArraysPerBucket: 4);

    private readonly IMediaEngine _mediaEngine;
    private readonly MediaPlayerConfiguration _configuration;
    private readonly RenderPipeline _renderPipeline;
    private readonly NativeD3D11PresentationBridge _presenter;
    private readonly SubtitleTextRenderer _subtitleTextRenderer;
    private readonly PlaybackScheduler _presentationScheduler = new("CoreNativePresentation");
    private readonly PlaybackScheduler _recoveryScheduler = new("DeviceLostRecovery");
    private readonly Dictionary<string, MediaPresentationSurfaceDescriptor> _surfaces = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _lock = new();
    private readonly Lock _cdgPresentationGate = new();
    private readonly Lock _subtitleGate = new();

    private IVideoEngine? _attachedVideo;
    private ICdgEngine? _attachedCdg;
    private bool _isRenderingActive;
    private bool _disposed;
    private int _recoveryInProgress;
    private ReadOnlyMemory<byte> _compositionBackgroundBgra = ReadOnlyMemory<byte>.Empty;
    private byte[]? _compositionBackgroundCopyBuffer;
    private int _compositionBackgroundWidth;
    private int _compositionBackgroundHeight;
    private int _compositionBackgroundStride;
    private MediaCompositionStretchMode _compositionBackgroundStretchMode = MediaCompositionStretchMode.UniformFit;
    private bool _compositionBackgroundIsRetainedGpu;
    private long _compositionBackgroundRevision;
    private MediaCompositionStretchMode _primaryStretchMode = MediaCompositionStretchMode.UniformFit;
    private bool _cdgPresentationPending;
    private bool _cdgPendingConsumeDirtyRegions;
    private bool _cdgPendingRetainedRefresh;
    private CdgRenderSnapshot? _lastCdgSnapshot;
    private string _cdgPendingReason = "cdg pending";
    private ReadOnlyMemory<byte> _subtitleBgra = ReadOnlyMemory<byte>.Empty;
    private string? _subtitleText;
    private long _subtitleRevision;
    private double _lastSubtitlePresentationSeconds = double.NaN;

    public NativeMediaPresentationCoordinator(IMediaEngine mediaEngine, MediaPlayerConfiguration configuration)
    {
        _mediaEngine = mediaEngine ?? throw new ArgumentNullException(nameof(mediaEngine));
        _configuration = configuration;
        _renderPipeline = new RenderPipeline(_mediaEngine);
        _presenter = new NativeD3D11PresentationBridge(configuration);
        _subtitleTextRenderer = new SubtitleTextRenderer(configuration.SubtitleStyle);
        _presenter.FramePresented += OnFramePresented;
        DiagnosticRouter.DiagnosticOccurred += OnDiagnosticOccurred;

        _mediaEngine.ImageFrameReady += OnCoreImageFrameReady;
        _mediaEngine.BackgroundFrameReady += OnCoreBackgroundFrameReady;
        _mediaEngine.BackgroundCleared += OnCoreBackgroundCleared;

        AttachVideoIfAvailable();
        AttachCdgIfAvailable();
        AttachImageIfAvailable();
    }

    public event EventHandler? FramePresented;

    public string PresentationBackend => _presenter.PresentationBackend;

    public VideoDisplayMode VideoDisplayMode
    {
        get;
        set
        {
            if (field == value)
            {
                return;
            }

            field = value;
            _primaryStretchMode = ToCompositionStretchMode(value);
            _presenter.SetNativePresentationStretch(_primaryStretchMode);
            PresentCurrentComposition("display mode changed");
        }
    } = VideoDisplayMode.Zoom;

    public bool AttachPresentationSurface(MediaPresentationSurfaceDescriptor surface)
    {
        if (_disposed || surface is null || !surface.IsValid || surface.Kind != MediaPresentationSurfaceKind.NativeHwnd)
        {
            return false;
        }

        bool attached;
        lock (_lock)
        {
            if (!EnsureD3D11())
            {
                return false;
            }

            attached = AttachSurfaceNoLock(surface);
            if (attached)
            {
                _surfaces[surface.Id] = surface;
            }
        }

        if (attached)
        {
            PresentCurrentComposition("presentation surface attached");
        }

        return attached;
    }

    public bool ResizePresentationSurface(string surfaceId, int pixelWidth, int pixelHeight)
    {
        if (_disposed || string.IsNullOrWhiteSpace(surfaceId) || pixelWidth <= 0 || pixelHeight <= 0)
        {
            return false;
        }

        bool resized;
        bool presentAfterResize;
        lock (_lock)
        {
            if (!_surfaces.TryGetValue(surfaceId, out MediaPresentationSurfaceDescriptor? surface))
            {
                return false;
            }

            if (surface.PixelWidth == pixelWidth && surface.PixelHeight == pixelHeight)
            {
                return true;
            }

            surface = surface.WithSize(pixelWidth, pixelHeight);
            _surfaces[surface.Id] = surface;

            if (surface.Role == MediaPresentationSurfaceRole.Primary)
            {
                if (_presenter.IsDirectCompositionActive)
                {
                    _presenter.QueueDirectCompositionPresentationResize(pixelWidth, pixelHeight);
                    resized = true;
                }
                else
                {
                    _presenter.QueueNativeHwndPresentationResize(pixelWidth, pixelHeight);
                    resized = true;
                }

                presentAfterResize = ShouldPresentImmediatelyAfterPrimaryResize();
            }
            else if (!AttachSurfaceNoLock(surface))
            {
                return false;
            }
            else
            {
                resized = true;
                presentAfterResize = true;
            }
        }

        if (resized && presentAfterResize)
        {
            PresentCurrentComposition("presentation surface resized");
        }

        return resized;
    }

    public bool SetPresentationSurfaceLiveResize(string surfaceId, bool isResizing)
    {
        if (_disposed || string.IsNullOrWhiteSpace(surfaceId))
            return false;

        bool shouldRefreshAfterResize;
        lock (_lock)
        {
            if (!_surfaces.TryGetValue(surfaceId, out MediaPresentationSurfaceDescriptor? surface))
                return false;

            if (surface.Role != MediaPresentationSurfaceRole.Primary)
                return true;

            _presenter.SetNativeLiveResizeInProgress(isResizing);
            shouldRefreshAfterResize = !isResizing;
        }

        if (shouldRefreshAfterResize)
            PresentCurrentComposition("presentation surface live resize ended");

        return true;
    }

    public bool DetachPresentationSurface(string surfaceId)
    {
        if (_disposed || string.IsNullOrWhiteSpace(surfaceId))
            return false;

        lock (_lock)
        {
            if (!_surfaces.Remove(surfaceId, out MediaPresentationSurfaceDescriptor? surface))
                return false;

            if (surface.Role == MediaPresentationSurfaceRole.Primary)
            {
                _presenter.CleanupResources();
                _ = EnsureD3D11();
                RecreateAttachedSurfacesNoLock();
            }
            else
                _ = _presenter.RemoveSecondaryPresentationTarget(surface.Id);

            return true;
        }
    }

    public void RefreshCurrentPosition()
    {
        _renderPipeline.RenderCdgAtCurrentPosition(force: true);
        _renderPipeline.RenderVideoAtCurrentPosition(allowWhilePlaying: true);
        PresentCurrentComposition("current position refreshed");
    }

    void IMediaRenderCoordinator.RunOnRenderThread(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        action();
    }

    void IMediaRenderCoordinator.StartRendering() => StartRendering();

    void IMediaRenderCoordinator.StopRendering() => StopRendering();

    void IMediaRenderCoordinator.RefreshAttachments() => RefreshAttachments();

    public void StartRendering()
    {
        if (_disposed || _isRenderingActive)
            return;

        _isRenderingActive = true;
        _attachedVideo?.SetHostRenderEventDrivenPresentation(false);

        if (_attachedCdg != null)
            _presentationScheduler.Start(RenderLoop);
    }

    public void StopRendering()
    {
        _isRenderingActive = false;
        _presentationScheduler.Stop();
        if (_mediaEngine.State is MediaPlayerState.Stopped or MediaPlayerState.Null)
        {
            ClearVisibleComposition("presentation stopped");
            return;
        }

        _ = PresentPendingCdgFrame(deferIfCompositionBusy: false);
    }

    internal void RefreshAttachments()
    {
        if (_configuration.EnableLogging)
            Debug.WriteLine("[NativeMediaPresentationCoordinator] Refreshing Core presentation attachments");

        var wasRenderingActive = _isRenderingActive;
        StopRendering();
        ClearAllContent();
        DetachAllEngines();

        AttachVideoIfAvailable();
        AttachCdgIfAvailable();
        AttachImageIfAvailable();
        _renderPipeline.Attach(_attachedVideo, _attachedCdg);
        if (HasCdgContent && !ShouldPresentVisualLayers)
            ClearVisibleComposition("attachments refreshed idle");
        else
            PresentCurrentComposition("attachments refreshed");

        if (wasRenderingActive || _mediaEngine.State == MediaPlayerState.Playing)
            StartRendering();
    }

    private void RenderLoop(CancellationToken cancellationToken)
    {
        using var waiter = new PrecisionWaiter();
        var nextTick = Stopwatch.GetTimestamp();
        while (!cancellationToken.IsCancellationRequested)
        {
            _renderPipeline.RenderCdgAtCurrentPosition();
            _ = PresentPendingCdgFrame(deferIfCompositionBusy: true);

            nextTick += CdgPresentationIntervalTicks;
            var delayTicks = nextTick - Stopwatch.GetTimestamp();
            if (delayTicks > 0)
                waiter.Wait(TimeSpan.FromSeconds(delayTicks / (double)Stopwatch.Frequency), cancellationToken.WaitHandle);
            else
            {
                nextTick = Stopwatch.GetTimestamp();
                waiter.Wait(TimeSpan.FromMilliseconds(1), cancellationToken.WaitHandle);
            }
        }
    }

    private bool AttachSurfaceNoLock(MediaPresentationSurfaceDescriptor surface) =>
        surface.Role == MediaPresentationSurfaceRole.Primary
            ? AttachPrimarySurfaceNoLock(surface)
            : _presenter.CreateSecondaryNativeHwndSurface(surface.Id, surface.NativeHandle, surface.PixelWidth, surface.PixelHeight);

    private bool AttachPrimarySurfaceNoLock(MediaPresentationSurfaceDescriptor surface) =>
        _presenter.CreateDirectCompositionSurface(surface.NativeHandle, surface.PixelWidth, surface.PixelHeight)
        || _presenter.CreateNativeHwndSurface(surface.NativeHandle, surface.PixelWidth, surface.PixelHeight);

    private bool EnsureD3D11()
    {
        if (_presenter.NativeDevicePointer != IntPtr.Zero)
            return true;

        var initialized = _presenter.Initialize();
        if (initialized)
            RecreateAttachedSurfacesNoLock();

        return initialized;
    }

    // Hardware frame release safety (007B): the coordinator must share the decoder's D3D11 device AND
    // immediate context so that decode, video-processor blit, and swap-chain Present all run on one
    // sequential command stream. This is what prevents FFmpeg from recycling a hardware texture before
    // the GPU blit has finished consuming it — the decoder cannot submit a new write to the texture
    // until the blit completes, because both operations are on the same D3D11 immediate context.
    //
    // If a future change (e.g. 017A display-rate compositor using a deferred context or separate command
    // queue) breaks this shared-context invariant, a D3D11 fence or deferred-release ring becomes
    // mandatory here. The debug assertion below fires immediately if the invariant is violated.
    private bool EnsureD3D11ForHardwareFrame(VideoFrameEventArgs frame)
    {
        if (!frame.IsD3D11HardwareFrame)
            return false;

        if (_presenter.NativeDevicePointer == frame.D3D11DevicePointer)
        {
            Debug.Assert(_presenter.NativeDevicePointer == frame.D3D11DevicePointer,
                "Hardware frame device must match the coordinator's D3D11 device to serialize decode + blit + present on one immediate context.");
            return true;
        }

        var initialized = _presenter.InitializeFromNativeDevice(frame.D3D11DevicePointer, frame.D3D11DeviceContextPointer);
        if (initialized)
        {
            Debug.Assert(_presenter.NativeDevicePointer == frame.D3D11DevicePointer,
                "InitializeFromNativeDevice must adopt the decoder's D3D11 device to preserve the shared-context hardware frame safety invariant.");
            RecreateAttachedSurfacesNoLock();
        }

        return initialized;
    }

    private void RecreateAttachedSurfacesNoLock()
    {
        foreach (MediaPresentationSurfaceDescriptor surface in _surfaces.Values)
            _ = AttachSurfaceNoLock(surface);
    }

    /// <summary>
    /// Observes the process-wide diagnostic feed for the device-lost condition raised by
    /// <c>D3D11SwapChainPresenter</c>/<c>D3D11DirectCompositionSingleTargetBackend</c> and starts
    /// recovery. This handler runs synchronously on whatever thread raised the diagnostic
    /// (a presentation thread, potentially already inside <see cref="_lock"/>), so it must only
    /// set a debounce guard and hand off to <see cref="_recoveryScheduler"/> rather than rebuild
    /// resources inline.
    /// </summary>
    private void OnDiagnosticOccurred(MediaDiagnosticInfo info)
    {
        if (_disposed
            || info.Level != MediaDiagnosticLevel.Error
            || !string.Equals(info.Category, DeviceLostDiagnosticCategory, StringComparison.Ordinal))
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _recoveryInProgress, 1, 0) == 0)
            _recoveryScheduler.Start(RunDeviceLostRecovery);
    }

    /// <summary>
    /// Recovers from a detected device-lost condition by tearing down and rebuilding the D3D11
    /// device, swap chains, and DirectComposition/secondary targets against the existing HWNDs
    /// already retained in <see cref="_surfaces"/>. Runs on a dedicated worker thread so it never
    /// executes inline with (or blocks) either presentation loop. Retries a bounded number of times
    /// with backoff before giving up and raising a terminal diagnostic.
    /// </summary>
    private void RunDeviceLostRecovery(CancellationToken cancellationToken)
    {
        try
        {
            if (_disposed)
                return;

            var wasRenderingActive = _isRenderingActive;
            StopRendering();

            var recovered = false;
            for (var attempt = 1; attempt <= DeviceLostRecoveryMaxAttempts && !cancellationToken.IsCancellationRequested; attempt++)
            {
                if (attempt > 1)
                {
                    TimeSpan delay = DeviceLostRecoveryBackoffDelays[Math.Min(attempt - 2, DeviceLostRecoveryBackoffDelays.Length - 1)];
                    if (cancellationToken.WaitHandle.WaitOne(delay))
                        break;
                }

                lock (_lock)
                {
                    if (_disposed)
                        return;

                    _presenter.CleanupResources();
                    recovered = EnsureD3D11();
                    RecreateAttachedSurfacesNoLock();
                }

                if (recovered)
                {
                    DiagnosticRouter.Raise(MediaDiagnosticLevel.Info, DeviceLostDiagnosticCategory, $"GPU device recovered after {attempt} attempt(s).");
                    break;
                }

                DiagnosticRouter.Raise(MediaDiagnosticLevel.Warning, DeviceLostDiagnosticCategory, $"Device-lost recovery attempt {attempt}/{DeviceLostRecoveryMaxAttempts} failed.");
            }

            if (recovered)
            {
                if (wasRenderingActive)
                    StartRendering();

                PresentCurrentComposition("device-lost recovery completed");
            }
            else if (!cancellationToken.IsCancellationRequested)
                DiagnosticRouter.Raise(
                    MediaDiagnosticLevel.Error,
                    DeviceLostRecoveryFailedDiagnosticCategory,
                    $"GPU device-lost recovery failed after {DeviceLostRecoveryMaxAttempts} attempts. Presentation will remain unavailable until the host reinitializes.");
        }
        finally
        {
            _ = Interlocked.Exchange(ref _recoveryInProgress, 0);
        }
    }

    private void AttachVideoIfAvailable()
    {
        if (_attachedVideo != null)
        {
            try
            {
                _attachedVideo.SetHostRenderEventDrivenPresentation(false);
                _attachedVideo.FrameReady -= OnVideoFrameReady;
                _attachedVideo.PositionChanged -= OnVideoPositionChanged;
                _attachedVideo.StateChanged -= OnVideoStateChanged;
            }
            catch
            {
            }

            _attachedVideo = null;
        }

        if (_mediaEngine.Video == null)
            return;

        _attachedVideo = _mediaEngine.Video;
        _attachedVideo.SetHostRenderEventDrivenPresentation(false);
        _attachedVideo.FrameReady += OnVideoFrameReady;
        _attachedVideo.PositionChanged += OnVideoPositionChanged;
        _attachedVideo.StateChanged += OnVideoStateChanged;
    }

    private void OnVideoPositionChanged(object? sender, double positionSeconds)
    {
        lock (_subtitleGate)
            _lastSubtitlePresentationSeconds = double.IsFinite(positionSeconds) && positionSeconds >= 0
                ? positionSeconds
                : double.NaN;
    }

    private void OnVideoStateChanged(object? sender, VideoEngineState state)
    {
        if (state is not (VideoEngineState.Ready or VideoEngineState.Closed))
            return;

        lock (_subtitleGate)
            _lastSubtitlePresentationSeconds = double.NaN;
    }

    private void AttachCdgIfAvailable()
    {
        if (_attachedCdg != null)
        {
            try
            {
                _attachedCdg.FrameInvalidated -= OnCdgFrameInvalidated;
                _attachedCdg.BackgroundWashChanged -= OnCdgBackgroundWashChanged;
            }
            catch
            {
            }

            _attachedCdg = null;
        }

        if (_mediaEngine.Cdg == null)
            return;

        _attachedCdg = _mediaEngine.Cdg;
        _attachedCdg.FrameInvalidated += OnCdgFrameInvalidated;
        _attachedCdg.BackgroundWashChanged += OnCdgBackgroundWashChanged;
        _renderPipeline.Attach(_attachedVideo, _attachedCdg);
        if (!ShouldPresentVisualLayers)
        {
            ClearPendingCdgPresentation();
            return;
        }

        _renderPipeline.RenderCdgAtCurrentPosition(force: true);
        if (!PresentPendingCdgFrame(deferIfCompositionBusy: false))
            PresentRetainedCdgFrame();
    }

    private void AttachImageIfAvailable()
    {
        if (!_mediaEngine.Content.HasFlag(MediaContent.Image))
            return;

        if (_mediaEngine.TryGetCurrentImageFrame(out VideoFrameEventArgs? frame) && frame != null)
            PresentCoreImageFrame(frame);
    }

    private void OnCoreImageFrameReady(object? sender, VideoFrameEventArgs e) => PresentCoreImageFrame(e);

    private void PresentCoreImageFrame(VideoFrameEventArgs e)
    {
        if (!IsValidBgraFrame(e))
            return;

        // A standalone image is Layer 0 (base) content, so it honors the same host-selected display
        // stretch mode as primary video instead of a fixed UniformFit.
        SetCompositionLayer0Frame(e, _primaryStretchMode);
        _ = BuildAndPresentSoftwareComposition(0, retainedRefresh: false, "core image frame");
    }

    private void OnCoreBackgroundFrameReady(object? sender, VideoFrameEventArgs e)
    {
        if (IsValidBgraFrame(e))
        {
            SetCompositionLayer0Frame(e, MediaCompositionStretchMode.UniformToFill);
            if (HasCdgContent)
            {
                if (!ShouldPresentVisualLayers)
                    return;

                if (!HasReadyCdgSnapshotForBackgroundPresentation())
                {
                    RequestCdgPresentation(consumeDirtyRegions: false, retainedRefresh: true, "background waiting for cdg");
                    return;
                }

                PresentRetainedCdgFrame();
                return;
            }

            _ = BuildAndPresentSoftwareComposition(0, retainedRefresh: false, "core background frame");
            return;
        }

        if (e.IsD3D11HardwareFrame)
        {
            if (HasCdgContent && !ShouldPresentVisualLayers)
            {
                StoreRetainedHardwareBackgroundFrame(e, MediaCompositionStretchMode.UniformToFill);
                return;
            }

            if (HasCdgContent && !HasReadyCdgSnapshotForBackgroundPresentation())
            {
                StoreRetainedHardwareBackgroundFrame(e, MediaCompositionStretchMode.UniformToFill);
                RequestCdgPresentation(consumeDirtyRegions: false, retainedRefresh: true, "hardware background waiting for cdg");
                return;
            }

            RenderHardwareVideoCompositionFrame(e, forceZoomToFill: true, "core hardware background frame");
            return;
        }

        ClearCompositionLayer0Frame();
    }

    private void OnCoreBackgroundCleared(object? sender, EventArgs e)
    {
        ClearCompositionLayer0Frame();
        if (!ShouldPresentVisualLayers)
        {
            ClearVisibleComposition("core background cleared idle");
            return;
        }

        if (HasCdgContent)
        {
            PresentRetainedCdgFrame();
            return;
        }

        _ = PresentMediaComposition(MediaCompositionFrame.Empty(
            MediaCompositionClearMode.Transparent,
            0x00000000,
            "core background cleared"));
    }

    private void OnVideoFrameReady(object? sender, VideoFrameEventArgs e)
    {
        try
        {
            if (HasCdgContent)
            {
                PresentVideoFrameAsCdgBackground(e);
                return;
            }

            PresentPrimaryVideoFrame(e);
        }
        catch (Exception ex)
        {
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Warning, "Composition", $"Video frame present failed: {ex.Message}", innerException: ex);
        }
    }

    private void PresentPrimaryVideoFrame(VideoFrameEventArgs e)
    {
        if (IsValidBgraFrame(e))
        {
            SetCompositionLayer0Frame(e, _primaryStretchMode);
            _ = BuildAndPresentSoftwareComposition(0, retainedRefresh: false, "primary video frame", e.PtsSeconds);
            return;
        }

        if (!e.IsD3D11HardwareFrame)
            return;

        if (HasSubtitleContent)
        {
            UpdateSubtitleOverlay(e.PtsSeconds);
            if (HasCachedSubtitleOverlay())
            {
                RenderHardwareVideoCompositionFrame(e, forceZoomToFill: false, "hardware video + subtitle");
                return;
            }
        }

        lock (_lock)
        {
            if (!EnsureD3D11ForHardwareFrame(e))
                return;

            _ = _presenter.PresentHardwareFrame(e);
        }
    }

    private void PresentVideoFrameAsCdgBackground(VideoFrameEventArgs e)
    {
        if (IsValidBgraFrame(e))
        {
            SetCompositionLayer0Frame(e, MediaCompositionStretchMode.UniformToFill);
            if (!HasReadyCdgSnapshotForBackgroundPresentation())
            {
                RequestCdgPresentation(consumeDirtyRegions: false, retainedRefresh: true, "video background waiting for cdg");
                return;
            }

            PresentRetainedCdgFrame();
            return;
        }

        if (e.IsD3D11HardwareFrame)
        {
            if (!HasReadyCdgSnapshotForBackgroundPresentation())
            {
                StoreRetainedHardwareBackgroundFrame(e, MediaCompositionStretchMode.UniformToFill);
                RequestCdgPresentation(consumeDirtyRegions: false, retainedRefresh: true, "hardware video background waiting for cdg");
                return;
            }

            RenderHardwareVideoCompositionFrame(e, forceZoomToFill: true, "hardware video + cdg");
        }
    }

    private void OnCdgBackgroundWashChanged(object? sender, EventArgs e) =>
        RequestCdgPresentationIfVisible(consumeDirtyRegions: false, retainedRefresh: true, "cdg wash changed");

    private void OnCdgFrameInvalidated(object? sender, EventArgs e) =>
        RequestCdgPresentationIfVisible(consumeDirtyRegions: true, retainedRefresh: false, "cdg invalidated");

    private void PresentRetainedCdgFrame()
    {
        _ = ConsumePendingCdgPresentation(out var consumeDirtyRegions, out _, out _);
        _ = RenderCurrentCdgFrame(consumeDirtyRegions, retainedRefresh: true, "retained cdg refresh");
    }

    private void RequestCdgPresentation(bool consumeDirtyRegions, bool retainedRefresh, string reason)
    {
        lock (_cdgPresentationGate)
        {
            _cdgPendingConsumeDirtyRegions |= consumeDirtyRegions;
            _cdgPendingRetainedRefresh = _cdgPresentationPending
                ? _cdgPendingRetainedRefresh && retainedRefresh
                : retainedRefresh;
            _cdgPendingReason = reason;
            _cdgPresentationPending = true;
        }

        if (!_isRenderingActive)
            _ = PresentPendingCdgFrame(deferIfCompositionBusy: false);
    }

    private void RequestCdgPresentationIfVisible(bool consumeDirtyRegions, bool retainedRefresh, string reason)
    {
        if (!ShouldPresentVisualLayers)
        {
            ClearPendingCdgPresentation();
            return;
        }

        RequestCdgPresentation(consumeDirtyRegions, retainedRefresh, reason);
    }

    private bool PresentPendingCdgFrame(bool deferIfCompositionBusy)
    {
        bool consumeDirtyRegions;
        bool retainedRefresh;
        string reason;

        lock (_cdgPresentationGate)
        {
            if (!_cdgPresentationPending)
                return false;

            consumeDirtyRegions = _cdgPendingConsumeDirtyRegions;
            retainedRefresh = _cdgPendingRetainedRefresh;
            reason = _cdgPendingReason;
            _cdgPresentationPending = false;
            _cdgPendingConsumeDirtyRegions = false;
            _cdgPendingRetainedRefresh = false;
            _cdgPendingReason = "cdg pending";
        }

        CompositionPresentResult result = RenderCurrentCdgFrame(consumeDirtyRegions, retainedRefresh, reason, deferIfCompositionBusy);
        if (result == CompositionPresentResult.Deferred)
            RequeueCdgPresentation(consumeDirtyRegions, retainedRefresh, reason);

        return result == CompositionPresentResult.Presented;
    }

    private void RequeueCdgPresentation(bool consumeDirtyRegions, bool retainedRefresh, string reason)
    {
        lock (_cdgPresentationGate)
        {
            _cdgPendingConsumeDirtyRegions |= consumeDirtyRegions;
            _cdgPendingRetainedRefresh = _cdgPresentationPending
                ? _cdgPendingRetainedRefresh && retainedRefresh
                : retainedRefresh;
            _cdgPendingReason = reason;
            _cdgPresentationPending = true;
        }
    }

    private void ClearPendingCdgPresentation()
    {
        lock (_cdgPresentationGate)
            ClearPendingCdgPresentationNoLock();
    }

    private bool ConsumePendingCdgPresentation(
        out bool consumeDirtyRegions,
        out bool retainedRefresh,
        out string reason)
    {
        lock (_cdgPresentationGate)
        {
            if (!_cdgPresentationPending)
            {
                consumeDirtyRegions = false;
                retainedRefresh = false;
                reason = "cdg pending";
                return false;
            }

            consumeDirtyRegions = _cdgPendingConsumeDirtyRegions;
            retainedRefresh = _cdgPendingRetainedRefresh;
            reason = _cdgPendingReason;
            ClearPendingCdgPresentationNoLock();
            return true;
        }
    }

    private void ClearPendingCdgPresentationNoLock()
    {
        _cdgPresentationPending = false;
        _cdgPendingConsumeDirtyRegions = false;
        _cdgPendingRetainedRefresh = false;
        _cdgPendingReason = "cdg pending";
    }

    private CompositionPresentResult RenderCurrentCdgFrame(
        bool consumeDirtyRegions,
        bool retainedRefresh,
        string reason,
        bool deferIfCompositionBusy = false)
    {
        UpdateSubtitleOverlay();
        MediaCompositionFrame frame;
        byte[]? rentedBackground;
        CompositionPresentResult buildResult;

        if (deferIfCompositionBusy)
        {
            if (!_lock.TryEnter())
                return CompositionPresentResult.Deferred;

            try
            {
                buildResult = TryBuildCdgCompositionFrameNoLock(consumeDirtyRegions, retainedRefresh, reason, out frame, out rentedBackground);
            }
            finally
            {
                _lock.Exit();
            }
        }
        else
        {
            lock (_lock)
                buildResult = TryBuildCdgCompositionFrameNoLock(consumeDirtyRegions, retainedRefresh, reason, out frame, out rentedBackground);
        }

        // The CDG snapshot and composition frame are built under _lock; the expensive GPU present runs
        // outside it so transport-visible operations (pause/stop/seek/resize) are not stalled (007A.2).
        return buildResult == CompositionPresentResult.Presented
            ? PresentSoftwareComposition(frame, rentedBackground)
            : buildResult;
    }

    // Builds the current CDG composition frame under _lock. A result of Presented means "successfully
    // built and ready to present outside the lock"; Failed / Deferred short-circuit before any present.
    private CompositionPresentResult TryBuildCdgCompositionFrameNoLock(
        bool consumeDirtyRegions,
        bool retainedRefresh,
        string reason,
        out MediaCompositionFrame frame,
        out byte[]? rentedBackground)
    {
        frame = default;
        rentedBackground = null;

        if (!TryCreateAndCacheCdgSnapshot(consumeDirtyRegions, out CdgRenderSnapshot? snapshot) || snapshot == null)
            return CompositionPresentResult.Failed;

        if (HasCompositionBackgroundNoLock() && !IsCdgSnapshotReadyForBackgroundPresentation(snapshot))
            return CompositionPresentResult.Deferred;

        if (!EnsureD3D11())
            return CompositionPresentResult.Failed;

        frame = BuildCompositionFrameNoLock(snapshot, snapshot.BackgroundWashArgb, retainedRefresh, reason, out rentedBackground);
        return CompositionPresentResult.Presented;
    }

    private void RenderHardwareVideoCompositionFrame(VideoFrameEventArgs frame, bool forceZoomToFill, string reason)
    {
        UpdateSubtitleOverlay();
        CdgRenderSnapshot? snapshot = null;
        uint washArgb = 0;
        if (TryGetCdgSnapshotForVideoComposition(out CdgRenderSnapshot? cdgSnapshot) && cdgSnapshot != null)
        {
            snapshot = cdgSnapshot;
            washArgb = cdgSnapshot.BackgroundWashArgb;
        }

        MediaCompositionFrame compositionFrame;
        lock (_lock)
        {
            if (!EnsureD3D11ForHardwareFrame(frame))
                return;

            MediaCompositionStretchMode stretch = forceZoomToFill ? MediaCompositionStretchMode.UniformToFill : _primaryStretchMode;
            _compositionBackgroundBgra = ReadOnlyMemory<byte>.Empty;
            _compositionBackgroundWidth = frame.Width;
            _compositionBackgroundHeight = frame.Height;
            _compositionBackgroundStride = frame.Width * 4;
            _compositionBackgroundStretchMode = stretch;
            _compositionBackgroundIsRetainedGpu = true;
            var backgroundRevision = ++_compositionBackgroundRevision;

            compositionFrame = new MediaCompositionFrame
            {
                ClearMode = MediaCompositionClearMode.Black,
                ClearArgb = 0xFF000000,
                BackgroundD3D11TexturePointer = frame.D3D11TexturePointer,
                BackgroundD3D11ArraySlice = frame.D3D11ArraySlice,
                BackgroundD3D11Width = frame.Width,
                BackgroundD3D11Height = frame.Height,
                BackgroundD3D11FramesPerSecond = frame.FramesPerSecond,
                BackgroundWidth = frame.Width,
                BackgroundHeight = frame.Height,
                BackgroundStride = frame.Width * 4,
                BackgroundStretchMode = stretch,
                BackgroundUniformToFill = stretch == MediaCompositionStretchMode.UniformToFill,
                BackgroundRevision = backgroundRevision,
                WashArgb = washArgb,
                CdgOverlay = snapshot,
                SubtitleBgra = GetCachedSubtitleOverlay(out var subtitleWidth, out var subtitleHeight, out var subtitleStride, out var subtitleRevision),
                SubtitleWidth = subtitleWidth,
                SubtitleHeight = subtitleHeight,
                SubtitleStride = subtitleStride,
                SubtitleRevision = subtitleRevision,
                Reason = reason
            };
        }

        _ = PresentMediaComposition(compositionFrame);
    }

    private void StoreRetainedHardwareBackgroundFrame(VideoFrameEventArgs frame, MediaCompositionStretchMode stretch)
    {
        if (!frame.IsD3D11HardwareFrame)
            return;

        lock (_lock)
        {
            if (!EnsureD3D11ForHardwareFrame(frame))
                return;

            _compositionBackgroundBgra = ReadOnlyMemory<byte>.Empty;
            _compositionBackgroundWidth = frame.Width;
            _compositionBackgroundHeight = frame.Height;
            _compositionBackgroundStride = frame.Width * 4;
            _compositionBackgroundStretchMode = stretch;
            _compositionBackgroundIsRetainedGpu = true;
            _compositionBackgroundRevision++;
        }
    }

    private bool TryGetCdgSnapshotForVideoComposition(out CdgRenderSnapshot? snapshot)
    {
        snapshot = null;
        if (!HasCdgContent)
            return false;

        var hadPending = ConsumePendingCdgPresentation(
            out var consumeDirtyRegions,
            out var retainedRefresh,
            out var pendingReason);
        if (!hadPending)
        {
            lock (_cdgPresentationGate)
                snapshot = _lastCdgSnapshot;

            if (snapshot != null && IsCdgSnapshotReadyForBackgroundPresentation(snapshot))
                return true;
        }

        if (TryCreateAndCacheCdgSnapshot(consumeDirtyRegions, out snapshot)
            && snapshot != null
            && IsCdgSnapshotReadyForBackgroundPresentation(snapshot))
            return true;

        if (hadPending)
            RequeueCdgPresentation(consumeDirtyRegions, retainedRefresh, pendingReason);

        return false;
    }

    private bool HasReadyCdgSnapshotForBackgroundPresentation()
    {
        lock (_cdgPresentationGate)
            return _lastCdgSnapshot != null && IsCdgSnapshotReadyForBackgroundPresentation(_lastCdgSnapshot);
    }

    private bool HasCompositionBackgroundNoLock()
    {
        var hasBgra = !_compositionBackgroundBgra.IsEmpty
            && _compositionBackgroundWidth > 0
            && _compositionBackgroundHeight > 0
            && _compositionBackgroundStride >= _compositionBackgroundWidth * 4;

        var hasRetainedGpu = _compositionBackgroundIsRetainedGpu
            && _compositionBackgroundWidth > 0
            && _compositionBackgroundHeight > 0;

        return hasBgra || hasRetainedGpu;
    }

    private static bool IsCdgSnapshotReadyForBackgroundPresentation(CdgRenderSnapshot snapshot) =>
        snapshot.BackgroundColorIndex >= 0
        || ((snapshot.BackgroundWashArgb >> 24) & 0xFF) != 0;

    private bool TryCreateAndCacheCdgSnapshot(bool consumeDirtyRegions, out CdgRenderSnapshot? snapshot)
    {
        snapshot = null;
        if (_attachedCdg is not ICdgSnapshotSource snapshotSource
            || !_mediaEngine.Content.HasFlag(MediaContent.Cdg)
            || !snapshotSource.TryCreateCdgSnapshot(consumeDirtyRegions, out CdgRenderSnapshot? createdSnapshot))
            return false;

        snapshot = createdSnapshot;
        lock (_cdgPresentationGate)
            _lastCdgSnapshot = createdSnapshot;

        return true;
    }

    private void PresentCurrentComposition(string reason)
    {
        if (HasCdgContent)
        {
            if (!ShouldPresentVisualLayers)
            {
                ClearVisibleComposition(reason + " idle");
                return;
            }

            PresentRetainedCdgFrame();
            return;
        }

        if (_mediaEngine.Content.HasFlag(MediaContent.Image)
            && _mediaEngine.TryGetCurrentImageFrame(out VideoFrameEventArgs? frame)
            && frame != null)
        {
            PresentCoreImageFrame(frame);
            return;
        }

        // Re-present the retained current video frame so a stopped/first-frame poster reaches a newly
        // attached or resized surface. The first decoded frame is produced during Open before the
        // coordinator subscribes to FrameReady, so without this reissue a stopped video shows nothing.
        if (_mediaEngine.Content.HasFlag(MediaContent.Video))
        {
            _renderPipeline.RenderVideoAtCurrentPosition(allowWhilePlaying: true, forceReissue: true);
            return;
        }

        _ = PresentMediaComposition(MediaCompositionFrame.Empty(
            MediaCompositionClearMode.Transparent,
            0x00000000,
            reason));
    }

    private CompositionPresentResult PresentMediaComposition(MediaCompositionFrame frame)
    {
        lock (_lock)
            return PresentMediaCompositionNoLock(frame);
    }

    private CompositionPresentResult PresentMediaCompositionNoLock(MediaCompositionFrame frame)
    {
        if (!EnsureD3D11())
            return CompositionPresentResult.Failed;

        if (!_presenter.PresentMediaCompositionVisible(frame))
        {
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Warning, "Composition", $"Composition present failed reason={frame.Reason}");

            return CompositionPresentResult.Failed;
        }

        return CompositionPresentResult.Presented;
    }

    // Software / image / BGRA-video present path (007A.2): build the composition frame under _lock with
    // an owned pixel snapshot, then present outside _lock so the transport-visible lock is not held
    // across the vsync-bound GPU present. Hardware (D3D11 texture) paths deliberately stay under _lock
    // and are handled separately (deferred to stage 007B).
    private CompositionPresentResult BuildAndPresentSoftwareComposition(
        uint washArgb,
        bool retainedRefresh,
        string reason,
        double? subtitlePositionSeconds = null)
    {
        UpdateSubtitleOverlay(subtitlePositionSeconds);
        MediaCompositionFrame frame;
        byte[]? rentedBackground;
        lock (_lock)
        {
            if (!EnsureD3D11())
                return CompositionPresentResult.Failed;

            frame = BuildCompositionFrameNoLock(null, washArgb, retainedRefresh, reason, out rentedBackground);
        }

        return PresentSoftwareComposition(frame, rentedBackground);
    }

    // Presents an already-built composition frame WITHOUT holding the coordinator's transport-visible
    // _lock. The D3D11 device context stays serialized by NativeD3D11PresentationBridge's own lock. Any
    // pooled background buffer captured for this frame is returned once the present completes.
    private CompositionPresentResult PresentSoftwareComposition(MediaCompositionFrame frame, byte[]? rentedBackground)
    {
        try
        {
            if (!_presenter.PresentMediaCompositionVisible(frame))
            {
                DiagnosticRouter.Raise(MediaDiagnosticLevel.Warning, "Composition", $"Composition present failed reason={frame.Reason}");
                return CompositionPresentResult.Failed;
            }

            return CompositionPresentResult.Presented;
        }
        finally
        {
            if (rentedBackground != null)
                CompositionBackgroundPool.Return(rentedBackground);
        }
    }

    private MediaCompositionFrame BuildCompositionFrameNoLock(
        CdgRenderSnapshot? cdgOverlay,
        uint washArgb,
        bool retainedRefresh,
        string reason,
        out byte[]? rentedBackground)
    {
        rentedBackground = null;
        ReadOnlyMemory<byte> backgroundBgra;
        int backgroundWidth;
        int backgroundHeight;
        int backgroundStride;
        MediaCompositionStretchMode backgroundStretchMode;
        bool backgroundIsRetainedGpu;
        long backgroundRevision;

        backgroundBgra = _compositionBackgroundBgra;
        backgroundWidth = _compositionBackgroundWidth;
        backgroundHeight = _compositionBackgroundHeight;
        backgroundStride = _compositionBackgroundStride;
        backgroundStretchMode = _compositionBackgroundStretchMode;
        backgroundIsRetainedGpu = _compositionBackgroundIsRetainedGpu;
        backgroundRevision = _compositionBackgroundRevision;

        var hasBackground = !backgroundBgra.IsEmpty
            && backgroundWidth > 0
            && backgroundHeight > 0
            && backgroundStride >= backgroundWidth * 4
            && backgroundBgra.Length >= checked(backgroundStride * backgroundHeight);
        var hasRetainedGpuBackground = !hasBackground
            && backgroundIsRetainedGpu
            && backgroundWidth > 0
            && backgroundHeight > 0;

        // Take an owned snapshot of the background pixels so the frame can be presented after _lock is
        // released without racing a concurrent SetCompositionLayer0Frame overwriting the master buffer
        // (007A.2). The rented buffer is returned to the pool by PresentSoftwareComposition.
        ReadOnlyMemory<byte> ownedBackgroundBgra = ReadOnlyMemory<byte>.Empty;
        if (hasBackground)
        {
            var backgroundLength = backgroundBgra.Length;
            rentedBackground = CompositionBackgroundPool.Rent(backgroundLength);
            try
            {
                backgroundBgra.Span.CopyTo(rentedBackground);
                ownedBackgroundBgra = new ReadOnlyMemory<byte>(rentedBackground, 0, backgroundLength);
            }
            catch
            {
                CompositionBackgroundPool.Return(rentedBackground);
                rentedBackground = null;
                throw;
            }
        }

        var subtitleBgra = GetCachedSubtitleOverlay(out var subtitleWidth, out var subtitleHeight, out var subtitleStride, out var subtitleRevision);

        return new MediaCompositionFrame
        {
            ClearMode = MediaCompositionClearMode.Black,
            ClearArgb = 0xFF000000,
            BackgroundBgra = hasBackground ? ownedBackgroundBgra : ReadOnlyMemory<byte>.Empty,
            BackgroundWidth = hasBackground || hasRetainedGpuBackground ? backgroundWidth : 0,
            BackgroundHeight = hasBackground || hasRetainedGpuBackground ? backgroundHeight : 0,
            BackgroundStride = hasBackground ? backgroundStride : 0,
            BackgroundStretchMode = hasBackground || hasRetainedGpuBackground ? backgroundStretchMode : MediaCompositionStretchMode.UniformFit,
            BackgroundUniformToFill = (hasBackground || hasRetainedGpuBackground) && backgroundStretchMode == MediaCompositionStretchMode.UniformToFill,
            BackgroundRevision = hasBackground || hasRetainedGpuBackground ? backgroundRevision : -1,
            UseRetainedBackground = hasRetainedGpuBackground,
            WashArgb = washArgb,
            CdgOverlay = cdgOverlay,
            SubtitleBgra = subtitleBgra,
            SubtitleWidth = subtitleWidth,
            SubtitleHeight = subtitleHeight,
            SubtitleStride = subtitleStride,
            SubtitleRevision = subtitleRevision,
            RetainedRefresh = retainedRefresh,
            Reason = reason
        };
    }

    // Direct2D/WIC work stays outside the coordinator lock so text changes cannot stall transport.
    private void UpdateSubtitleOverlay(double? presentationPositionSeconds = null)
    {
        lock (_subtitleGate)
        {
            var subtitlePosition = presentationPositionSeconds is { } position && double.IsFinite(position) && position >= 0
                ? position
                : _mediaEngine.Position;
            if (double.IsFinite(_lastSubtitlePresentationSeconds) && subtitlePosition < _lastSubtitlePresentationSeconds)
                subtitlePosition = _lastSubtitlePresentationSeconds;
            else
                _lastSubtitlePresentationSeconds = subtitlePosition;

            var activeCues = _mediaEngine.Subtitle?.GetActiveCues(subtitlePosition) ?? [];
            var text = activeCues.Count == 0 ? null : string.Join("\n", activeCues.Select(static cue => cue.Text));
            if (string.Equals(text, _subtitleText, StringComparison.Ordinal))
                return;

            _subtitleText = text;
            _subtitleBgra = ReadOnlyMemory<byte>.Empty;
            _subtitleRevision++;
            if (string.IsNullOrWhiteSpace(text))
                return;

            try
            {
                _subtitleBgra = _subtitleTextRenderer.Render(text, 1920, 1080);
            }
            catch (Exception ex)
            {
                DiagnosticRouter.Raise(MediaDiagnosticLevel.Warning, "Subtitle", $"Text rasterization failed: {ex.Message}", innerException: ex);
            }
        }
    }

    private bool HasCachedSubtitleOverlay()
    {
        lock (_subtitleGate)
            return !_subtitleBgra.IsEmpty;
    }

    private ReadOnlyMemory<byte> GetCachedSubtitleOverlay(out int width, out int height, out int stride, out long revision)
    {
        lock (_subtitleGate)
        {
            if (_subtitleBgra.IsEmpty)
            {
                width = 0;
                height = 0;
                stride = 0;
                revision = -1;
                return ReadOnlyMemory<byte>.Empty;
            }

            width = 1920;
            height = 1080;
            stride = width * 4;
            revision = _subtitleRevision;
            return _subtitleBgra;
        }
    }

    private void SetCompositionLayer0Frame(VideoFrameEventArgs frame, MediaCompositionStretchMode stretchMode)
    {
        if (_disposed || !IsValidBgraFrame(frame))
        {
            ClearCompositionLayer0Frame();
            return;
        }

        var required = checked(frame.Stride * frame.Height);
        if (frame.PixelData.Length < required)
        {
            ClearCompositionLayer0Frame();
            return;
        }

        lock (_lock)
        {
            // Always copy incoming pixels into the coordinator-owned master buffer. The master is only
            // read/written under _lock; the per-present snapshot taken in BuildCompositionFrameNoLock is
            // what safely crosses the lock boundary (007A.2). The former PixelDataCanBeRetained fast-path
            // aliased the caller's frame buffer, whose lifetime cannot be guaranteed once _lock is
            // released around the present.
            if (_compositionBackgroundCopyBuffer == null || _compositionBackgroundCopyBuffer.Length != required)
                _compositionBackgroundCopyBuffer = new byte[required];

            frame.PixelData.Span[..required].CopyTo(_compositionBackgroundCopyBuffer);
            _compositionBackgroundBgra = new ReadOnlyMemory<byte>(_compositionBackgroundCopyBuffer, 0, required);

            _compositionBackgroundWidth = frame.Width;
            _compositionBackgroundHeight = frame.Height;
            _compositionBackgroundStride = frame.Stride;
            _compositionBackgroundStretchMode = stretchMode;
            _compositionBackgroundIsRetainedGpu = false;
            _compositionBackgroundRevision++;
        }
    }

    private void ClearCompositionLayer0Frame()
    {
        lock (_lock)
            ClearCompositionLayer0FrameNoLock();

        _presenter.ClearCompositionBackgroundFrame();
    }

    private void ClearCompositionLayer0FrameNoLock()
    {
        _compositionBackgroundBgra = ReadOnlyMemory<byte>.Empty;
        _compositionBackgroundCopyBuffer = null;
        _compositionBackgroundWidth = 0;
        _compositionBackgroundHeight = 0;
        _compositionBackgroundStride = 0;
        _compositionBackgroundStretchMode = MediaCompositionStretchMode.UniformFit;
        _compositionBackgroundIsRetainedGpu = false;
        _compositionBackgroundRevision++;
    }

    private void ClearAllContent() => ClearVisibleComposition("all content cleared");

    private void ClearVisibleComposition(string reason)
    {
        ClearPendingCdgPresentation();
        lock (_cdgPresentationGate)
            _lastCdgSnapshot = null;

        lock (_lock)
        {
            ClearCompositionLayer0FrameNoLock();
            _presenter.ClearCompositionBackgroundFrame();
            _ = PresentMediaCompositionNoLock(MediaCompositionFrame.Empty(
                MediaCompositionClearMode.Transparent,
                0x00000000,
                reason));
        }
    }

    private void DetachAllEngines()
    {
        if (_attachedVideo != null)
        {
            try
            {
                _attachedVideo.SetHostRenderEventDrivenPresentation(false);
                _attachedVideo.FrameReady -= OnVideoFrameReady;
            }
            catch
            {
            }

            _attachedVideo = null;
        }

        if (_attachedCdg != null)
        {
            try
            {
                _attachedCdg.FrameInvalidated -= OnCdgFrameInvalidated;
                _attachedCdg.BackgroundWashChanged -= OnCdgBackgroundWashChanged;
            }
            catch
            {
            }

            _attachedCdg = null;
        }

        lock (_cdgPresentationGate)
        {
            _lastCdgSnapshot = null;
            ClearPendingCdgPresentationNoLock();
        }

        _renderPipeline.Clear();
    }

    private bool HasCdgContent => _mediaEngine.Content.HasFlag(MediaContent.Cdg);

    private bool HasSubtitleContent => _mediaEngine.Content.HasFlag(MediaContent.Subtitle);

    private bool ShouldPresentVisualLayers =>
        _isRenderingActive
        || _mediaEngine.State is MediaPlayerState.Playing or MediaPlayerState.Paused
        || HasStoppedStandaloneVisualContent;

    // Standalone visual media without an audio stream (CDG or video with no audio) never enters
    // Playing/Paused on load, but should still show its first visual frame while stopped, like an image.
    private bool HasStoppedStandaloneVisualContent =>
        _mediaEngine.State == MediaPlayerState.Stopped
        && !_mediaEngine.Content.HasFlag(MediaContent.Audio)
        && (_mediaEngine.Content.HasFlag(MediaContent.Video)
            || _mediaEngine.Content.HasFlag(MediaContent.Cdg));

    private bool ShouldPresentImmediatelyAfterPrimaryResize() =>
        !_isRenderingActive
        || (!HasCdgContent && !_mediaEngine.Content.HasFlag(MediaContent.Video));

    private static bool IsValidBgraFrame(VideoFrameEventArgs e) =>
        !e.PixelData.IsEmpty && e.Width > 0 && e.Height > 0 && e.Stride >= e.Width * 4;

    private static MediaCompositionStretchMode ToCompositionStretchMode(VideoDisplayMode mode) => mode switch
    {
        VideoDisplayMode.Original => MediaCompositionStretchMode.Original,
        VideoDisplayMode.Stretch => MediaCompositionStretchMode.Fill,
        VideoDisplayMode.ZoomToFill => MediaCompositionStretchMode.UniformToFill,
        _ => MediaCompositionStretchMode.UniformFit
    };

    private void OnFramePresented(object? sender, EventArgs e) => FramePresented?.Invoke(this, EventArgs.Empty);

    public void Dispose()
    {
        if (_disposed)
            return;

        StopRendering();
        DetachAllEngines();
        _mediaEngine.ImageFrameReady -= OnCoreImageFrameReady;
        _mediaEngine.BackgroundFrameReady -= OnCoreBackgroundFrameReady;
        _mediaEngine.BackgroundCleared -= OnCoreBackgroundCleared;
        _presenter.FramePresented -= OnFramePresented;
        DiagnosticRouter.DiagnosticOccurred -= OnDiagnosticOccurred;
        _subtitleTextRenderer.Dispose();
        _presenter.Dispose();
        _presentationScheduler.Dispose();
        _recoveryScheduler.Dispose();
        _disposed = true;
    }
}
