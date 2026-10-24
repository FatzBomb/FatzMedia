using FatzBomb.MediaControl.Core.Configuration;
using FatzBomb.MediaControl.Core.Cdg;
using FatzBomb.MediaControl.Core.Graphics.Composition;
using FatzBomb.MediaControl.Core.Graphics.D3D.Device;
using FatzBomb.MediaControl.Core.Graphics.D3D.Diagnostics;
using FatzBomb.MediaControl.Core.Graphics.D3D.Frame;
using FatzBomb.MediaControl.Core.Graphics.D3D.Presentation.DirectComposition;
using FatzBomb.MediaControl.Core.Graphics.D3D.Presentation.Managed;
using FatzBomb.MediaControl.Core.Graphics.D3D.Presentation.NativeHwnd;
using FatzBomb.MediaControl.Core.Graphics.D3D.Upload;
using FatzBomb.MediaControl.Core.Graphics.D3D.Video;
using FatzBomb.MediaControl.Rendering.Direct3D11;
using System.Diagnostics;

namespace FatzBomb.MediaControl.Core.Graphics.D3D.Presentation;

/// <summary>
/// Builds and owns the presentation-side services used by <see cref="VorticeD3D11Renderer" />.
/// Keeping this object graph out of the renderer makes the renderer a small coordinator while
/// preserving the existing backend, upload, compositor, and presenter lifetimes.
/// </summary>
internal sealed class D3D11RendererPresentationServices
{
    private const int DirectCompositionOverlaySplitDiagnosticInterval = 120;

    private readonly MediaPlayerConfiguration _configuration;
    private readonly D3D11DeviceResources _deviceResources;
    private bool _directCompositionOverlaySplitActive;
    private bool _directCompositionOverlaySplitBaseStateValid;
    private bool _directCompositionOverlaySplitOverlayStateValid;
    private DirectCompositionOverlaySplitBaseState _directCompositionOverlaySplitBaseState;
    private DirectCompositionOverlaySplitOverlayState _directCompositionOverlaySplitOverlayState;
    private int _directCompositionOverlaySplitPresents;
    private int _directCompositionOverlaySplitFallbacks;
    private int _directCompositionOverlaySplitOverlayPresentFailures;
    private int _directCompositionOverlaySplitBasePresents;
    private int _directCompositionOverlaySplitBaseSkips;
    private int _directCompositionOverlaySplitOverlayPresents;
    private int _directCompositionOverlaySplitOverlaySkips;

    public D3D11RendererPresentationServices(
        MediaPlayerConfiguration configuration,
        Direct3D11PresentationOptions options,
        D3D11DeviceResources deviceResources)
    {
        _configuration = configuration;
        _deviceResources = deviceResources;

        VideoProcessorPresenter = new D3D11VideoProcessorPresenter(configuration);
        Diagnostics = new D3D11PresentationDiagnostics(configuration);
        State = new D3D11PresentationState();
        MediaCompositor = new D3D11MediaCompositionPresenter(
            configuration.EnableVerboseLogging,
            configuration.EnablePerformanceLogging);

        D3D11NativeHwndSwapChainBackend CreateNativeHwndBackend() => new(
            configuration,
            options.NativeSwapChainPresentMode,
            Diagnostics,
            () => VideoProcessorPresenter.Reset(),
            static () => { });

        D3D11NativeHwndSwapChainBackend nativeHwndBackend = CreateNativeHwndBackend();
        var directCompositionBackend = new D3D11DirectCompositionSingleTargetBackend(
            configuration,
            options.NativeSwapChainPresentMode,
            Diagnostics,
            () => VideoProcessorPresenter.Reset());
        var managedFrameSinkBackend = new D3D11ManagedFrameSinkBackend();

        Backends = new D3D11PresentationBackendSet(
            nativeHwndBackend,
            directCompositionBackend,
            managedFrameSinkBackend,
            CreateNativeHwndBackend);
        Surfaces = new D3D11PresentationSurfaceController(Backends);
        UploadResources = new D3D11FrameUploadResources(Diagnostics);
        ResourceCleaner = new D3D11PresentationResourceCleaner(
            deviceResources,
            VideoProcessorPresenter,
            Surfaces);

        var framePresenter = new D3D11FramePresenter(
            UploadResources,
            VideoProcessorPresenter);
        FramePresenter = new D3D11PresentationFramePresenter(
            framePresenter,
            Surfaces,
            State);
    }


