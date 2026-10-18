using FatzBomb.MediaControl.Core.Abstractions;
using FatzBomb.MediaControl.Core.Cdg;
using FatzBomb.MediaControl.Core.Configuration;
using FatzBomb.MediaControl.Core.Graphics;
using FatzBomb.MediaControl.Core.Graphics.Composition;
using FatzBomb.MediaControl.Core.Subtitle;
using FatzBomb.MediaControl.Core.Threading;
using FatzBomb.MediaControl.Pipeline;
using System.Buffers;
using System.Diagnostics;

namespace FatzBomb.MediaControl.Controllers;

public sealed class MediaPresentationCoordinator : IMediaPresentationCoordinator
{
    private enum CompositionPresentResult
    {
        Failed,
        Presented,
        Deferred
    }

    private const int CdgPacketsPerSector = 4;
    private const int CdgPresentationCadenceHz = CdgConstants.PacketsPerSecond / CdgPacketsPerSector;
    private const int SubtitleCanvasWidth = 1920;
    private const int SubtitleCanvasHeight = 1080;
    private static readonly TimeSpan CdgPresentationInterval = TimeSpan.FromSeconds(1.0 / CdgPresentationCadenceHz);

    private const int DeviceLostRecoveryMaxAttempts = 3;
    private static readonly TimeSpan[] DeviceLostRecoveryBackoffDelays =
    [
        TimeSpan.FromMilliseconds(250),
        TimeSpan.FromMilliseconds(500),
        TimeSpan.FromSeconds(1),
    ];

    private static readonly ArrayPool<byte> CompositionBackgroundPool =
        ArrayPool<byte>.Create(maxArrayLength: 64 * 1024 * 1024, maxArraysPerBucket: 4);

    private readonly IMediaEngine _mediaEngine;
    private readonly MediaPlayerConfiguration _configuration;
    private readonly RenderPipeline _renderPipeline;
    private readonly IMediaRendererBackend _rendererBackend;
    private readonly ISubtitleRasterizer _subtitleRasterizer;
    private readonly TimeSpan _wipeTransitionDuration;
    private readonly PlaybackScheduler _presentationScheduler = new("CoreNativePresentation");
    private readonly PlaybackScheduler _recoveryScheduler = new("DeviceLostRecovery");
    private readonly IPrecisionWaiterFactory _precisionWaiterFactory;
    private readonly Dictionary<string, MediaPresentationSurfaceDescriptor> _surfaces = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _lock = new();
    private readonly Lock _cdgSnapshotGate = new();
    private readonly Lock _subtitleGate = new();
    private readonly CdgPresentationRequestQueue _cdgPresentationQueue = new();

    private IVideoEngine? _attachedVideo;
    // Read from the video presentation thread through _primaryRefreshRateSource.
    private MediaPresentationSurfaceDescriptor? _primarySurface;
    // Attached non-primary surfaces (secondary/projector windows); guarded by _lock.
    private int _secondarySurfaceCount;
    private readonly Func<double?> _primaryRefreshRateSource;
    private readonly PresentationTimingSource _presentationTimingSource;
    // Attached surfaces, republished on attach/resize/detach, for the timing source: it runs
    // on the video presentation thread once per display refresh and must not take _lock.
    private MediaPresentationSurfaceDescriptor[] _surfaceSnapshot = [];
    // How surfaces group by display; rebuilt only when the surfaces or their displays change.
    // Presentation thread only.
    private TimingLayout? _timingLayout;
    private PresentationVsyncTiming[] _scratchSurfaceTimings = [];
    // The targets of the video frame being presented on this thread (Stage 017A slice 5).
    [ThreadStatic]
    private static MediaCompositionTargetFilter? t_videoFrameTargets;
    private ICdgEngine? _attachedCdg;
    private bool _isRenderingActive;
    private bool _disposed;
    private int _recoveryInProgress;
    private ReadOnlyMemory<byte> _compositionBackgroundBgra = ReadOnlyMemory<byte>.Empty;
    private RetainedFrameBuffer? _compositionBackgroundBuffer;
    private int _compositionBackgroundWidth;
    private int _compositionBackgroundHeight;
    private int _compositionBackgroundStride;
    private MediaCompositionStretchMode _compositionBackgroundStretchMode = MediaCompositionStretchMode.UniformFit;
    private bool _compositionBackgroundIsRetainedGpu;
    private long _compositionBackgroundRevision;
    private MediaCompositionStretchMode _primaryStretchMode = MediaCompositionStretchMode.UniformFit;
    private BaseVisualTransform _baseVisualTransform;
    private CdgRenderSnapshot? _lastCdgSnapshot;
    private SubtitleRasterResult? _subtitleOverlay;
    private string? _subtitleText;
    private int _subtitleProgressRevision = int.MinValue;
    private long _subtitleRevision;
    private double _lastSubtitleOffsetSeconds = double.NaN;

