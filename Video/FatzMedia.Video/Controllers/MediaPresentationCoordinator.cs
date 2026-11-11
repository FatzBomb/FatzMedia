using System.Buffers;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using FatzMedia.Video.Core.Abstractions;
using FatzMedia.Video.Core.Cdg;
using FatzMedia.Video.Core.Configuration;
using FatzMedia.Video.Core.Graphics;
using FatzMedia.Video.Core.Graphics.Composition;
using FatzMedia.Video.Core.Graphics.Rendering;
using FatzMedia.Video.Core.Subtitle;
using FatzMedia.Video.Core.Threading;
using FatzMedia.Video.Core.Video;
using FatzMedia.Video.Pipeline;

namespace FatzMedia.Video.Controllers;

public sealed partial class MediaPresentationCoordinator : IMediaPresentationCoordinator
{
    enum CompositionPresentResult
    {
        Failed,
        Presented,
        Deferred
    }

    const int CdgPacketsPerSector = 4;
    const int CdgPresentationCadenceHz = CdgConstants.PacketsPerSecond / CdgPacketsPerSector;
    const int SubtitleCanvasWidth = 1920;
    const int SubtitleCanvasHeight = 1080;
    static readonly TimeSpan CdgPresentationInterval = TimeSpan.FromSeconds(1.0 / CdgPresentationCadenceHz);

    static readonly ArrayPool<byte> CompositionBackgroundPool =
        ArrayPool<byte>.Create(maxArrayLength: 64 * 1024 * 1024, maxArraysPerBucket: 4);

    readonly IMediaEngine _mediaEngine;
    readonly MediaPlayerConfiguration _configuration;
    readonly RenderPipeline _renderPipeline;
    readonly IMediaRendererBackend _rendererBackend;
    readonly ISubtitleRasterizer _subtitleRasterizer;
    readonly TimeSpan _wipeTransitionDuration;
    readonly PlaybackScheduler _presentationScheduler = new("CoreNativePresentation");

    // Guards _isRenderLoopRunning with its scheduler start/stop: seek refreshes, StartRendering and
    // StopRendering run on different threads. PlaybackScheduler.Start/Stop never wait.
    readonly Lock _renderLoopGate = new();
    // Orders every present (ENG-023, ENG-039): taken before _lock and held from building a
    // composition to the renderer call, so an older composition is never presented after a newer
    // one. _lock guards the frame state only and is never held across a present, so a surface call
    // from the UI thread never waits for a present that is stuck in the renderer. Nothing may take
    // this gate while holding _lock, and nothing may render video or CD+G while holding it (their
    // engines raise frames that take it, on threads that hold the engines' own locks).
    readonly Lock _presentGate = new();
    readonly PlaybackScheduler _recoveryScheduler = new("DeviceLostRecovery");
    readonly IPrecisionWaiterFactory _precisionWaiterFactory;
    readonly Dictionary<string, MediaPresentationSurfaceDescriptor> _surfaces = new(StringComparer.OrdinalIgnoreCase);
    // A refused present is reported once per episode: a target going away (a surface unloading
    // while the engine still stops or clears) refuses every present until one succeeds again.
    readonly RendererDiagnosticLatch _presentFailure = new();
    readonly Lock _lock = new();
    readonly Lock _cdgSnapshotGate = new();
    readonly Lock _subtitleGate = new();
    readonly CdgPresentationRequestQueue _cdgPresentationQueue = new();

    IVideoEngine? _attachedVideo;
    // Read from the video presentation thread through _primaryRefreshRateSource.
    MediaPresentationSurfaceDescriptor? _primarySurface;
    // Attached non-primary surfaces (secondary/projector windows); guarded by _lock.
    int _secondarySurfaceCount;
    readonly Func<double?> _primaryRefreshRateSource;
    readonly PresentationTimingSource _presentationTimingSource;
    // Attached surfaces, republished on attach/resize/detach, for the timing source: it runs
    // on the video presentation thread once per display refresh and must not take _lock.
    MediaPresentationSurfaceDescriptor[] _surfaceSnapshot = [];
    // How surfaces group by display; rebuilt only when the surfaces or their displays change.
    // Presentation thread only.
    TimingLayout? _timingLayout;
    PresentationVsyncTiming[] _scratchSurfaceTimings = [];
    // The targets of the video frame being presented on this thread (Stage 017A slice 5).
    [ThreadStatic]
    static MediaCompositionTargetFilter? _videoFrameTargets;
    ICdgEngine? _attachedCdg;
    bool _isRenderingActive;
    bool _disposed;
    int _recoveryInProgress;
    ReadOnlyMemory<byte> _compositionBackgroundBgra = ReadOnlyMemory<byte>.Empty;
    RetainedFrameBuffer? _compositionBackgroundBuffer;
    int _compositionBackgroundWidth;
    int _compositionBackgroundHeight;
    int _compositionBackgroundStride;
    MediaCompositionStretchMode _compositionBackgroundStretchMode = MediaCompositionStretchMode.UniformFit;
    // A hardware (GPU) background frame: pending once handed to the renderer, presented once a
    // present carrying it succeeded. Only a presented one is held by the renderer, so only it may
    // be reused by a retained refresh (ENG-039).
    GpuBackground? _pendingGpuBackground;
    GpuBackground? _presentedGpuBackground;
    long _compositionBackgroundRevision;
    MediaCompositionStretchMode _primaryStretchMode = MediaCompositionStretchMode.UniformFit;
    // Replaced whole, read without a lock (surface calls and frame builders).
    volatile StrongBox<BaseVisualTransform> _baseVisualTransform = new(default);
    CdgRenderSnapshot? _lastCdgSnapshot;
    SubtitleRasterResult? _subtitleOverlay;
    // The pooled buffer the rasterizer writes into, and the one _subtitleOverlay's pixels lie in
    // (null when the rasterizer returned its own memory). Compositions take a reference to it.
    RetainedFrameBuffer? _subtitleBuffer;
    RetainedFrameBuffer? _subtitleOverlayBuffer;
    // Subtitle rasters (at most the 1920x1080 canvas) go here instead of a new array per frame. One pool per
    // coordinator: an engine that draws lyrics into a returned buffer draws only the change (IKaraokeOverlaySource),
    // so no other player may write into the arrays meanwhile.
    readonly ArrayPool<byte> _subtitleOverlayPool = ArrayPool<byte>.Create(maxArrayLength: 16 * 1024 * 1024, maxArraysPerBucket: 4);
    readonly Func<int, Memory<byte>> _rentSubtitlePixels;
    bool _subtitleBufferRented;
    SubtitleCue[] _subtitleCues = [];
    string? _subtitleText;
    int _subtitleProgressRevision = int.MinValue;
    long _subtitleRevision;
    double _lastSubtitleOffsetSeconds = double.NaN;
    // The player that built this coordinator (DiagnosticRouter.Owner at construction).
    readonly object? _diagnosticOwner;