    public bool IsReady => _deviceResources.HasDevice && Surfaces.IsReady;

    public bool IsDirectCompositionActive => Surfaces.IsDirectCompositionActive;

    public string PresentationBackend => Surfaces.PresentationBackend;


    public bool IsNativeLiveResizeInProgress => State.IsNativeLiveResizeInProgress;

    public void SetNativeLiveResizeInProgress(bool isResizing) => State.SetNativeLiveResizeInProgress(isResizing);

    public void SetNativePresentationStretch(MediaCompositionStretchMode stretchMode) => State.SetNativePresentationStretch(stretchMode, VideoProcessorPresenter);

    public bool InitializeDevice(bool isDisposed)
    {
        if (isDisposed)
        {
            return false;
        }

        var initialized = _deviceResources.Initialize(_configuration);
        if (!initialized)
        {
            ResourceCleaner.CleanupResources();
        }

        return initialized;
    }

    public bool InitializeFromNativeDevice(bool isDisposed, IntPtr devicePointer, IntPtr deviceContextPointer)
    {
        if (isDisposed || devicePointer == IntPtr.Zero)
        {
            return false;
        }

        MediaCompositor.Reset();
        ResourceCleaner.CleanupResources();
        return _deviceResources.InitializeFromNativeDevice(devicePointer, deviceContextPointer, _configuration);
    }

    public bool CreateNativeHwndSurface(bool isDisposed, IntPtr hwnd, int width, int height, Action<int, int, bool> notifyPresented) => Surfaces.CreateNativeHwndSurface(
            isDisposed,
            _deviceResources,
            hwnd,
            width,
            height,
            ResourceCleaner.CleanupPrimarySurfaces,
            _configuration.EnableLogging,
            notifyPresented);

    public bool CreateSecondaryNativeHwndSurface(bool isDisposed, string targetId, IntPtr hwnd, int width, int height) => Surfaces.CreateSecondaryNativeHwndSurface(
            isDisposed,
            _deviceResources,
            targetId,
            hwnd,
            width,
            height,
            _configuration.EnableLogging);

    public bool RemoveSecondaryPresentationTarget(string targetId) => Surfaces.RemoveSecondaryPresentationTarget(targetId);

    public bool CreateSecondaryManagedFrameSinkSurface(bool isDisposed, string targetId, IMediaPresentationFrameSink sink, int width, int height) =>
        Surfaces.CreateSecondaryManagedFrameSinkSurface(isDisposed, _deviceResources, targetId, sink, width, height, _configuration.EnableLogging);

    public bool CreateSecondarySharedTextureSurface(
        bool isDisposed,
        string targetId,
        IMediaSharedGpuImageConsumer consumer,
        int width,
        int height,
        out string? failureReason) =>
        Surfaces.CreateSecondarySharedTextureSurface(isDisposed, _deviceResources, targetId, consumer, width, height, _configuration.EnableLogging, out failureReason);

    public bool CreateManagedFrameSinkSurface(
        bool isDisposed,
        IMediaPresentationFrameSink sink,
        int width,
        int height,
        Action<int, int, bool> notifyPresented) => Surfaces.CreateManagedFrameSinkSurface(
            isDisposed,
            _deviceResources,
            sink,
            width,
            height,
            ResourceCleaner.CleanupPrimarySurfaces,
            _configuration.EnableLogging,
            notifyPresented);

    public bool CreateSharedTextureSurface(
        bool isDisposed,
        IMediaSharedGpuImageConsumer consumer,
        int width,
        int height,
        Action<int, int, bool> notifyPresented,
        out string? failureReason) => Surfaces.CreateSharedTextureSurface(
            isDisposed,
            _deviceResources,
            consumer,
            width,
            height,
            ResourceCleaner.CleanupPrimarySurfaces,
            _configuration.EnableLogging,
            notifyPresented,
            out failureReason);