    public MediaPresentationCoordinator(
        IMediaRendererBackend rendererBackend,
        ISubtitleRasterizer subtitleRasterizer,
        IMediaEngine mediaEngine,
        MediaPlayerConfiguration configuration,
        IPrecisionWaiterFactory precisionWaiterFactory)
    {
        _rendererBackend = rendererBackend ?? throw new ArgumentNullException(nameof(rendererBackend));
        _subtitleRasterizer = subtitleRasterizer ?? throw new ArgumentNullException(nameof(subtitleRasterizer));
        _mediaEngine = mediaEngine ?? throw new ArgumentNullException(nameof(mediaEngine));
        _precisionWaiterFactory = precisionWaiterFactory ?? throw new ArgumentNullException(nameof(precisionWaiterFactory));
        _configuration = configuration;
        _renderPipeline = new RenderPipeline(_mediaEngine);
        _primaryRefreshRateSource = GetPrimaryRefreshRateHz;
        _presentationTimingSource = FillPresentationTimingGroups;
        _wipeTransitionDuration = TimeSpan.FromMilliseconds(configuration.KaraokeStyle.Normalize().WipeTransitionMilliseconds);
        _rendererBackend.FramePresented += OnFramePresented;
        DiagnosticRouter.DiagnosticOccurred += OnDiagnosticOccurred;

        _mediaEngine.ImageFrameReady += OnCoreImageFrameReady;
        _mediaEngine.BackgroundFrameReady += OnCoreBackgroundFrameReady;
        _mediaEngine.BackgroundCleared += OnCoreBackgroundCleared;

        AttachVideoIfAvailable();
        AttachCdgIfAvailable();
        AttachImageIfAvailable();
    }

    public event EventHandler? FramePresented;

    public string PresentationBackend => _rendererBackend.BackendName;

    public VideoDisplayMode VideoDisplayMode
    {
        get;
        set
        {
            if (field == value)
                return;

            field = value;
            _primaryStretchMode = ToCompositionStretchMode(value);
            _rendererBackend.SetPresentationStretch(_primaryStretchMode);
            PresentCurrentComposition("display mode changed");
        }
    } = VideoDisplayMode.Zoom;

    public BaseVisualTransform BaseVisualTransform
    {
        get
        {
            lock (_lock)
            {
                return _baseVisualTransform;
            }
        }
        set
        {
            lock (_lock)
            {
                if (_baseVisualTransform == value)
                {
                    return;
                }

                _baseVisualTransform = value;
            }

            PresentCurrentComposition("base visual transform changed");
        }
    }

    public bool AttachPresentationSurface(MediaPresentationSurfaceDescriptor surface)
    {
        if (_disposed || surface is null || !surface.IsValid)
            return false;

        bool attached;
        lock (_lock)
        {
            if (!EnsureBackendInitialized())
                return false;

            attached = AttachSurfaceNoLock(surface);
            if (attached)
            {
                _surfaces[surface.Id] = surface;
                TrackPrimarySurface(surface);
                UpdateSecondarySurfaceCountNoLock();
                PublishSurfaceSnapshotNoLock();
            }
        }

        if (attached)
            PresentCurrentComposition("presentation surface attached");

        return attached;
    }

    public bool ResizePresentationSurface(string surfaceId, int pixelWidth, int pixelHeight)
    {
        if (_disposed || string.IsNullOrWhiteSpace(surfaceId) || pixelWidth <= 0 || pixelHeight <= 0)
            return false;

        bool resized;
        bool presentAfterResize;
        lock (_lock)
        {
            if (!_surfaces.TryGetValue(surfaceId, out MediaPresentationSurfaceDescriptor? surface))
                return false;

            if (surface.PixelWidth == pixelWidth && surface.PixelHeight == pixelHeight)
                return true;

            surface = surface.WithSize(pixelWidth, pixelHeight);
            _surfaces[surface.Id] = surface;
            TrackPrimarySurface(surface);
            PublishSurfaceSnapshotNoLock();

            if (!_rendererBackend.ResizeSurface(surface))
                return false;

            resized = true;
            presentAfterResize = surface.Role != MediaPresentationSurfaceRole.Primary
                || ShouldPresentImmediatelyAfterPrimaryResize();
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

            if (!_rendererBackend.SetSurfaceLiveResize(surface, isResizing))
                return false;

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

            UpdateSecondarySurfaceCountNoLock();
            PublishSurfaceSnapshotNoLock();

            // Only the tracked primary: a host may attach a new primary before detaching the old.
            if (Volatile.Read(ref _primarySurface)?.Id == surface.Id)
                Volatile.Write(ref _primarySurface, null);

            if (!_rendererBackend.DetachSurface(surface))
                return false;

            if (surface.Role == MediaPresentationSurfaceRole.Primary)
            {
                _ = EnsureBackendInitialized();
                RecreateAttachedSurfacesNoLock();
            }

            return true;
        }
    }

    public void RefreshCurrentPosition()
    {
        _renderPipeline.RenderCdgAtCurrentPosition(force: true);

        // A plain video is presented by PresentCurrentComposition below (it presents the frame for
        // the new position, or reissues the current one); rendering it here too presented it twice.
        if (!IsPlainVideoComposition())
            _renderPipeline.RenderVideoAtCurrentPosition(allowWhilePlaying: true);

        // Audio with no picture, lyrics, subtitles, or background has nothing to redraw at the new
        // position; presenting an empty frame on every seek only produced failed presents.
        if (!HasCurrentVisualContent())
            return;

        if (_attachedVideo == null && _attachedCdg == null && HasSubtitleContent)
            _ = BuildAndPresentSoftwareComposition(0, retainedRefresh: false, "current position refreshed");
        else
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

        if (_attachedCdg != null || (HasSubtitleContent && _attachedVideo == null))
            _presentationScheduler.Start(RenderLoop);

        if (HasCurrentVisualContent())
            PresentCurrentComposition("presentation started");
    }