    public MediaPresentationCoordinator(
        IMediaRendererBackend rendererBackend,
        ISubtitleRasterizer subtitleRasterizer,
        IMediaEngine mediaEngine,
        MediaPlayerConfiguration configuration,
        IPrecisionWaiterFactory precisionWaiterFactory)
        : this(rendererBackend, subtitleRasterizer, mediaEngine, configuration, precisionWaiterFactory, TimeProvider.System)
    {
    }

    /// <param name="rendererBackend">The renderer that presents the composition.</param>
    /// <param name="subtitleRasterizer">Draws subtitle text into pixels.</param>
    /// <param name="mediaEngine">The engine whose frames and position are presented.</param>
    /// <param name="configuration">The player's configuration.</param>
    /// <param name="precisionWaiterFactory">Creates the precise waiters used to pace presents.</param>
    /// <param name="timeProvider">Drives the device-health timer and the stall detector (ENG-039).</param>
    internal MediaPresentationCoordinator(
        IMediaRendererBackend rendererBackend,
        ISubtitleRasterizer subtitleRasterizer,
        IMediaEngine mediaEngine,
        MediaPlayerConfiguration configuration,
        IPrecisionWaiterFactory precisionWaiterFactory,
        TimeProvider timeProvider)
    {
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _rendererBackend = rendererBackend ?? throw new ArgumentNullException(nameof(rendererBackend));
        _subtitleRasterizer = subtitleRasterizer ?? throw new ArgumentNullException(nameof(subtitleRasterizer));
        _rentSubtitlePixels = RentSubtitlePixels;
        _mediaEngine = mediaEngine ?? throw new ArgumentNullException(nameof(mediaEngine));
        _precisionWaiterFactory = precisionWaiterFactory ?? throw new ArgumentNullException(nameof(precisionWaiterFactory));
        _configuration = configuration;
        _renderPipeline = new RenderPipeline(_mediaEngine);
        _primaryRefreshRateSource = GetPrimaryRefreshRateHz;
        _presentationTimingSource = FillPresentationTimingGroups;
        _wipeTransitionDuration = TimeSpan.FromMilliseconds(configuration.KaraokeStyle.Normalize().WipeTransitionMilliseconds);
        _rendererBackend.FramePresented += OnFramePresented;
        // A renderer that decodes on its own device (Stage 045A slice 2) supplies every decoder's
        // hardware device; any other renderer leaves the platform's.
        (_mediaEngine as IRendererVideoDecodeAttachment)?.AttachRendererVideoDecodeDevice(_rendererBackend as IMediaRendererVideoDecodeDevice);
        _diagnosticOwner = DiagnosticRouter.Owner;
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

    /// <summary>
    /// Stage 043A: where no layer draws, the primary composition clears to transparent instead of
    /// black, so an app layer below shows through on hosts that composite. Off by default.
    /// </summary>
    internal bool TransparentBackground
    {
        get => Volatile.Read(ref field);
        set => Volatile.Write(ref field, value);
    }

    /// <summary>The clear of a composition frame where no layer draws: black, or transparent with <paramref name="transparentBackground"/>.</summary>
    internal static (MediaCompositionClearMode Mode, uint Argb) ResolveContentClear(bool transparentBackground) =>
        transparentBackground ? (MediaCompositionClearMode.Transparent, 0x00000000u) : (MediaCompositionClearMode.Black, 0xFF000000u);

    /// <summary>
    /// Gets or sets how video fills the primary surface. Setting it does not present: the player
    /// refreshes the composition on its control queue (ENG-039), so the caller never waits for a
    /// present.
    /// </summary>
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
        }
    } = VideoDisplayMode.Zoom;

    /// <summary>
    /// Gets or sets the base layer's colour adjustments. Setting it does not present (see
    /// <see cref="VideoDisplayMode"/>); reading it takes no lock.
    /// </summary>
    public BaseVisualTransform BaseVisualTransform
    {
        get => _baseVisualTransform.Value;
        set => _baseVisualTransform = new StrongBox<BaseVisualTransform>(value);
    }

    public bool AttachPresentationSurface(MediaPresentationSurfaceDescriptor surface)
    {
        if (_disposed || surface is null || !surface.IsValid)
            return false;

        bool attached;
        lock (_lock)
        {
            if (!EnsureBackendInitialized(surfaceCall: true))
                return false;

            attached = AttachSurfaceNoLock(surface);
            if (attached)
            {
                _surfaces[surface.Id] = surface;
                // A new target starts a new episode: its first refused present is reported.
                _presentFailure.Reset();
                TrackPrimarySurface(surface);
                UpdateSecondarySurfaceCountNoLock();
                PublishSurfaceSnapshotNoLock();
            }
        }

        if (attached)
            PresentAfterSurfaceChange("presentation surface attached");
        else
            KickDeviceProbe(attachFailed: true);

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
            if (!_surfaces.TryGetValue(surfaceId, out var surface))
                return false;

            if (surface.PixelWidth == pixelWidth && surface.PixelHeight == pixelHeight)
                return true;

            surface = surface.WithSize(pixelWidth, pixelHeight);
            _surfaces[surface.Id] = surface;
            TrackPrimarySurface(surface);
            PublishSurfaceSnapshotNoLock();

            bool backendResized;
            using (BeginRendererCall(_surfaceCalls, "ResizeSurface"))
                backendResized = _rendererBackend.ResizeSurface(surface);

            if (!backendResized)
            {
                KickDeviceProbe();
                return false;
            }

            resized = true;
            presentAfterResize = surface.Role != MediaPresentationSurfaceRole.Primary
                || ShouldPresentImmediatelyAfterPrimaryResize();
        }

        if (resized && presentAfterResize)
        {
            PresentAfterSurfaceChange("presentation surface resized");
        }
        else if (resized && HasCdgContent)
        {
            // Playing CD+G presents only when its graphics change: redraw the whole picture at the new size now,
            // instead of leaving the old size in the corner until the next change.
            RequestCdgPresentation(consumeDirtyRegions: false, retainedRefresh: true, "presentation surface resized");
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
            if (!_surfaces.TryGetValue(surfaceId, out var surface))
                return false;

            if (surface.Role != MediaPresentationSurfaceRole.Primary)
                return true;

            bool changed;
            using (BeginRendererCall(_surfaceCalls, "SetSurfaceLiveResize"))
                changed = _rendererBackend.SetSurfaceLiveResize(surface, isResizing);

            if (!changed)
                return false;

            shouldRefreshAfterResize = !isResizing;
        }

        if (shouldRefreshAfterResize)
            PresentAfterSurfaceChange("presentation surface live resize ended");

        return true;
    }

    public bool DetachPresentationSurface(string surfaceId)
    {
        if (_disposed || string.IsNullOrWhiteSpace(surfaceId))
            return false;

        lock (_lock)
        {
            if (!_surfaces.Remove(surfaceId, out var surface))
                return false;

            UpdateSecondarySurfaceCountNoLock();
            PublishSurfaceSnapshotNoLock();

            // Only the tracked primary: a host may attach a new primary before detaching the old.
            if (Volatile.Read(ref _primarySurface)?.Id == surface.Id)
                Volatile.Write(ref _primarySurface, null);

            bool detached;
            using (BeginRendererCall(_surfaceCalls, "DetachSurface"))
                detached = _rendererBackend.DetachSurface(surface);

            if (!detached)
                return false;

            if (surface.Role == MediaPresentationSurfaceRole.Primary)
            {
                _ = EnsureBackendInitialized(surfaceCall: true);
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
        // The subtitle flag follows the selected track (ENG-022): selecting or deselecting one while
        // playing starts or stops the loop that updates the overlay.
        SyncRenderLoop();

        if (!HasCurrentVisualContent())
            return;

        if (_attachedVideo == null && _attachedCdg == null && HasSubtitleContent)
            _ = BuildAndPresentSoftwareComposition(0, retainedRefresh: false, "current position refreshed");
        else
            PresentCurrentComposition("current position refreshed");
    }

    void IMediaRenderCoordinator.StartRendering() => StartRendering();

    void IMediaRenderCoordinator.StopRendering() => StopRendering();

    void IMediaRenderCoordinator.RefreshAttachments() => RefreshAttachments();

    public void StartRendering()
    {
        if (_disposed || _isRenderingActive)
            return;

        _isRenderingActive = true;
        SyncRenderLoop();

        if (HasCurrentVisualContent())
            PresentCurrentComposition("presentation started");
    }

    internal bool IsRenderLoopRunning { get; private set; }

    bool NeedsRenderLoop => _attachedCdg != null || (HasSubtitleContent && _attachedVideo == null);

    void SyncRenderLoop()
    {
        if (!_isRenderingActive)
            return;

        var needed = NeedsRenderLoop;
        lock (_renderLoopGate)
        {
            if (!_isRenderingActive || needed == IsRenderLoopRunning)
                return;

            IsRenderLoopRunning = needed;
            if (needed)
                _presentationScheduler.Start(RenderLoop);
            else
                _presentationScheduler.Stop();
        }
    }

    public void StopRendering()
    {
        lock (_renderLoopGate)
        {
            _isRenderingActive = false;
            IsRenderLoopRunning = false;
            _presentationScheduler.Stop();
        }
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

    void RenderLoop(CancellationToken cancellationToken)
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

    bool AttachSurfaceNoLock(MediaPresentationSurfaceDescriptor surface)
    {
        bool attached;
        using (BeginRendererCall(_surfaceCalls, "AttachSurface"))
            attached = _rendererBackend.AttachSurface(surface);

        if (attached)
            return true;

        // A shared-GPU-image consumer (Stage 027A slice 2) learns that this renderer will not feed
        // it, also when the attach is a deferred one (device re-creation, a graph rebuilt by a
        // mode switch) that its adapter never sees fail. Renderers that understand the surface
        // kind report a specific reason first; consumers keep the first one.
        if (MediaSharedGpuImageConsumerHandle.TryResolve(surface, out var consumer))
        {
            consumer!.OnUnavailable(
                $"the {_rendererBackend.BackendName} renderer did not accept the shared GPU image surface '{surface.Id}'");
        }

        return false;
    }

    /// <param name="surfaceCall">
    /// A surface call or the recovery itself: it may create the device while a recovery runs or
    /// waits to retry. A present may not: it would re-create the device on the side, every frame
    /// (ENG-039).
    /// </param>
    bool EnsureBackendInitialized(bool surfaceCall = false)
    {
        if (_rendererBackend.IsInitialized)
            return true;

        if (!surfaceCall && IsRecoveringDevice)
            return false;

        bool initialized;
        using (BeginRendererCall(_surfaceCalls, "Initialize"))
            initialized = _rendererBackend.Initialize();

        if (initialized)
        {
            RecreateAttachedSurfacesNoLock();
            ArmDeviceHealthNoLock();
        }

        return initialized;
    }

    // Caller holds _presentGate, not _lock: adopting the decoder's device is a renderer call.
    bool EnsureNativeFrameDevice(VideoFrameEventArgs frame)
    {
        bool ensured;
        bool deviceAdopted;
        using (BeginRendererCall(_renderCalls, "EnsureNativeFrameDevice"))
            ensured = _rendererBackend.EnsureNativeFrameDevice(frame, out deviceAdopted);

        if (!ensured)
        {
            RequestSoftwareDecodeFallback(frame);
            return false;
        }

        if (deviceAdopted)
        {
            Debug.Assert(_rendererBackend.NativeDeviceHandle == frame.NativeResource?.Device.DeviceHandle,
                "EnsureNativeFrameDevice must adopt the decoder's device to preserve the shared-context hardware frame safety invariant.");
            lock (_lock)
                RecreateAttachedSurfacesNoLock();
        }

        return true;
    }

    /// <summary>
    /// ENG-037: the renderer cannot take this decoder's hardware frames, so a paused first frame
    /// would stay black. Unless the device is lost (recovery reopens the video) or nothing is
    /// shown, the decoder is asked to continue in software; it switches on its own worker.
    /// </summary>
    void RequestSoftwareDecodeFallback(VideoFrameEventArgs frame)
    {
        if (_disposed
            || IsRecoveringDevice
            || !HasAttachedSurfaces
            || frame.NativeResource?.DecodeFallback is not { IsRequested: false } fallback)
        {
            return;
        }

        fallback.RequestSoftwareDecoding($"the {_rendererBackend.BackendName} renderer cannot draw frames from the decoder's device");
    }

    void RecreateAttachedSurfacesNoLock()
    {
        foreach (var surface in _surfaces.Values)
            _ = AttachSurfaceNoLock(surface);
    }

    // The category match stays as a backup trigger for renderers that only raise their device
    // loss as a diagnostic (ENG-039); ProbeDeviceLost is the primary path.
    void OnDiagnosticOccurred(MediaDiagnosticInfo info)
    {
        // Only this coordinator's own device loss (another player's renderer has its own device).
        if (_disposed
            || info.Level != MediaDiagnosticLevel.Error
            || !string.Equals(info.Category, _rendererBackend.DeviceLostDiagnosticCategory, StringComparison.Ordinal)
            || !info.IsFor(_diagnosticOwner))
            return;

        RequestDeviceLostRecovery(info.Message);
    }

    void AttachVideoIfAvailable()
    {
        if (_attachedVideo != null)
        {
            try
            {
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
        _attachedVideo.FrameReady += OnVideoFrameReady;
        (_attachedVideo as IRendererRefreshRateSink)?.SetRendererRefreshRateSource(_primaryRefreshRateSource);
        (_attachedVideo as IPresentationTimingSink)?.SetPresentationTimingSource(_presentationTimingSource);
    }

    void UpdateSecondarySurfaceCountNoLock() =>
        _secondarySurfaceCount = _surfaces.Values.Count(static surface => surface.Role != MediaPresentationSurfaceRole.Primary);

    void TrackPrimarySurface(MediaPresentationSurfaceDescriptor surface)
    {
        if (surface.Role == MediaPresentationSurfaceRole.Primary)
            Volatile.Write(ref _primarySurface, surface);
    }

    /// <summary>
    /// Refresh rate of the monitor showing the primary surface, as the renderer reports it
    /// (Stage 017A slice 3). Called from the video presentation thread; renderers cache it.
    /// </summary>
    double? GetPrimaryRefreshRateHz()
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

    void PublishSurfaceSnapshotNoLock() => Volatile.Write(ref _surfaceSnapshot, [.. _surfaces.Values]);

    /// <summary>
    /// The displays showing the attached surfaces and their vertical-blank timing, for
    /// display-locked video timing (Stage 017A slices 4 and 5). Surfaces on one monitor form one
    /// group; a surface the renderer has no timing for joins the primary's group. Nothing is
    /// reported while the primary has no timing. Called from the video presentation thread once
    /// per display refresh; renderers return cached timing.
    /// </summary>
    void FillPresentationTimingGroups(List<PresentationTimingGroup> destination)
    {
        if (_disposed || Volatile.Read(ref _primarySurface) is not { } primary)
            return;

        var surfaces = Volatile.Read(ref _surfaceSnapshot);
        if (_scratchSurfaceTimings.Length < surfaces.Length)
            _scratchSurfaceTimings = new PresentationVsyncTiming[surfaces.Length];

        var timings = _scratchSurfaceTimings;
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

        var layout = GetTimingLayout(surfaces, timings, primaryIndex);
        foreach (var group in layout.Groups)
            destination.Add(new PresentationTimingGroup(timings[group.TimingSurfaceIndex], group.IncludesPrimary, group.SurfaceIds, group.Targets));
    }

    TimingLayout GetTimingLayout(MediaPresentationSurfaceDescriptor[] surfaces, PresentationVsyncTiming[] timings, int primaryIndex)
    {
        var layout = _timingLayout;
        if (layout is not null && layout.Matches(surfaces, timings, primaryIndex))
            return layout;

        layout = TimingLayout.Create(surfaces, timings, primaryIndex);
        _timingLayout = layout;
        return layout;
    }

    /// <summary>The attached surfaces grouped by the display showing them.</summary>
    sealed class TimingLayout
    {
        readonly MediaPresentationSurfaceDescriptor[] _surfaces;
        readonly long[] _displayKeys;
        readonly int _primaryIndex;

        TimingLayout(MediaPresentationSurfaceDescriptor[] surfaces, long[] displayKeys, int primaryIndex, Group[] groups)
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
                var targets = order.Count == 1
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
    static MediaCompositionFrame ForVideoFrameTargets(MediaCompositionFrame frame) =>
        _videoFrameTargets is { } targets && frame.Targets is null ? frame with { Targets = targets } : frame;

    void AttachCdgIfAvailable()
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

    void AttachImageIfAvailable()
    {
        if (!_mediaEngine.Content.HasFlag(MediaContent.Image))
            return;

        if (_mediaEngine.TryGetCurrentImageFrame(out var frame) && frame != null)
            PresentCoreImageFrame(frame);
    }

    void OnCoreImageFrameReady(object? sender, VideoFrameEventArgs e) => PresentCoreImageFrame(e);

    void PresentCoreImageFrame(VideoFrameEventArgs e)
    {
        if (!IsValidBgraFrame(e))
            return;

        SetCompositionLayer0Frame(e, _primaryStretchMode);
        _ = BuildAndPresentSoftwareComposition(0, retainedRefresh: false, "core image frame");
    }

    void OnCoreBackgroundFrameReady(object? sender, VideoFrameEventArgs e)
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
            // It still refers to the lost device (ENG-039).
            if (IsRecoveringDevice)
                return;

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

    void OnCoreBackgroundCleared(object? sender, EventArgs e)
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

    void OnVideoFrameReady(object? sender, VideoFrameEventArgs e)
    {
        // A hardware frame still refers to the lost device (ENG-039).
        if (IsRecoveringDevice && IsHardwareFrame(e))
            return;

        _videoFrameTargets = e.Targets;
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
            _videoFrameTargets = null;
        }
    }

    void PresentPrimaryVideoFrame(VideoFrameEventArgs e)
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
        // blank, or froze on the last composed frame after a seek; ENG-015). Read under the lock
        // that attach and detach update it under; a window attached during this one present gets
        // the next frame.
        lock (_presentGate)
        {
            int secondarySurfaceCount;
            lock (_lock)
                secondarySurfaceCount = _secondarySurfaceCount;

            if (secondarySurfaceCount == 0)
            {
                if (EnsureNativeFrameDevice(e))
                    PresentNativeFrame(e);

                return;
            }
        }

        RenderHardwareVideoCompositionFrame(e, forceZoomToFill: false, "hardware video + secondary target");
    }

    void PresentVideoFrameAsCdgBackground(VideoFrameEventArgs e)
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

    void OnCdgBackgroundWashChanged(object? sender, EventArgs e) =>
        RequestCdgPresentationIfVisible(consumeDirtyRegions: false, retainedRefresh: true, "cdg wash changed");

    void OnCdgFrameInvalidated(object? sender, EventArgs e) =>
        RequestCdgPresentationIfVisible(consumeDirtyRegions: true, retainedRefresh: false, "cdg invalidated");

    void PresentRetainedCdgFrame()
    {
        _ = _cdgPresentationQueue.TryDequeue(out var consumeDirtyRegions, out _, out _);
        _ = RenderCurrentCdgFrame(consumeDirtyRegions, retainedRefresh: true, "retained cdg refresh");
    }

    void RequestCdgPresentation(bool consumeDirtyRegions, bool retainedRefresh, string reason)
    {
        _cdgPresentationQueue.Enqueue(consumeDirtyRegions, retainedRefresh, reason);

        if (!_isRenderingActive)
            _ = PresentPendingCdgFrame(deferIfCompositionBusy: false);
    }

    void RequestCdgPresentationIfVisible(bool consumeDirtyRegions, bool retainedRefresh, string reason)
    {
        if (!ShouldPresentVisualLayers)
        {
            _cdgPresentationQueue.Clear();
            return;
        }

        RequestCdgPresentation(consumeDirtyRegions, retainedRefresh, reason);
    }

    bool PresentPendingCdgFrame(bool deferIfCompositionBusy)
    {
        if (!_cdgPresentationQueue.TryDequeue(out var consumeDirtyRegions, out var retainedRefresh, out var reason))
            return false;

        var result = RenderCurrentCdgFrame(consumeDirtyRegions, retainedRefresh, reason, deferIfCompositionBusy);
        if (result == CompositionPresentResult.Deferred)
            _cdgPresentationQueue.Enqueue(consumeDirtyRegions, retainedRefresh, reason);

        return result == CompositionPresentResult.Presented;
    }

    CompositionPresentResult RenderCurrentCdgFrame(
        bool consumeDirtyRegions,
        bool retainedRefresh,
        string reason,
        bool deferIfCompositionBusy = false)
    {
        _ = UpdateSubtitleOverlay();

        // The render loop leaves the frame for later while another present is in progress.
        if (deferIfCompositionBusy)
        {
            if (!_presentGate.TryEnter())
                return CompositionPresentResult.Deferred;
        }
        else
        {
            _presentGate.Enter();
        }

        try
        {
            MediaCompositionFrame frame;
            RetainedFrameBuffer? rentedBackground;
            RetainedFrameBuffer? rentedSubtitle;
            CompositionPresentResult buildResult;
            lock (_lock)
                buildResult = TryBuildCdgCompositionFrameNoLock(consumeDirtyRegions, retainedRefresh, reason, out frame, out rentedBackground, out rentedSubtitle);

            return buildResult == CompositionPresentResult.Presented
                ? PresentToRenderer(frame, rentedBackground, rentedSubtitle)
                : buildResult;
        }
        finally
        {
            _presentGate.Exit();
        }
    }

    CompositionPresentResult TryBuildCdgCompositionFrameNoLock(
        bool consumeDirtyRegions,
        bool retainedRefresh,
        string reason,
        out MediaCompositionFrame frame,
        out RetainedFrameBuffer? rentedBackground,
        out RetainedFrameBuffer? rentedSubtitle)
    {
        frame = default;
        rentedBackground = null;
        rentedSubtitle = null;

        if (!TryCreateAndCacheCdgSnapshot(consumeDirtyRegions, out var snapshot) || snapshot == null)
            return CompositionPresentResult.Failed;

        if (HasCompositionBackgroundNoLock() && !IsCdgSnapshotReadyForBackgroundPresentation(snapshot))
            return CompositionPresentResult.Deferred;

        if (!EnsureBackendInitialized())
            return CompositionPresentResult.Failed;

        frame = BuildCompositionFrameNoLock(snapshot, snapshot.BackgroundWashArgb, retainedRefresh, reason, out rentedBackground, out rentedSubtitle);
        return CompositionPresentResult.Presented;
    }

    void RenderHardwareVideoCompositionFrame(VideoFrameEventArgs frame, bool forceZoomToFill, string reason)
    {
        _ = UpdateSubtitleOverlay();
        CdgRenderSnapshot? snapshot = null;
        uint washArgb = 0;
        if (TryGetCdgSnapshotForVideoComposition(out var cdgSnapshot) && cdgSnapshot != null)
        {
            snapshot = cdgSnapshot;
            washArgb = cdgSnapshot.BackgroundWashArgb;
        }

        lock (_presentGate)
        {
            if (!EnsureNativeFrameDevice(frame))
                return;

            MediaCompositionFrame compositionFrame;
            long backgroundRevision;
            RetainedFrameBuffer? rentedSubtitle;
            lock (_lock)
            {
                if (!EnsureBackendInitialized())
                    return;

                var stretch = forceZoomToFill ? MediaCompositionStretchMode.UniformToFill : _primaryStretchMode;
                backgroundRevision = SetPendingGpuBackgroundNoLock(frame, stretch);
                compositionFrame = BuildHardwareCompositionFrameNoLock(frame, stretch, backgroundRevision, snapshot, washArgb, reason, out rentedSubtitle);
            }

            // The renderer holds the frame only once a present carrying it succeeded.
            if (PresentToRenderer(compositionFrame, rentedBackground: null, rentedSubtitle) == CompositionPresentResult.Presented)
            {
                lock (_lock)
                    CommitGpuBackgroundNoLock(backgroundRevision);
            }
        }
    }

    MediaCompositionFrame BuildHardwareCompositionFrameNoLock(
        VideoFrameEventArgs frame,
        MediaCompositionStretchMode stretch,
        long backgroundRevision,
        CdgRenderSnapshot? snapshot,
        uint washArgb,
        string reason,
        out RetainedFrameBuffer? rentedSubtitle)
    {
        var subtitleOverlay = GetCachedSubtitleOverlay(out var subtitleRevision, out rentedSubtitle);

        (var clearMode, var clearArgb) = ResolveContentClear(TransparentBackground);
        return new MediaCompositionFrame
        {
            ClearMode = clearMode,
            ClearArgb = clearArgb,
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
            BaseVisualTransform = _baseVisualTransform.Value,
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

    void StoreRetainedHardwareBackgroundFrame(VideoFrameEventArgs frame, MediaCompositionStretchMode stretch)
    {
        if (!IsHardwareFrame(frame))
            return;

        lock (_presentGate)
        {
            if (!EnsureNativeFrameDevice(frame))
                return;

            // Not presented, so not retained: the next hardware frame that presents is (ENG-039).
            lock (_lock)
                _ = SetPendingGpuBackgroundNoLock(frame, stretch);
        }
    }

    /// <summary>
    /// Makes a hardware frame the pending background and returns its revision. The pooled CPU
    /// frame buffer is returned: hardware frames never use it. A retained refresh reuses it only
    /// after <see cref="CommitGpuBackgroundNoLock"/>: until a present carrying it succeeded the
    /// renderer does not hold it.
    /// </summary>
    long SetPendingGpuBackgroundNoLock(VideoFrameEventArgs frame, MediaCompositionStretchMode stretch)
    {
        _compositionBackgroundBgra = ReadOnlyMemory<byte>.Empty;
        _compositionBackgroundBuffer?.Release();
        _compositionBackgroundBuffer = null;
        _compositionBackgroundWidth = 0;
        _compositionBackgroundHeight = 0;
        _compositionBackgroundStride = 0;
        var revision = ++_compositionBackgroundRevision;
        _pendingGpuBackground = new GpuBackground(frame.Width, frame.Height, stretch, revision);
        return revision;
    }

    /// <summary>A present carrying the pending hardware background of <paramref name="revision"/> succeeded.</summary>
    void CommitGpuBackgroundNoLock(long revision)
    {
        if (_pendingGpuBackground is { } pending && pending.Revision == revision)
            _presentedGpuBackground = pending;
    }

    bool TryGetCdgSnapshotForVideoComposition(out CdgRenderSnapshot? snapshot)
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

    bool HasReadyCdgSnapshotForBackgroundPresentation()
    {
        lock (_cdgSnapshotGate)
            return _lastCdgSnapshot != null && IsCdgSnapshotReadyForBackgroundPresentation(_lastCdgSnapshot);
    }

    bool HasCompositionBackgroundNoLock()
    {
        var hasBgra = !_compositionBackgroundBgra.IsEmpty
            && _compositionBackgroundWidth > 0
            && _compositionBackgroundHeight > 0
            && _compositionBackgroundStride >= _compositionBackgroundWidth * 4;

        // A pending hardware frame counts: it is the background, even before it was presented.
        return hasBgra || _presentedGpuBackground is not null || _pendingGpuBackground is not null;
    }

    bool HasCompositionBackground()
    {
        lock (_lock)
            return HasCompositionBackgroundNoLock();
    }

    string DescribeBackendPresentFailure() =>
        _rendererBackend.LastPresentFailureReason is { Length: > 0 } reason ? $" ({reason})" : string.Empty;

    // Refused presents repeat every frame: build the message only for the first of an episode.
    // Before any surface is attached "no target" is the expected answer, not a failure (ENG-012).
    void ReportPresentFailure(MediaCompositionFrame frame)
    {
        if (!HasAttachedSurfaces)
            return;

        if (!_presentFailure.IsLatched)
            _ = _presentFailure.Raise(MediaDiagnosticLevel.Warning, "Composition", $"Composition present failed reason={frame.Reason}{DescribeBackendPresentFailure()}");

        KickDeviceProbe();
    }

    // Caller holds _presentGate. ENG-039: its result was ignored before.
    void PresentNativeFrame(VideoFrameEventArgs frame)
    {
        bool presented;
        using (BeginRendererCall(_renderCalls, "PresentNativeFrame"))
            presented = _rendererBackend.PresentNativeFrame(frame);

        if (presented)
        {
            _nativePresentFailure.Reset();
            return;
        }

        if (!HasAttachedSurfaces)
            return;

        if (!_nativePresentFailure.IsLatched)
            _ = _nativePresentFailure.Raise(MediaDiagnosticLevel.Warning, "Composition", $"Video frame present failed{DescribeBackendPresentFailure()}");

        KickDeviceProbe();
    }

    bool HasCurrentVisualContent() =>
        _mediaEngine.Content.HasFlag(MediaContent.Image)
        || _mediaEngine.Content.HasFlag(MediaContent.Video)
        || HasCdgContent
        || HasSubtitleContent
        || HasCompositionBackground();

    static bool IsCdgSnapshotReadyForBackgroundPresentation(CdgRenderSnapshot snapshot) =>
        snapshot.BackgroundColorIndex >= 0
        || ((snapshot.BackgroundWashArgb >> 24) & 0xFF) != 0;

    bool TryCreateAndCacheCdgSnapshot(bool consumeDirtyRegions, out CdgRenderSnapshot? snapshot)
    {
        snapshot = null;
        if (_attachedCdg is not ICdgSnapshotSource snapshotSource
            || !_mediaEngine.Content.HasFlag(MediaContent.Cdg)
            || !snapshotSource.TryCreateCdgSnapshot(consumeDirtyRegions, out var createdSnapshot))
            return false;

        snapshot = createdSnapshot;
        lock (_cdgSnapshotGate)
            _lastCdgSnapshot = createdSnapshot;

        return true;
    }

    /// <summary>Whether <see cref="PresentCurrentComposition"/> presents through its video branch.</summary>
    bool IsPlainVideoComposition() =>
        !HasCdgContent
        && !_mediaEngine.Content.HasFlag(MediaContent.Image)
        && _mediaEngine.Content.HasFlag(MediaContent.Video)
        && !(_attachedVideo == null && _attachedCdg == null && HasSubtitleContent);

    void PresentCurrentComposition(string reason)
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
            && _mediaEngine.TryGetCurrentImageFrame(out var frame)
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

    CompositionPresentResult PresentMediaComposition(MediaCompositionFrame frame)
    {
        lock (_presentGate)
        {
            lock (_lock)
            {
                if (!EnsureBackendInitialized())
                    return CompositionPresentResult.Failed;
            }

            return PresentToRenderer(frame, rentedBackground: null);
        }
    }

    CompositionPresentResult BuildAndPresentSoftwareComposition(
        uint washArgb,
        bool retainedRefresh,
        string reason,
        double? subtitlePositionSeconds = null,
        bool updateSubtitleOverlay = true)
    {
        lock (_presentGate)
        {
            if (updateSubtitleOverlay)
                _ = UpdateSubtitleOverlay(subtitlePositionSeconds);
            MediaCompositionFrame frame;
            RetainedFrameBuffer? rentedBackground;
            RetainedFrameBuffer? rentedSubtitle;
            lock (_lock)
            {
                if (!EnsureBackendInitialized())
                    return CompositionPresentResult.Failed;

                frame = BuildCompositionFrameNoLock(null, washArgb, retainedRefresh, reason, out rentedBackground, out rentedSubtitle);
            }

            return PresentToRenderer(frame, rentedBackground, rentedSubtitle);
        }
    }

    // Caller holds _presentGate and not _lock: the renderer call, its failure report and the
    // device probe a failure kicks.
    CompositionPresentResult PresentToRenderer(
        MediaCompositionFrame frame,
        RetainedFrameBuffer? rentedBackground,
        RetainedFrameBuffer? rentedSubtitle = null)
    {
        try
        {
            bool presented;
            using (BeginRendererCall(_renderCalls, "PresentComposition"))
                presented = _rendererBackend.PresentComposition(ForVideoFrameTargets(frame));

            if (!presented)
            {
                ReportPresentFailure(frame);
                return CompositionPresentResult.Failed;
            }

            _presentFailure.Reset();
            return CompositionPresentResult.Presented;
        }
        finally
        {
            rentedBackground?.Release();
            rentedSubtitle?.Release();
        }
    }

    MediaCompositionFrame BuildCompositionFrameNoLock(
        CdgRenderSnapshot? cdgOverlay,
        uint washArgb,
        bool retainedRefresh,
        string reason,
        out RetainedFrameBuffer? rentedBackground,
        out RetainedFrameBuffer? rentedSubtitle)
    {
        rentedBackground = null;
        ReadOnlyMemory<byte> backgroundBgra;
        int backgroundWidth;
        int backgroundHeight;
        int backgroundStride;
        MediaCompositionStretchMode backgroundStretchMode;
        long backgroundRevision;

        backgroundBgra = _compositionBackgroundBgra;
        backgroundWidth = _compositionBackgroundWidth;
        backgroundHeight = _compositionBackgroundHeight;
        backgroundStride = _compositionBackgroundStride;
        backgroundStretchMode = _compositionBackgroundStretchMode;
        backgroundRevision = _compositionBackgroundRevision;

        var hasBackground = !backgroundBgra.IsEmpty
            && backgroundWidth > 0
            && backgroundHeight > 0
            && backgroundStride >= backgroundWidth * 4
            && backgroundBgra.Length >= checked(backgroundStride * backgroundHeight);

        // Only a hardware frame the renderer received may be reused; until then the refresh goes
        // out without a background rather than asking for one the renderer never got (ENG-039).
        var hasRetainedGpuBackground = false;
        if (!hasBackground && _presentedGpuBackground is { Width: > 0, Height: > 0 } presentedGpu)
        {
            hasRetainedGpuBackground = true;
            backgroundWidth = presentedGpu.Width;
            backgroundHeight = presentedGpu.Height;
            backgroundStretchMode = presentedGpu.StretchMode;
            backgroundRevision = presentedGpu.Revision;
        }

        // The composition shares the retained pixels through a reference instead of copying
        // them; SetCompositionLayer0Frame never overwrites a buffer a composition still holds.
        var ownedBackgroundBgra = ReadOnlyMemory<byte>.Empty;
        if (hasBackground && _compositionBackgroundBuffer is { } retainedBuffer)
        {
            rentedBackground = retainedBuffer.AddReference();
            ownedBackgroundBgra = backgroundBgra;
        }
        else
        {
            hasBackground = false;
        }

        var subtitleOverlay = GetCachedSubtitleOverlay(out var subtitleRevision, out rentedSubtitle);

        (var clearMode, var clearArgb) = ResolveContentClear(TransparentBackground);
        return new MediaCompositionFrame
        {
            ClearMode = clearMode,
            ClearArgb = clearArgb,
            BackgroundBgra = hasBackground ? ownedBackgroundBgra : ReadOnlyMemory<byte>.Empty,
            BackgroundWidth = hasBackground || hasRetainedGpuBackground ? backgroundWidth : 0,
            BackgroundHeight = hasBackground || hasRetainedGpuBackground ? backgroundHeight : 0,
            BackgroundStride = hasBackground ? backgroundStride : 0,
            BackgroundStretchMode = hasBackground || hasRetainedGpuBackground ? backgroundStretchMode : MediaCompositionStretchMode.UniformFit,
            BackgroundUniformToFill = (hasBackground || hasRetainedGpuBackground) && backgroundStretchMode == MediaCompositionStretchMode.UniformToFill,
            BackgroundRevision = hasBackground || hasRetainedGpuBackground ? backgroundRevision : -1,
            UseRetainedBackground = hasRetainedGpuBackground,
            BaseVisualTransform = _baseVisualTransform.Value,
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

    internal bool UpdateSubtitleOverlay(double? presentationPositionSeconds = null)
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

            // A scaled CD+G picture (it draws the position the CD+G engine last rendered), else engine-drawn lyrics.
            if (_attachedCdg is IKaraokeOverlaySource { DrawsOverlay: true } cdgPicture)
                return UpdateEngineOverlay(cdgPicture, TimeSpan.FromSeconds(subtitlePosition));
            if (_mediaEngine.Audio is IKaraokeOverlaySource { DrawsOverlay: true } engineLyrics)
                return UpdateEngineOverlay(engineLyrics, TimeSpan.FromSeconds(subtitlePosition));

            var activeCues = _mediaEngine.Subtitle?.GetActiveCues(subtitlePosition) ?? [];
            var karaokeTiming = activeCues.Count == 1 ? activeCues[0].KaraokeTiming : null;
            var subtitleTime = TimeSpan.FromSeconds(subtitlePosition);
            var progressRevision = karaokeTiming?.GetProgressRevision(subtitleTime, _wipeTransitionDuration) ?? int.MinValue;
            // The same cues at the same progress draw the same raster: skip it without rebuilding
            // the cue text (ENG-039 P5-2).
            var sameCues = IsSameSubtitleCues(activeCues);
            if (sameCues && progressRevision == _subtitleProgressRevision)
                return false;

            var text = sameCues ? _subtitleText : JoinCueText(activeCues);
            if (!sameCues)
                _subtitleCues = [.. activeCues];
            if (string.Equals(text, _subtitleText, StringComparison.Ordinal)
                && progressRevision == _subtitleProgressRevision)
                return false;

            _subtitleText = text;
            _subtitleProgressRevision = progressRevision;
            _subtitleOverlay = null;
            _subtitleOverlayBuffer = null;
            _subtitleRevision++;
            if (string.IsNullOrWhiteSpace(text))
                return true;

            try
            {
                var overlay = karaokeTiming == null
                    ? _subtitleRasterizer.Render(text, SubtitleCanvasWidth, SubtitleCanvasHeight, _rentSubtitlePixels)
                    : _subtitleRasterizer.RenderKaraoke(karaokeTiming, subtitleTime, SubtitleCanvasWidth, SubtitleCanvasHeight, _rentSubtitlePixels);
                _subtitleOverlay = overlay;
                // Pixels in the pooled buffer are shared with compositions by reference; a
                // rasterizer that returned its own memory keeps owning it.
                _subtitleOverlayBuffer = _subtitleBufferRented
                    && _subtitleBuffer is { } buffer
                    && overlay.Pixels.Span.Overlaps(buffer.Memory.Span)
                        ? buffer
                        : null;
            }
            catch (Exception ex)
            {
                DiagnosticRouter.Raise(MediaDiagnosticLevel.Warning, "Subtitle", $"Text rasterization failed: {ex.Message}", innerException: ex);
            }
            finally
            {
                _subtitleBufferRented = false;
            }

            return true;
        }
    }

    /// <summary>
    /// The lyric screen the audio engine draws (FatzAudio, 037A) instead of the rasterized lyric cues: into the
    /// pooled overlay memory, only what changed when that memory still holds the previous frame. Caller holds
    /// _subtitleGate.
    /// </summary>
    bool UpdateEngineOverlay(IKaraokeOverlaySource source, TimeSpan time)
    {
        // The cue path's state no longer describes the overlay.
        _subtitleCues = [];
        _subtitleText = null;
        _subtitleProgressRevision = int.MinValue;
        try
        {
            var overlay = source.DrawOverlay(time, SubtitleCanvasWidth, SubtitleCanvasHeight, _rentSubtitlePixels, out var changed);
            if (overlay is null)
            {
                if (_subtitleOverlay is null)
                    return false;

                _subtitleOverlay = null;
                _subtitleOverlayBuffer = null;
                _subtitleRevision++;
                return true;
            }

            var buffer = _subtitleBufferRented && _subtitleBuffer is { } rented && overlay.Value.Pixels.Span.Overlaps(rented.Memory.Span)
                ? rented
                : null;
            if (!changed && ReferenceEquals(buffer, _subtitleOverlayBuffer) && _subtitleOverlay is not null)
                return false;

            _subtitleOverlay = overlay;
            _subtitleOverlayBuffer = buffer;
            _subtitleRevision++;
            return true;
        }
        catch (Exception ex)
        {
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Warning, "Subtitle", $"Lyric drawing failed: {ex.Message}", innerException: ex);
            return false;
        }
        finally
        {
            _subtitleBufferRented = false;
        }
    }

    // Caller holds _subtitleGate.
    bool IsSameSubtitleCues(IReadOnlyList<SubtitleCue> activeCues)
    {
        var previous = _subtitleCues;
        if (previous.Length != activeCues.Count)
            return false;

        for (var i = 0; i < previous.Length; i++)
        {
            if (!ReferenceEquals(previous[i], activeCues[i]))
                return false;
        }

        return true;
    }

    static string? JoinCueText(IReadOnlyList<SubtitleCue> activeCues) => activeCues.Count switch
    {
        0 => null,
        1 => activeCues[0].Text,
        _ => string.Join("\n", activeCues.Select(static cue => cue.Text)),
    };

    // The rasterizer's buffer request, made under _subtitleGate during UpdateSubtitleOverlay. The
    // buffer is overwritten in place only when no composition holds it (as the background is).
    Memory<byte> RentSubtitlePixels(int length)
    {
        var buffer = _subtitleBuffer;
        // A karaoke raster's bounds change from frame to frame: any buffer large enough is reused.
        if (buffer is null || !buffer.IsExclusive || buffer.Length < length)
        {
            buffer?.Release();
            buffer = RetainedFrameBuffer.Rent(_subtitleOverlayPool, length);
            _subtitleBuffer = buffer;
        }

        _subtitleBufferRented = true;
        return buffer.Memory[..length];
    }

    bool HasCachedSubtitleOverlay()
    {
        lock (_subtitleGate)
            return _subtitleOverlay is { Pixels.IsEmpty: false };
    }

    static bool IsHardwareFrame(VideoFrameEventArgs frame) =>
        frame.NativeResource is { IsValid: true };

    // A pooled overlay comes with a reference the caller releases once the present is over
    // (PresentToRenderer does), so a raster on another thread never overwrites pixels in flight.
    SubtitleRasterResult? GetCachedSubtitleOverlay(out long revision, out RetainedFrameBuffer? rentedSubtitle)
    {
        lock (_subtitleGate)
        {
            if (_subtitleOverlay is not { Pixels.IsEmpty: false } overlay)
            {
                revision = -1;
                rentedSubtitle = null;
                return null;
            }

            revision = _subtitleRevision;
            rentedSubtitle = _subtitleOverlayBuffer?.AddReference();
            return overlay;
        }
    }

    void SetCompositionLayer0Frame(VideoFrameEventArgs frame, MediaCompositionStretchMode stretchMode)
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
            var buffer = _compositionBackgroundBuffer;
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
            _pendingGpuBackground = null;
            _presentedGpuBackground = null;
            _compositionBackgroundRevision++;
        }
    }

    void ClearCompositionLayer0Frame()
    {
        lock (_presentGate)
        {
            lock (_lock)
                ClearCompositionLayer0FrameNoLock();

            ClearRendererBackgroundFrame();
        }
    }

    // Caller holds _presentGate.
    void ClearRendererBackgroundFrame()
    {
        using (BeginRendererCall(_renderCalls, "ClearCompositionBackgroundFrame"))
            _rendererBackend.ClearCompositionBackgroundFrame();
    }

    void ClearCompositionLayer0FrameNoLock()
    {
        _compositionBackgroundBgra = ReadOnlyMemory<byte>.Empty;
        _compositionBackgroundBuffer?.Release();
        _compositionBackgroundBuffer = null;
        _compositionBackgroundWidth = 0;
        _compositionBackgroundHeight = 0;
        _compositionBackgroundStride = 0;
        _compositionBackgroundStretchMode = MediaCompositionStretchMode.UniformFit;
        _pendingGpuBackground = null;
        _presentedGpuBackground = null;
        _compositionBackgroundRevision++;
    }

    void ClearAllContent() => ClearVisibleComposition("all content cleared");

    void ClearVisibleComposition(string reason, bool preserveBackground = false)
    {
        _cdgPresentationQueue.Clear();
        lock (_cdgSnapshotGate)
            _lastCdgSnapshot = null;
        InvalidateSubtitleOverlayCache();

        lock (_presentGate)
        {
            if (!preserveBackground)
            {
                lock (_lock)
                    ClearCompositionLayer0FrameNoLock();

                ClearRendererBackgroundFrame();
            }

            lock (_lock)
            {
                if (!EnsureBackendInitialized())
                    return;
            }

            _ = PresentToRenderer(
                MediaCompositionFrame.Empty(MediaCompositionClearMode.Transparent, 0x00000000, reason),
                rentedBackground: null);
        }
    }

    void InvalidateSubtitleOverlayCache()
    {
        lock (_subtitleGate)
        {
            _subtitleText = null;
            _subtitleCues = [];
            _subtitleProgressRevision = int.MinValue;
            _subtitleOverlay = null;
            _subtitleOverlayBuffer = null;
            _subtitleBuffer?.Release();
            _subtitleBuffer = null;
            _lastSubtitleOffsetSeconds = double.NaN;
        }
    }

    void DetachAllEngines()
    {
        if (_attachedVideo != null)
        {
            try
            {
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

    bool HasCdgContent => _mediaEngine.Content.HasFlag(MediaContent.Cdg);

    bool HasSubtitleContent => _mediaEngine.Content.HasFlag(MediaContent.Subtitle);

    bool ShouldPresentVisualLayers =>
        _isRenderingActive
        || _mediaEngine.State is MediaPlayerState.Playing or MediaPlayerState.Paused
        || HasStoppedStandaloneVisualContent;

    bool HasStoppedStandaloneVisualContent =>
        _mediaEngine.State == MediaPlayerState.Stopped
        && !_mediaEngine.Content.HasFlag(MediaContent.Audio)
        && (_mediaEngine.Content.HasFlag(MediaContent.Video)
            || _mediaEngine.Content.HasFlag(MediaContent.Cdg));

    bool ShouldPresentImmediatelyAfterPrimaryResize() =>
        !_isRenderingActive
        || (!HasCdgContent && !_mediaEngine.Content.HasFlag(MediaContent.Video));

    static bool IsValidBgraFrame(VideoFrameEventArgs e) =>
        !e.PixelData.IsEmpty && e.Width > 0 && e.Height > 0 && e.Stride >= e.Width * 4;

    static MediaCompositionStretchMode ToCompositionStretchMode(VideoDisplayMode mode) => mode switch
    {
        VideoDisplayMode.Original => MediaCompositionStretchMode.Original,
        VideoDisplayMode.Stretch => MediaCompositionStretchMode.Fill,
        VideoDisplayMode.ZoomToFill => MediaCompositionStretchMode.UniformToFill,
        _ => MediaCompositionStretchMode.UniformFit
    };

    void OnFramePresented(object? sender, EventArgs e) => FramePresented?.Invoke(this, EventArgs.Empty);

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        _isRenderingActive = false;
        StopDeviceHealth();
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
        if (_rendererBackend is IMediaRendererVideoDecodeDevice decodeDevice)
            (_mediaEngine as IRendererVideoDecodeAttachment)?.DetachRendererVideoDecodeDevice(decodeDevice);

        // After a present still in progress on a video thread (it took the gate before Dispose
        // detached the engines).
        lock (_presentGate)
        {
            lock (_lock)
            {
                _rendererBackend.CleanupResources();
                _rendererBackend.Dispose();
                ClearCompositionLayer0FrameNoLock();
            }
        }
        _subtitleRasterizer.Dispose();
        InvalidateSubtitleOverlayCache();
        _presentationScheduler.Dispose();
        _recoveryScheduler.Dispose();
    }
}