    /// <summary>
    /// Disposes only the primary presentation surface/backends, retaining secondary native HWND
    /// targets so they survive a primary detach and resume when a native primary returns.
    /// </summary>
    public void CleanupPrimarySurfaces() => ResourceCleaner.CleanupPrimarySurfaces();

    public bool CreateDirectCompositionSurface(bool isDisposed, IntPtr hwnd, int width, int height, Action<int, int, bool> notifyPresented) => Surfaces.CreateDirectCompositionSurface(
            isDisposed,
            _deviceResources,
            hwnd,
            width,
            height,
            ResourceCleaner.CleanupPrimarySurfaces,
            _configuration.EnableLogging,
            notifyPresented);

    public void QueueDirectCompositionResize(bool isDisposed, int width, int height) => Surfaces.QueueDirectCompositionResize(isDisposed, width, height);

    public void QueueNativeHwndResize(bool isDisposed, int width, int height) => Surfaces.QueueNativeHwndResize(isDisposed, width, height);

    public bool TryGetActiveTargetSnapshot(bool applyPendingNativeResize, string? reason, out D3D11PresentationTargetSnapshot target) => Surfaces.TryGetActiveTargetSnapshot(applyPendingNativeResize, reason, out target);

    public bool TryGetDirectCompositionOverlayTargetSnapshot(out D3D11PresentationTargetSnapshot target) => Surfaces.TryGetDirectCompositionOverlayTargetSnapshot(out target);



    public bool PresentHardwareFrame(
        bool isDisposed,
        IntPtr sourceTexturePointer,
        int sourceArraySlice,
        int width,
        int height,
        double framesPerSecond,
        VideoColorSpace colorSpace,
        HdrToneMapRequest? toneMap,
        Func<int, int, bool> notifyPresented) => FramePresenter.PresentHardwareFrame(
            _deviceResources,
            isDisposed,
            sourceTexturePointer,
            sourceArraySlice,
            width,
            height,
            framesPerSecond,
            colorSpace,
            toneMap,
            notifyPresented);

    // isDisposed is read live: the owner can be disposed while a present is in flight.
    public bool NotifyFramePresented(Func<bool> isDisposed, bool syncImmediate, Action raiseFramePresented) => Surfaces.NotifyFramePresented(
            IsNativeLiveResizeInProgress,
            syncImmediate,
            isDisposed,
            raiseFramePresented);

    public void ClearCompositionBackgroundFrame() => MediaCompositor.ClearBackgroundFrame();

    /// <summary>Why the last visible composition present returned false, or null.</summary>
    public string? LastCompositionFailureReason { get; private set; }

    /// <param name="isDisposed">Read live: the owner can be disposed while a present is in flight.</param>
    public bool PresentMediaCompositionVisible(
        Func<bool> isDisposed,
        MediaCompositionFrame frame,
        Action<int, int> framePresented)
    {
        LastCompositionFailureReason = null;
        if (isDisposed() || !_deviceResources.HasDevice)
        {
            LastCompositionFailureReason = "renderer disposed or D3D11 device unavailable";
            if (_configuration.EnableLogging) Debug.WriteLine("[COMPOSITION] Visible present skipped: renderer disposed or D3D11 device unavailable.");
            return false;
        }

        var isOverlaySplitCandidate = IsDirectCompositionOverlaySplitCandidate(frame);
        if (CanUseDirectCompositionOverlaySplit(frame))
        {
            if (PresentMediaCompositionWithDirectCompositionOverlaySplit(
                isDisposed,
                frame,
                framePresented,
                out var basePresented,
                out var overlayPresented,
                out var overlaySkipped,
                out var overlayPresentFailure))
            {
                RecordDirectCompositionOverlaySplitDiagnostics(
                    splitPresented: true,
                    fallback: false,
                    overlayPresentFailure: false,
                    basePresented,
                    baseSkipped: !basePresented,
                    overlayPresented,
                    overlaySkipped);
                return true;
            }

            RecordDirectCompositionOverlaySplitDiagnostics(
                splitPresented: false,
                fallback: true,
                overlayPresentFailure,
                basePresented: false,
                baseSkipped: false,
                overlayPresented: false,
                overlaySkipped: false);
        }
        else if (isOverlaySplitCandidate)
        {
            RecordDirectCompositionOverlaySplitDiagnostics(
                splitPresented: false,
                fallback: true,
                overlayPresentFailure: false,
                basePresented: false,
                baseSkipped: false,
                overlayPresented: false,
                overlaySkipped: false);
        }

        ClearDirectCompositionOverlaySplitIfNeeded(isDisposed());
        return PresentMediaCompositionToFinalTargets(isDisposed, frame, framePresented);
    }