    public void StopRendering()
    {
        _isRenderingActive = false;
        _presentationScheduler.Stop();
        if (_mediaEngine.State is MediaPlayerState.Stopped or MediaPlayerState.Null)
        {
            ClearVisibleComposition("presentation stopped", preserveBackground: true);
            return;
        }

        _ = PresentPendingCdgFrame(deferIfCompositionBusy: false);
    }

    internal void RefreshAttachments()
    {
        if (_configuration.EnableLogging)
            Debug.WriteLine("[MediaPresentationCoordinator] Refreshing Core presentation attachments");

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
        using var pacer = new MediaLoopPacer(_precisionWaiterFactory);
        while (!cancellationToken.IsCancellationRequested)
        {
            if (_attachedCdg != null)
            {
                _renderPipeline.RenderCdgAtCurrentPosition();
                _ = PresentPendingCdgFrame(deferIfCompositionBusy: true);
            }
            else if (HasSubtitleContent && _attachedVideo == null)
            {
                if (UpdateSubtitleOverlay())
                {
                    _ = BuildAndPresentSoftwareComposition(
                        0,
                        retainedRefresh: false,
                        "audio subtitle refresh",
                        updateSubtitleOverlay: false);
                }
            }

            pacer.WaitNext(
                CdgPresentationInterval,
                cancellationToken.WaitHandle,
                lateYield: TimeSpan.FromMilliseconds(1));
        }
    }

    private bool AttachSurfaceNoLock(MediaPresentationSurfaceDescriptor surface) =>
        _rendererBackend.AttachSurface(surface);

    private bool EnsureBackendInitialized()
    {
        if (_rendererBackend.IsInitialized)
            return true;

        var initialized = _rendererBackend.Initialize();
        if (initialized)
            RecreateAttachedSurfacesNoLock();

        return initialized;
    }

    private bool EnsureNativeFrameDevice(VideoFrameEventArgs frame)
    {
        if (!_rendererBackend.EnsureNativeFrameDevice(frame, out var deviceAdopted))
            return false;

        if (deviceAdopted)
        {
            Debug.Assert(_rendererBackend.NativeDeviceHandle == frame.NativeResource?.Device.DeviceHandle,
                "EnsureNativeFrameDevice must adopt the decoder's device to preserve the shared-context hardware frame safety invariant.");
            RecreateAttachedSurfacesNoLock();
        }

        return true;
    }

    private void RecreateAttachedSurfacesNoLock()
    {
        foreach (MediaPresentationSurfaceDescriptor surface in _surfaces.Values)
            _ = AttachSurfaceNoLock(surface);
    }

    private void OnDiagnosticOccurred(MediaDiagnosticInfo info)
    {
        if (_disposed
            || info.Level != MediaDiagnosticLevel.Error
            || !string.Equals(info.Category, _rendererBackend.DeviceLostDiagnosticCategory, StringComparison.Ordinal))
            return;

        if (Interlocked.CompareExchange(ref _recoveryInProgress, 1, 0) == 0)
            _recoveryScheduler.Start(RunDeviceLostRecovery);
    }

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