    private bool PresentMediaCompositionToFinalTargets(
        Func<bool> isDisposed,
        MediaCompositionFrame frame,
        Action<int, int> framePresented)
    {
        IReadOnlyList<D3D11PresentationTargetSnapshot> targets = Surfaces.GetActiveTargetSnapshots(
            applyPendingNativeResize: true,
            reason: "media composition presentation");
        if (targets.Count == 0)
        {
            LastCompositionFailureReason = "no active D3D11 presentation target";
            if (_configuration.EnableLogging) Debug.WriteLine("[COMPOSITION] Visible present skipped: no active D3D11 presentation target.");
            return false;
        }

        if (!MediaCompositor.PresentToTargets(
                _deviceResources,
                frame,
                targets,
                VideoProcessorPresenter,
                out IReadOnlyList<D3D11PresentationTargetSnapshot> presentedTargets))
        {
            LastCompositionFailureReason = MediaCompositor.LastFailureReason ?? "media compositor draw failed";
            if (_configuration.EnableLogging) Debug.WriteLine($"[COMPOSITION] Visible present failed in media compositor reason={frame.Reason}.");
            return false;
        }

        var presented = Surfaces.NotifyTargetsPresented(
            presentedTargets,
            State.IsNativeLiveResizeInProgress,
            syncImmediate: true,
            isDisposed,
            target => framePresented(target.Width, target.Height));
        if (!presented)
            LastCompositionFailureReason = "swap chain present was not completed";

        return presented;
    }

    private bool PresentMediaCompositionWithDirectCompositionOverlaySplit(
        Func<bool> isDisposed,
        MediaCompositionFrame frame,
        Action<int, int> framePresented,
        out bool basePresented,
        out bool overlayPresented,
        out bool overlaySkipped,
        out bool overlayPresentFailure)
    {
        basePresented = false;
        overlayPresented = false;
        overlaySkipped = false;
        overlayPresentFailure = false;

        CdgRenderSnapshot? snapshot = frame.CdgOverlay;
        if (snapshot == null || !Surfaces.TryGetDirectCompositionOverlayTargetSnapshot(out D3D11PresentationTargetSnapshot overlayTarget))
            return false;

        DirectCompositionOverlaySplitBaseState baseState = CreateDirectCompositionOverlaySplitBaseState(frame, overlayTarget);
        var shouldPresentBase = ShouldPresentDirectCompositionOverlaySplitBase(baseState);
        if (shouldPresentBase && !PresentMediaCompositionToFinalTargets(isDisposed, AsDirectCompositionBaseFrame(frame), framePresented))
            return false;

        if (shouldPresentBase)
        {
            _directCompositionOverlaySplitBaseState = baseState;
            _directCompositionOverlaySplitBaseStateValid = true;
            basePresented = true;
        }

        DirectCompositionOverlaySplitOverlayState overlayState = CreateDirectCompositionOverlaySplitOverlayState(snapshot, overlayTarget);
        var shouldPresentOverlay = ShouldPresentDirectCompositionOverlaySplitOverlay(overlayState);
        if (!shouldPresentOverlay)
        {
            overlaySkipped = true;
            _directCompositionOverlaySplitActive = true;
            return true;
        }

        if (!MediaCompositor.DrawCdgOverlayToTarget(_deviceResources, snapshot, overlayTarget))
            return false;

        if (!Surfaces.PresentDirectCompositionOverlay(isDisposed(), State.IsNativeLiveResizeInProgress))
        {
            overlayPresentFailure = true;
            return false;
        }

        _directCompositionOverlaySplitOverlayState = overlayState;
        _directCompositionOverlaySplitOverlayStateValid = true;
        _directCompositionOverlaySplitActive = true;
        overlayPresented = true;
        framePresented(overlayTarget.Width, overlayTarget.Height);
        return true;
    }

    private bool ShouldPresentDirectCompositionOverlaySplitBase(DirectCompositionOverlaySplitBaseState baseState) =>
        !_directCompositionOverlaySplitActive
        || !_directCompositionOverlaySplitBaseStateValid
        || baseState != _directCompositionOverlaySplitBaseState;

    private bool ShouldPresentDirectCompositionOverlaySplitOverlay(DirectCompositionOverlaySplitOverlayState overlayState) =>
        !_directCompositionOverlaySplitActive
        || !_directCompositionOverlaySplitOverlayStateValid
        || overlayState != _directCompositionOverlaySplitOverlayState;

    private static bool IsDirectCompositionOverlaySplitCandidate(MediaCompositionFrame frame) =>
        frame.HasCdgOverlay
        && (frame.HasBackground || frame.HasNativeBackground || frame.HasRetainedBackground);

    private bool CanUseDirectCompositionOverlaySplit(MediaCompositionFrame frame) =>
        Backends.DirectComposition.IsActive
        && !Backends.HasMultipleActiveTargets
        && !State.IsNativeLiveResizeInProgress
        && !Surfaces.ShouldSuppressDirectCompositionOverlaySplit
        && (frame.BaseVisualTransform.IsIdentity || !frame.BaseVisualTransform.IncludeOverlays)
        && IsDirectCompositionOverlaySplitCandidate(frame);

    private void RecordDirectCompositionOverlaySplitDiagnostics(
        bool splitPresented,
        bool fallback,
        bool overlayPresentFailure,
        bool basePresented,
        bool baseSkipped,
        bool overlayPresented,
        bool overlaySkipped)
    {
        if (!_configuration.EnablePerformanceLogging)
            return;

        if (splitPresented)
            _directCompositionOverlaySplitPresents++;

        if (fallback)
            _directCompositionOverlaySplitFallbacks++;

        if (overlayPresentFailure)
            _directCompositionOverlaySplitOverlayPresentFailures++;

        if (basePresented)
            _directCompositionOverlaySplitBasePresents++;

        if (baseSkipped)
            _directCompositionOverlaySplitBaseSkips++;

        if (overlayPresented)
            _directCompositionOverlaySplitOverlayPresents++;

        if (overlaySkipped)
            _directCompositionOverlaySplitOverlaySkips++;

        var total = _directCompositionOverlaySplitPresents
            + _directCompositionOverlaySplitFallbacks
            + _directCompositionOverlaySplitOverlayPresentFailures;
        if (total < DirectCompositionOverlaySplitDiagnosticInterval)
            return;

        Debug.WriteLine(
            $"[COMPOSITION-D3D11:10X-SPLIT] directCompositionOverlaySplitPresents={_directCompositionOverlaySplitPresents}, " +
            $"fallbacks={_directCompositionOverlaySplitFallbacks}, overlayPresentFailures={_directCompositionOverlaySplitOverlayPresentFailures}, " +
            $"basePresents={_directCompositionOverlaySplitBasePresents}, baseSkips={_directCompositionOverlaySplitBaseSkips}, " +
            $"overlayPresents={_directCompositionOverlaySplitOverlayPresents}, overlaySkips={_directCompositionOverlaySplitOverlaySkips}");

        _directCompositionOverlaySplitPresents = 0;
        _directCompositionOverlaySplitFallbacks = 0;
        _directCompositionOverlaySplitOverlayPresentFailures = 0;
        _directCompositionOverlaySplitBasePresents = 0;
        _directCompositionOverlaySplitBaseSkips = 0;
        _directCompositionOverlaySplitOverlayPresents = 0;
        _directCompositionOverlaySplitOverlaySkips = 0;
    }