                    _rendererBackend.CleanupResources();
                    recovered = EnsureBackendInitialized();
                    RecreateAttachedSurfacesNoLock();
                }

                if (recovered)
                {
                    DiagnosticRouter.Raise(MediaDiagnosticLevel.Info, _rendererBackend.DeviceLostDiagnosticCategory, $"GPU device recovered after {attempt} attempt(s).");
                    break;
                }

                DiagnosticRouter.Raise(MediaDiagnosticLevel.Warning, _rendererBackend.DeviceLostDiagnosticCategory, $"Device-lost recovery attempt {attempt}/{DeviceLostRecoveryMaxAttempts} failed.");
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
                    _rendererBackend.DeviceLostRecoveryFailedDiagnosticCategory,
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
                (_attachedVideo as IRendererRefreshRateSink)?.SetRendererRefreshRateSource(null);
                (_attachedVideo as IPresentationTimingSink)?.SetPresentationTimingSource(null);
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
        (_attachedVideo as IRendererRefreshRateSink)?.SetRendererRefreshRateSource(_primaryRefreshRateSource);
        (_attachedVideo as IPresentationTimingSink)?.SetPresentationTimingSource(_presentationTimingSource);
    }

    private void UpdateSecondarySurfaceCountNoLock() =>
        _secondarySurfaceCount = _surfaces.Values.Count(static surface => surface.Role != MediaPresentationSurfaceRole.Primary);

    private void TrackPrimarySurface(MediaPresentationSurfaceDescriptor surface)
    {
        if (surface.Role == MediaPresentationSurfaceRole.Primary)
            Volatile.Write(ref _primarySurface, surface);
    }

    /// <summary>
    /// Refresh rate of the monitor showing the primary surface, as the renderer reports it
    /// (Stage 017A slice 3). Called from the video presentation thread; renderers cache it.
    /// </summary>
    private double? GetPrimaryRefreshRateHz()
    {
        if (_disposed || Volatile.Read(ref _primarySurface) is not { } surface)
            return null;

        try
        {
            return _rendererBackend.GetSurfaceRefreshRateHz(surface);
        }
        catch (Exception ex)
        {
            // Timing falls back to the platform rate; a renderer bug must not stop playback.
            Debug.WriteLine($"[MediaPresentationCoordinator] Refresh rate query failed: {ex.Message}");
            return null;
        }
    }

    private void PublishSurfaceSnapshotNoLock() => Volatile.Write(ref _surfaceSnapshot, [.. _surfaces.Values]);

    /// <summary>
    /// The displays showing the attached surfaces and their vertical-blank timing, for
    /// display-locked video timing (Stage 017A slices 4 and 5). Surfaces on one monitor form one
    /// group; a surface the renderer has no timing for joins the primary's group. Nothing is
    /// reported while the primary has no timing. Called from the video presentation thread once
    /// per display refresh; renderers return cached timing.
    /// </summary>
    private void FillPresentationTimingGroups(List<PresentationTimingGroup> destination)
    {
        if (_disposed || Volatile.Read(ref _primarySurface) is not { } primary)
            return;

        MediaPresentationSurfaceDescriptor[] surfaces = Volatile.Read(ref _surfaceSnapshot);
        if (_scratchSurfaceTimings.Length < surfaces.Length)
            _scratchSurfaceTimings = new PresentationVsyncTiming[surfaces.Length];

        PresentationVsyncTiming[] timings = _scratchSurfaceTimings;
        var primaryIndex = -1;
        try
        {
            for (var index = 0; index < surfaces.Length; index++)
            {
                if (!_rendererBackend.TryGetSurfaceVsyncTiming(surfaces[index], out timings[index]) || !timings[index].IsValid)
                    timings[index] = default;

                if (string.Equals(surfaces[index].Id, primary.Id, StringComparison.OrdinalIgnoreCase))
                    primaryIndex = index;
            }
        }
        catch (Exception ex)
        {
            // Timing falls back to source cadence; a renderer bug must not stop playback.
            Debug.WriteLine($"[MediaPresentationCoordinator] Vertical-blank timing query failed: {ex.Message}");
            return;
        }

        if (primaryIndex < 0 || !timings[primaryIndex].IsValid)
            return;

        TimingLayout layout = GetTimingLayout(surfaces, timings, primaryIndex);
        foreach (TimingLayout.Group group in layout.Groups)
            destination.Add(new PresentationTimingGroup(timings[group.TimingSurfaceIndex], group.IncludesPrimary, group.SurfaceIds, group.Targets));
    }

    private TimingLayout GetTimingLayout(MediaPresentationSurfaceDescriptor[] surfaces, PresentationVsyncTiming[] timings, int primaryIndex)
    {
        TimingLayout? layout = _timingLayout;
        if (layout is not null && layout.Matches(surfaces, timings, primaryIndex))
            return layout;

        layout = TimingLayout.Create(surfaces, timings, primaryIndex);
        _timingLayout = layout;
        return layout;
    }

    /// <summary>The attached surfaces grouped by the display showing them.</summary>
    private sealed class TimingLayout
    {
        private readonly MediaPresentationSurfaceDescriptor[] _surfaces;
        private readonly long[] _displayKeys;
        private readonly int _primaryIndex;

        private TimingLayout(MediaPresentationSurfaceDescriptor[] surfaces, long[] displayKeys, int primaryIndex, Group[] groups)
        {
            _surfaces = surfaces;
            _displayKeys = displayKeys;
            _primaryIndex = primaryIndex;
            Groups = groups;
        }

        public Group[] Groups { get; }

        public bool Matches(MediaPresentationSurfaceDescriptor[] surfaces, PresentationVsyncTiming[] timings, int primaryIndex)
        {
            if (!ReferenceEquals(surfaces, _surfaces) || primaryIndex != _primaryIndex)
                return false;

            for (var index = 0; index < surfaces.Length; index++)
                if (timings[index].DisplayKey != _displayKeys[index] || timings[index].IsValid != (_displayKeys[index] != 0))
                    return false;

            return true;
        }

        public static TimingLayout Create(MediaPresentationSurfaceDescriptor[] surfaces, PresentationVsyncTiming[] timings, int primaryIndex)
        {
            var keys = new long[surfaces.Length];
            for (var index = 0; index < surfaces.Length; index++)
                keys[index] = timings[index].IsValid ? timings[index].DisplayKey : 0;

            // The primary's display first; a surface without timing follows the primary.
            var primaryKey = keys[primaryIndex];
            var order = new List<long> { primaryKey };
            foreach (var key in keys)
                if (key != 0 && !order.Contains(key))
                    order.Add(key);

            var groups = new Group[order.Count];
            for (var groupIndex = 0; groupIndex < order.Count; groupIndex++)
            {
                var key = order[groupIndex];
                var members = Enumerable.Range(0, surfaces.Length)
                    .Where(index => keys[index] == key || (key == primaryKey && keys[index] == 0))
                    .ToArray();
                var includesPrimary = members.Contains(primaryIndex);
                MediaCompositionTargetFilter? targets = order.Count == 1
                    ? null
                    : new MediaCompositionTargetFilter(
                        includesPrimary,
                        members.Where(index => index != primaryIndex).Select(index => surfaces[index].Id));
                var timingIndex = includesPrimary ? primaryIndex : members.First(index => keys[index] == key);
                groups[groupIndex] = new Group(includesPrimary, [.. members.Select(index => surfaces[index].Id)], targets, timingIndex);
            }

            return new TimingLayout(surfaces, keys, primaryIndex, groups);
        }

        public sealed record Group(bool IncludesPrimary, string[] SurfaceIds, MediaCompositionTargetFilter? Targets, int TimingSurfaceIndex);
    }

    // During a frame raised for some displays only, every composition built on this thread goes
    // to those displays' targets.
    private static MediaCompositionFrame ForVideoFrameTargets(MediaCompositionFrame frame) =>
        t_videoFrameTargets is { } targets && frame.Targets is null ? frame with { Targets = targets } : frame;

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
            _cdgPresentationQueue.Clear();
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

        if (IsHardwareFrame(e))
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
        t_videoFrameTargets = e.Targets;
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
        finally
        {
            t_videoFrameTargets = null;
        }
    }

    private void PresentPrimaryVideoFrame(VideoFrameEventArgs e)
    {
        if (IsValidBgraFrame(e))
        {
            SetCompositionLayer0Frame(e, _primaryStretchMode);
            _ = BuildAndPresentSoftwareComposition(0, retainedRefresh: false, "primary video frame");
            return;
        }

        if (!IsHardwareFrame(e))
            return;

        if (HasSubtitleContent)
        {
            _ = UpdateSubtitleOverlay();
            if (HasCachedSubtitleOverlay())
            {
                RenderHardwareVideoCompositionFrame(e, forceZoomToFill: false, "hardware video + subtitle");
                return;
            }
        }

        if (!BaseVisualTransform.IsIdentity)
        {
            RenderHardwareVideoCompositionFrame(e, forceZoomToFill: false, "hardware video + base visual transform");
            return;
        }

        // The native-frame path presents to the primary target only. With a secondary or
        // projector window attached, compose instead: composition fans the frame out to every
        // target. Plain hardware video otherwise never reached the secondary window (it stayed
        // blank, or froze on the last composed frame after a seek; ENG-015). Checked under the
        // lock that attach and detach update it under, so no frame takes the stale path.
        lock (_lock)
        {
            if (_secondarySurfaceCount == 0)
            {
                if (EnsureNativeFrameDevice(e))
                    _ = _rendererBackend.PresentNativeFrame(e);

                return;
            }
        }

        RenderHardwareVideoCompositionFrame(e, forceZoomToFill: false, "hardware video + secondary target");
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

        if (IsHardwareFrame(e))
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
        _ = _cdgPresentationQueue.TryDequeue(out var consumeDirtyRegions, out _, out _);
        _ = RenderCurrentCdgFrame(consumeDirtyRegions, retainedRefresh: true, "retained cdg refresh");
    }

    private void RequestCdgPresentation(bool consumeDirtyRegions, bool retainedRefresh, string reason)
    {
        _cdgPresentationQueue.Enqueue(consumeDirtyRegions, retainedRefresh, reason);

        if (!_isRenderingActive)
            _ = PresentPendingCdgFrame(deferIfCompositionBusy: false);
    }

    private void RequestCdgPresentationIfVisible(bool consumeDirtyRegions, bool retainedRefresh, string reason)
    {
        if (!ShouldPresentVisualLayers)
        {
            _cdgPresentationQueue.Clear();
            return;
        }

        RequestCdgPresentation(consumeDirtyRegions, retainedRefresh, reason);
    }

    private bool PresentPendingCdgFrame(bool deferIfCompositionBusy)
    {
        if (!_cdgPresentationQueue.TryDequeue(out var consumeDirtyRegions, out var retainedRefresh, out var reason))
            return false;

        CompositionPresentResult result = RenderCurrentCdgFrame(consumeDirtyRegions, retainedRefresh, reason, deferIfCompositionBusy);
        if (result == CompositionPresentResult.Deferred)
            _cdgPresentationQueue.Enqueue(consumeDirtyRegions, retainedRefresh, reason);

        return result == CompositionPresentResult.Presented;
    }

    private CompositionPresentResult RenderCurrentCdgFrame(
        bool consumeDirtyRegions,
        bool retainedRefresh,
        string reason,
        bool deferIfCompositionBusy = false)
    {
        _ = UpdateSubtitleOverlay();
        MediaCompositionFrame frame;
        RetainedFrameBuffer? rentedBackground;
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

        return buildResult == CompositionPresentResult.Presented
            ? PresentSoftwareComposition(frame, rentedBackground)
            : buildResult;
    }

    private CompositionPresentResult TryBuildCdgCompositionFrameNoLock(
        bool consumeDirtyRegions,
        bool retainedRefresh,
        string reason,
        out MediaCompositionFrame frame,
        out RetainedFrameBuffer? rentedBackground)
    {
        frame = default;
        rentedBackground = null;

        if (!TryCreateAndCacheCdgSnapshot(consumeDirtyRegions, out CdgRenderSnapshot? snapshot) || snapshot == null)
            return CompositionPresentResult.Failed;

        if (HasCompositionBackgroundNoLock() && !IsCdgSnapshotReadyForBackgroundPresentation(snapshot))
            return CompositionPresentResult.Deferred;

        if (!EnsureBackendInitialized())
            return CompositionPresentResult.Failed;

        frame = BuildCompositionFrameNoLock(snapshot, snapshot.BackgroundWashArgb, retainedRefresh, reason, out rentedBackground);
        return CompositionPresentResult.Presented;
    }

    private void RenderHardwareVideoCompositionFrame(VideoFrameEventArgs frame, bool forceZoomToFill, string reason)
    {
        _ = UpdateSubtitleOverlay();
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
            if (!EnsureNativeFrameDevice(frame))
                return;

            MediaCompositionStretchMode stretch = forceZoomToFill ? MediaCompositionStretchMode.UniformToFill : _primaryStretchMode;
            var backgroundRevision = SetRetainedGpuBackgroundNoLock(frame, stretch);
            SubtitleRasterResult? subtitleOverlay = GetCachedSubtitleOverlay(out var subtitleRevision);

            compositionFrame = new MediaCompositionFrame
            {
                ClearMode = MediaCompositionClearMode.Black,
                ClearArgb = 0xFF000000,
                BackgroundNativeResource = frame.NativeResource,
                BackgroundNativeWidth = frame.Width,
                BackgroundNativeHeight = frame.Height,
                BackgroundNativeFramesPerSecond = frame.FramesPerSecond,
                BackgroundWidth = frame.Width,
                BackgroundHeight = frame.Height,
                BackgroundStride = frame.Width * 4,
                BackgroundStretchMode = stretch,
                BackgroundUniformToFill = stretch == MediaCompositionStretchMode.UniformToFill,
                BackgroundRevision = backgroundRevision,
                BaseVisualTransform = _baseVisualTransform,
                WashArgb = washArgb,
                CdgOverlay = snapshot,
                SubtitleBgra = subtitleOverlay?.Pixels ?? ReadOnlyMemory<byte>.Empty,
                SubtitleWidth = subtitleOverlay?.Width ?? 0,
                SubtitleHeight = subtitleOverlay?.Height ?? 0,
                SubtitleStride = subtitleOverlay?.Stride ?? 0,
                SubtitleCanvasLeft = subtitleOverlay?.CanvasLeft ?? 0,
                SubtitleCanvasTop = subtitleOverlay?.CanvasTop ?? 0,
                SubtitleCanvasWidth = subtitleOverlay?.CanvasWidth ?? 0,
                SubtitleCanvasHeight = subtitleOverlay?.CanvasHeight ?? 0,
                SubtitleRevision = subtitleRevision,
                Reason = reason
            };
        }

        _ = PresentMediaComposition(compositionFrame);
    }

    private void StoreRetainedHardwareBackgroundFrame(VideoFrameEventArgs frame, MediaCompositionStretchMode stretch)
    {
        if (!IsHardwareFrame(frame))
            return;

        lock (_lock)
        {
            if (!EnsureNativeFrameDevice(frame))
                return;

            _ = SetRetainedGpuBackgroundNoLock(frame, stretch);
        }
    }

    /// <summary>
    /// Makes the renderer-retained hardware frame the background and returns its revision. The
    /// pooled CPU frame buffer is returned: hardware frames never use it.
    /// </summary>
    private long SetRetainedGpuBackgroundNoLock(VideoFrameEventArgs frame, MediaCompositionStretchMode stretch)
    {
        _compositionBackgroundBgra = ReadOnlyMemory<byte>.Empty;
        _compositionBackgroundBuffer?.Release();
        _compositionBackgroundBuffer = null;
        _compositionBackgroundWidth = frame.Width;
        _compositionBackgroundHeight = frame.Height;
        _compositionBackgroundStride = frame.Width * 4;
        _compositionBackgroundStretchMode = stretch;
        _compositionBackgroundIsRetainedGpu = true;
        return ++_compositionBackgroundRevision;
    }

    private bool TryGetCdgSnapshotForVideoComposition(out CdgRenderSnapshot? snapshot)
    {
        snapshot = null;
        if (!HasCdgContent)
            return false;

        var hadPending = _cdgPresentationQueue.TryDequeue(
            out var consumeDirtyRegions,
            out var retainedRefresh,
            out var pendingReason);
        if (!hadPending)
        {
            lock (_cdgSnapshotGate)
                snapshot = _lastCdgSnapshot;

            if (snapshot != null && IsCdgSnapshotReadyForBackgroundPresentation(snapshot))
                return true;
        }

        if (TryCreateAndCacheCdgSnapshot(consumeDirtyRegions, out snapshot)
            && snapshot != null
            && IsCdgSnapshotReadyForBackgroundPresentation(snapshot))
            return true;

        if (hadPending)
            _cdgPresentationQueue.Enqueue(consumeDirtyRegions, retainedRefresh, pendingReason);

        return false;
    }

    private bool HasReadyCdgSnapshotForBackgroundPresentation()
    {
        lock (_cdgSnapshotGate)
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

    private bool HasCompositionBackground()
    {
        lock (_lock)
            return HasCompositionBackgroundNoLock();
    }

    private string DescribeBackendPresentFailure() =>
        _rendererBackend.LastPresentFailureReason is { Length: > 0 } reason ? $" ({reason})" : string.Empty;

    private bool HasCurrentVisualContent() =>
        _mediaEngine.Content.HasFlag(MediaContent.Image)
        || _mediaEngine.Content.HasFlag(MediaContent.Video)
        || HasCdgContent
        || HasSubtitleContent
        || HasCompositionBackground();

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
        lock (_cdgSnapshotGate)
            _lastCdgSnapshot = createdSnapshot;

        return true;
    }

    /// <summary>Whether <see cref="PresentCurrentComposition"/> presents through its video branch.</summary>
    private bool IsPlainVideoComposition() =>
        !HasCdgContent
        && !_mediaEngine.Content.HasFlag(MediaContent.Image)
        && _mediaEngine.Content.HasFlag(MediaContent.Video)
        && !(_attachedVideo == null && _attachedCdg == null && HasSubtitleContent);

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

        if (_mediaEngine.Content.HasFlag(MediaContent.Video))
        {
            _renderPipeline.RenderVideoAtCurrentPosition(allowWhilePlaying: true, forceReissue: true);
            return;
        }

        if (HasSubtitleContent || HasCompositionBackground())
        {
            _ = BuildAndPresentSoftwareComposition(0, retainedRefresh: true, reason);
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
        if (!EnsureBackendInitialized())
            return CompositionPresentResult.Failed;

        if (!_rendererBackend.PresentComposition(ForVideoFrameTargets(frame)))
        {
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Warning, "Composition", $"Composition present failed reason={frame.Reason}{DescribeBackendPresentFailure()}");
            return CompositionPresentResult.Failed;
        }

        return CompositionPresentResult.Presented;
    }

    private CompositionPresentResult BuildAndPresentSoftwareComposition(
        uint washArgb,
        bool retainedRefresh,
        string reason,
        double? subtitlePositionSeconds = null,
        bool updateSubtitleOverlay = true)
    {
        if (updateSubtitleOverlay)
            _ = UpdateSubtitleOverlay(subtitlePositionSeconds);
        MediaCompositionFrame frame;
        RetainedFrameBuffer? rentedBackground;
        lock (_lock)
        {
            if (!EnsureBackendInitialized())
                return CompositionPresentResult.Failed;

            frame = BuildCompositionFrameNoLock(null, washArgb, retainedRefresh, reason, out rentedBackground);
        }

        return PresentSoftwareComposition(frame, rentedBackground);
    }

    private CompositionPresentResult PresentSoftwareComposition(MediaCompositionFrame frame, RetainedFrameBuffer? rentedBackground)
    {
        try
        {
            if (!_rendererBackend.PresentComposition(ForVideoFrameTargets(frame)))
            {
                DiagnosticRouter.Raise(MediaDiagnosticLevel.Warning, "Composition", $"Composition present failed reason={frame.Reason}{DescribeBackendPresentFailure()}");
                return CompositionPresentResult.Failed;
            }

            return CompositionPresentResult.Presented;
        }
        finally
        {
            rentedBackground?.Release();
        }
    }

    private MediaCompositionFrame BuildCompositionFrameNoLock(
        CdgRenderSnapshot? cdgOverlay,
        uint washArgb,
        bool retainedRefresh,
        string reason,
        out RetainedFrameBuffer? rentedBackground)
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

        // The composition shares the retained pixels through a reference instead of copying
        // them; SetCompositionLayer0Frame never overwrites a buffer a composition still holds.
        ReadOnlyMemory<byte> ownedBackgroundBgra = ReadOnlyMemory<byte>.Empty;
        if (hasBackground && _compositionBackgroundBuffer is { } retainedBuffer)
        {
            rentedBackground = retainedBuffer.AddReference();
            ownedBackgroundBgra = backgroundBgra;
        }
        else
        {
            hasBackground = false;
        }

        SubtitleRasterResult? subtitleOverlay = GetCachedSubtitleOverlay(out var subtitleRevision);

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
            BaseVisualTransform = _baseVisualTransform,
            WashArgb = washArgb,
            CdgOverlay = cdgOverlay,
            SubtitleBgra = subtitleOverlay?.Pixels ?? ReadOnlyMemory<byte>.Empty,
            SubtitleWidth = subtitleOverlay?.Width ?? 0,
            SubtitleHeight = subtitleOverlay?.Height ?? 0,
            SubtitleStride = subtitleOverlay?.Stride ?? 0,
            SubtitleCanvasLeft = subtitleOverlay?.CanvasLeft ?? 0,
            SubtitleCanvasTop = subtitleOverlay?.CanvasTop ?? 0,
            SubtitleCanvasWidth = subtitleOverlay?.CanvasWidth ?? 0,
            SubtitleCanvasHeight = subtitleOverlay?.CanvasHeight ?? 0,
            SubtitleRevision = subtitleRevision,
            RetainedRefresh = retainedRefresh,
            Reason = reason
        };
    }

    private bool UpdateSubtitleOverlay(double? presentationPositionSeconds = null)
    {
        lock (_subtitleGate)
        {
            var subtitlePosition = presentationPositionSeconds is { } position && double.IsFinite(position) && position >= 0
                ? position
                : _mediaEngine.Position;
            var subtitleOffset = _mediaEngine.SubtitleOffsetSeconds;
            if (!double.IsFinite(_lastSubtitleOffsetSeconds) || Math.Abs(subtitleOffset - _lastSubtitleOffsetSeconds) > 0.0001)
                _lastSubtitleOffsetSeconds = subtitleOffset;

            subtitlePosition = Math.Max(0, subtitlePosition + subtitleOffset);

            IReadOnlyList<SubtitleCue> activeCues = _mediaEngine.Subtitle?.GetActiveCues(subtitlePosition) ?? [];
            var text = activeCues.Count == 0 ? null : string.Join("\n", activeCues.Select(static cue => cue.Text));
            KaraokeCueTiming? karaokeTiming = activeCues.Count == 1 ? activeCues[0].KaraokeTiming : null;
            var subtitleTime = TimeSpan.FromSeconds(subtitlePosition);
            var progressRevision = karaokeTiming?.GetProgressRevision(subtitleTime, _wipeTransitionDuration) ?? int.MinValue;
            if (string.Equals(text, _subtitleText, StringComparison.Ordinal)
                && progressRevision == _subtitleProgressRevision)
                return false;

            _subtitleText = text;
            _subtitleProgressRevision = progressRevision;
            _subtitleOverlay = null;
            _subtitleRevision++;
            if (string.IsNullOrWhiteSpace(text))
                return true;

            try
            {
                _subtitleOverlay = karaokeTiming == null
                    ? _subtitleRasterizer.Render(text, SubtitleCanvasWidth, SubtitleCanvasHeight)
                    : _subtitleRasterizer.RenderKaraoke(karaokeTiming, subtitleTime, SubtitleCanvasWidth, SubtitleCanvasHeight);
            }
            catch (Exception ex)
            {
                DiagnosticRouter.Raise(MediaDiagnosticLevel.Warning, "Subtitle", $"Text rasterization failed: {ex.Message}", innerException: ex);
            }

            return true;
        }
    }

    private bool HasCachedSubtitleOverlay()
    {
        lock (_subtitleGate)
            return _subtitleOverlay is { Pixels.IsEmpty: false };
    }

    private static bool IsHardwareFrame(VideoFrameEventArgs frame) =>
        frame.NativeResource is { IsValid: true };

    private SubtitleRasterResult? GetCachedSubtitleOverlay(out long revision)
    {
        lock (_subtitleGate)
        {
            if (_subtitleOverlay is not { Pixels.IsEmpty: false } overlay)
            {
                revision = -1;
                return null;
            }

            revision = _subtitleRevision;
            return overlay;
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
            // Reuse the retained buffer in place only when no in-flight composition references it;
            // otherwise retain into a fresh pooled buffer and let the last reader return the old one.
            RetainedFrameBuffer? buffer = _compositionBackgroundBuffer;
            if (buffer is null || !buffer.IsExclusive || buffer.Length != required)
            {
                buffer?.Release();
                buffer = RetainedFrameBuffer.Rent(CompositionBackgroundPool, required);
                _compositionBackgroundBuffer = buffer;
            }

            frame.PixelData.Span[..required].CopyTo(buffer.Memory.Span);
            _compositionBackgroundBgra = buffer.Memory;

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

        _rendererBackend.ClearCompositionBackgroundFrame();
    }

    private void ClearCompositionLayer0FrameNoLock()
    {
        _compositionBackgroundBgra = ReadOnlyMemory<byte>.Empty;
        _compositionBackgroundBuffer?.Release();
        _compositionBackgroundBuffer = null;
        _compositionBackgroundWidth = 0;
        _compositionBackgroundHeight = 0;
        _compositionBackgroundStride = 0;
        _compositionBackgroundStretchMode = MediaCompositionStretchMode.UniformFit;
        _compositionBackgroundIsRetainedGpu = false;
        _compositionBackgroundRevision++;
    }

    private void ClearAllContent() => ClearVisibleComposition("all content cleared");

    private void ClearVisibleComposition(string reason, bool preserveBackground = false)
    {
        _cdgPresentationQueue.Clear();
        lock (_cdgSnapshotGate)
            _lastCdgSnapshot = null;
        InvalidateSubtitleOverlayCache();

        lock (_lock)
        {
            if (!preserveBackground)
            {
                ClearCompositionLayer0FrameNoLock();
                _rendererBackend.ClearCompositionBackgroundFrame();
            }

            _ = PresentMediaCompositionNoLock(MediaCompositionFrame.Empty(
                MediaCompositionClearMode.Transparent,
                0x00000000,
                reason));
        }
    }

    private void InvalidateSubtitleOverlayCache()
    {
        lock (_subtitleGate)
        {
            _subtitleText = null;
            _subtitleProgressRevision = int.MinValue;
            _subtitleOverlay = null;
            _lastSubtitleOffsetSeconds = double.NaN;
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
                (_attachedVideo as IRendererRefreshRateSink)?.SetRendererRefreshRateSource(null);
                (_attachedVideo as IPresentationTimingSink)?.SetPresentationTimingSource(null);
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

        lock (_cdgSnapshotGate)
            _lastCdgSnapshot = null;
        _cdgPresentationQueue.Clear();

        _renderPipeline.Clear();
    }

    private bool HasCdgContent => _mediaEngine.Content.HasFlag(MediaContent.Cdg);

    private bool HasSubtitleContent => _mediaEngine.Content.HasFlag(MediaContent.Subtitle);

    private bool ShouldPresentVisualLayers =>
        _isRenderingActive
        || _mediaEngine.State is MediaPlayerState.Playing or MediaPlayerState.Paused
        || HasStoppedStandaloneVisualContent;

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

        _disposed = true;

        _isRenderingActive = false;
        _presentationScheduler.StopAndWait();

        _rendererBackend.FramePresented -= OnFramePresented;
        _mediaEngine.ImageFrameReady -= OnCoreImageFrameReady;
        _mediaEngine.BackgroundFrameReady -= OnCoreBackgroundFrameReady;
        _mediaEngine.BackgroundCleared -= OnCoreBackgroundCleared;
        DiagnosticRouter.DiagnosticOccurred -= OnDiagnosticOccurred;

        // Drain device-lost recovery before tearing down renderer resources so no recovery worker
        // is mid-backend-call while the backend is disposed. RunDeviceLostRecovery checks _disposed
        // at each _lock acquisition, so a still-running worker bails on the next gate.
        _recoveryScheduler.StopAndWait();
        _ = Interlocked.Exchange(ref _recoveryInProgress, 0);

        DetachAllEngines();
        lock (_lock)
        {
            _rendererBackend.CleanupResources();
            _rendererBackend.Dispose();
            ClearCompositionLayer0FrameNoLock();
        }
        _subtitleRasterizer.Dispose();
        _presentationScheduler.Dispose();
        _recoveryScheduler.Dispose();
    }
}