    private void ClearDirectCompositionOverlaySplitIfNeeded(bool isDisposed)
    {
        if (!_directCompositionOverlaySplitActive)
            return;

        if (!Surfaces.ClearDirectCompositionOverlay(_deviceResources, isDisposed, State.IsNativeLiveResizeInProgress)
            && _configuration.EnableLogging)
        {
            Debug.WriteLine("[COMPOSITION] DirectComposition overlay split clear failed before final-target fallback.");
        }

        _directCompositionOverlaySplitActive = false;
        _directCompositionOverlaySplitBaseStateValid = false;
        _directCompositionOverlaySplitOverlayStateValid = false;
        _directCompositionOverlaySplitBaseState = default;
        _directCompositionOverlaySplitOverlayState = default;
    }

    internal static DirectCompositionOverlaySplitBaseState CreateDirectCompositionOverlaySplitBaseState(
        MediaCompositionFrame frame,
        D3D11PresentationTargetSnapshot overlayTarget) => new(
        overlayTarget.Revision,
        frame.ClearMode,
        frame.ClearArgb,
        frame.WashArgb,
        frame.HasBackground,
        frame.HasNativeBackground,
        frame.HasRetainedBackground,
        frame.BackgroundRevision,
        frame.BackgroundWidth,
        frame.BackgroundHeight,
        frame.BackgroundStride,
        frame.EffectiveBackgroundStretchMode,
        frame.BaseVisualTransform,
        frame.BackgroundNativeResource,
        frame.BackgroundNativeWidth,
        frame.BackgroundNativeHeight);

    private static DirectCompositionOverlaySplitOverlayState CreateDirectCompositionOverlaySplitOverlayState(
        CdgRenderSnapshot snapshot,
        D3D11PresentationTargetSnapshot overlayTarget) => new(
        overlayTarget.Revision,
        snapshot.Revision,
        snapshot.PaletteRevision,
        overlayTarget.Width,
        overlayTarget.Height);

    private static MediaCompositionFrame AsDirectCompositionBaseFrame(MediaCompositionFrame frame) => frame with
    {
        SuppressCdgOverlayDraw = true,
        Reason = string.IsNullOrWhiteSpace(frame.Reason)
            ? "directcomposition base"
            : frame.Reason + " directcomposition base"
    };

    public void CleanupResources()
    {
        _directCompositionOverlaySplitActive = false;
        _directCompositionOverlaySplitBaseStateValid = false;
        _directCompositionOverlaySplitBaseState = default;
        MediaCompositor.Reset();
        ResourceCleaner.CleanupResources();
    }

    public D3D11VideoProcessorPresenter VideoProcessorPresenter { get; }

    public D3D11PresentationDiagnostics Diagnostics { get; }

    public D3D11PresentationBackendSet Backends { get; }

    public D3D11PresentationSurfaceController Surfaces { get; }

    public D3D11PresentationState State { get; }

    public D3D11MediaCompositionPresenter MediaCompositor { get; }

    public D3D11FrameUploadResources UploadResources { get; }

    public D3D11PresentationResourceCleaner ResourceCleaner { get; }

    public D3D11PresentationFramePresenter FramePresenter { get; }

    internal readonly record struct DirectCompositionOverlaySplitBaseState(
        long TargetRevision,
        MediaCompositionClearMode ClearMode,
        uint ClearArgb,
        uint WashArgb,
        bool HasBackground,
        bool HasNativeBackground,
        bool HasRetainedBackground,
        long BackgroundRevision,
        int BackgroundWidth,
        int BackgroundHeight,
        int BackgroundStride,
        MediaCompositionStretchMode BackgroundStretchMode,
        BaseVisualTransform BaseVisualTransform,
        NativeVideoFrameResource? BackgroundNativeResource,
        int BackgroundNativeWidth,
        int BackgroundNativeHeight);

    private readonly record struct DirectCompositionOverlaySplitOverlayState(
        long TargetRevision,
        long SnapshotRevision,
        long PaletteRevision,
        int TargetWidth,
        int TargetHeight);
}
