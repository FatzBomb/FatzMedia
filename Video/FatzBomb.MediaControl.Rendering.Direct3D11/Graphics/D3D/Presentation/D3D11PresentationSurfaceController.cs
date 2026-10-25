using FatzBomb.MediaControl.Core.Graphics.D3D.Device;
using FatzBomb.MediaControl.Core.Graphics.D3D.Presentation.Shared;
using Vortice.Direct3D11;
using System.Diagnostics;

namespace FatzBomb.MediaControl.Core.Graphics.D3D.Presentation;

/// <summary>
/// Tracks the active presentation surface and centralizes backend activation/sizing.
/// The renderer should coordinate frame upload/composition; this controller owns the
/// bookkeeping that says which backend target texture is active and how large it is.
/// </summary>
internal sealed class D3D11PresentationSurfaceController(
    D3D11PresentationBackendSet backends) : ID3D11PresentationSurfaceProvider
{
    private static readonly long DirectCompositionResizeOverlaySplitSuppressionTicks = Math.Max(1, Stopwatch.Frequency / 4);

    public ID3D11Texture2D? TargetTexture { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }
    public long TargetRevision { get; private set; }
    public IntPtr NativeHwnd { get; private set; }
    private D3D11PresentationBackendKind _activeBackendKind = D3D11PresentationBackendKind.None;
    private long _directCompositionOverlaySplitSuppressedUntil;
    // Reused every frame (ENG-026). Every caller holds the presentation lock.
    private readonly List<D3D11PresentationBackendTarget> _scratchBackends = [];
    private readonly List<D3D11PresentationTargetSnapshot> _frameSnapshots = [];
    private readonly List<D3D11PresentationTargetSnapshot> _probeSnapshots = [];
    public bool LastActivationCreated { get; private set; }
    public bool IsReady => TryGetActiveTargetSnapshot(applyPendingNativeResize: false, reason: null, out _);
    public bool IsDirectCompositionActive => backends.DirectComposition.IsActive;
    public bool ShouldSuppressDirectCompositionOverlaySplit =>
        backends.DirectComposition.IsActive
        && (backends.DirectComposition.HasPendingResize
            || _directCompositionOverlaySplitSuppressedUntil > Stopwatch.GetTimestamp());
    public string PresentationBackend => backends.ActiveKind switch
    {
        D3D11PresentationBackendKind.DirectCompositionSingleTarget => "DirectCompositionNativeLayers",
        D3D11PresentationBackendKind.NativeHwndSwapChain => "NativeHwndSwapChain",
        D3D11PresentationBackendKind.ManagedFrameSink => "WpfCompositedD3D11",
        D3D11PresentationBackendKind.SharedTexture => "SharedTextureD3D11",
        _ => "None"
    };


    public bool TryGetActiveTargetSnapshot(
        bool applyPendingNativeResize,
        string? reason,
        out D3D11PresentationTargetSnapshot snapshot)
    {
        FillActiveTargetSnapshots(_probeSnapshots, applyPendingNativeResize, reason);
        snapshot = _probeSnapshots.Count > 0 ? _probeSnapshots[0] : default;
        _probeSnapshots.Clear();
        return snapshot.IsValid;
    }

    /// <summary>
    /// The active targets for this frame. The list is reused by the next call (ENG-026): callers
    /// use it within the frame, under the presentation lock, and do not keep it.
    /// </summary>
    public IReadOnlyList<D3D11PresentationTargetSnapshot> GetActiveTargetSnapshots(
        bool applyPendingNativeResize,
        string? reason)
    {
        FillActiveTargetSnapshots(_frameSnapshots, applyPendingNativeResize, reason);
        return _frameSnapshots;
    }

    private void FillActiveTargetSnapshots(
        List<D3D11PresentationTargetSnapshot> snapshots,
        bool applyPendingNativeResize,
        string? reason)
    {
        snapshots.Clear();
        if (applyPendingNativeResize && !ApplyPendingPresentationResizeIfNeeded())
        {
            LogInvalidTarget(reason, "pending presentation resize failed");
            return;
        }

        if (applyPendingNativeResize)
            backends.ApplyPendingSecondaryResizes();

        ID3D11PresentationBackend? primary = backends.Active;
        backends.CollectActiveTargetBackends(_scratchBackends);
        foreach (D3D11PresentationBackendTarget candidate in _scratchBackends)
        {
            ID3D11PresentationBackend backend = candidate.Backend;
            ID3D11Texture2D? texture = backend.TargetTexture;
            var width = backend.Width;
            var height = backend.Height;
            if (texture == null || width <= 0 || height <= 0)
            {
                LogInvalidTarget(reason, $"invalid target {candidate.TargetId} texture/dimensions (texture null: {texture == null}, {width}x{height})");
                continue;
            }

            var isPrimary = ReferenceEquals(backend, primary);
            var targetId = isPrimary
                ? Composition.Planning.CompositionTargetDescriptor.PrimaryFinalTargetId
                : candidate.TargetId;

            if (isPrimary && (!ReferenceEquals(texture, TargetTexture)
                || width != Width
                || height != Height
                || candidate.NativeHwnd != NativeHwnd
                || backend.Kind != _activeBackendKind))
            {
                Adopt(backend, candidate.NativeHwnd);
            }

            snapshots.Add(new D3D11PresentationTargetSnapshot(
                targetId,
                texture,
                width,
                height,
                candidate.NativeHwnd,
                backend.Kind,
                candidate.BackendName,
                isPrimary ? TargetRevision : SecondaryRevision(backend, texture),
                Composition.Planning.CompositionTargetKind.FinalPresentation,
                backend.TargetViews));
        }

        _scratchBackends.Clear();
        if (snapshots.Count == 0)
        {
            LogInvalidTarget(reason, "no active backend targets");
        }
    }

    /// <summary>
    /// A secondary target's revision. An offscreen secondary re-creates its texture in the same
    /// backend object (a resize, or a new sink of the same size), and a new texture must not be
    /// taken for the one a retained refresh already drew into.
    /// </summary>
    private static long SecondaryRevision(ID3D11PresentationBackend backend, ID3D11Texture2D texture) =>
        backend.Kind is D3D11PresentationBackendKind.ManagedFrameSink or D3D11PresentationBackendKind.SharedTexture
            ? HashCode.Combine(backend, texture)
            : backend.GetHashCode();

    public bool TryGetDirectCompositionOverlayTargetSnapshot(out D3D11PresentationTargetSnapshot snapshot)
    {
        snapshot = default;
        if (!backends.DirectComposition.IsActive
            || backends.DirectComposition.OverlayTexture == null
            || backends.DirectComposition.Width <= 0
            || backends.DirectComposition.Height <= 0)
        {
            return false;
        }

        snapshot = new D3D11PresentationTargetSnapshot(
            D3D11PresentationBackendSet.DirectCompositionOverlayTargetId,
            backends.DirectComposition.OverlayTexture,
            backends.DirectComposition.Width,
            backends.DirectComposition.Height,
            backends.DirectComposition.Hwnd,
            backends.DirectComposition.Kind,
            "DirectCompositionOverlay",
            TargetRevision,
            Composition.Planning.CompositionTargetKind.DirectCompositionOverlay,
            backends.DirectComposition.OverlayTargetViews);

        return snapshot.IsValid;
    }

    public bool PresentDirectCompositionOverlay(bool isDisposed, bool liveResize) =>
        !isDisposed
        && backends.DirectComposition.IsActive
        && backends.DirectComposition.PresentOverlayAndCommit(liveResize ? 0u : 1u);

    public bool ClearDirectCompositionOverlay(
        D3D11DeviceResources deviceResources,
        bool isDisposed,
        bool liveResize)
    {
        if (isDisposed || !backends.DirectComposition.IsActive)
            return false;

        backends.DirectComposition.ClearOverlayToTransparent(deviceResources);
        return backends.DirectComposition.PresentOverlayAndCommit(liveResize ? 0u : 1u);
    }

    private static void LogInvalidTarget(string? reason, string detail)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return;
        }

        Debug.WriteLine($"[D3D11PresentationSurfaceController] Skipping {reason}: {detail}.");
    }

    public bool OwnsTexture(ID3D11Texture2D? texture) => backends.OwnsTexture(texture);

    public void CleanupBackends() => backends.Cleanup();

    /// <summary>
    /// Disposes only the primary backends, retaining secondary native HWND targets so they
    /// survive a primary surface recreate/resize/detach and resume when a native primary returns.
    /// </summary>
    public void CleanupPrimaryBackends() => backends.CleanupPrimary();

    public void Reset()
    {
        TargetTexture = null;
        Width = 0;
        Height = 0;
        NativeHwnd = IntPtr.Zero;
        _activeBackendKind = D3D11PresentationBackendKind.None;
        _directCompositionOverlaySplitSuppressedUntil = 0;
        LastActivationCreated = false;
        TargetRevision++;
    }

    public bool ActivatePresentationSurface(
        bool isDisposed,
        bool isValidTarget,
        D3D11DeviceResources deviceResources,
        int width,
        int height,
        Func<Action, bool> activateSurface,
        string description,
        string backendName,
        bool enableLogging,
        Action<int, int, bool> notifyPresented) =>
        !isDisposed && isValidTarget && deviceResources.HasDevice && width > 0 && height > 0 && activateSurface(() =>
        {
            notifyPresented(Width, Height, true);

            if (!enableLogging)
                return;

            Debug.WriteLine($"[VorticeD3D11] Created {description} for {width}x{height}");
            Debug.WriteLine($"[VorticeD3D11] Presentation backend: {backendName}");
        });

    public bool CreateNativeHwndSurface(
        bool isDisposed,
        D3D11DeviceResources deviceResources,
        IntPtr hwnd,
        int width,
        int height,
        Action cleanupBeforeCreate,
        bool enableLogging,
        Action<int, int, bool> notifyPresented) => ActivatePresentationSurface(
            isDisposed,
            hwnd != IntPtr.Zero,
            deviceResources,
            width,
            height,
            onCreated => ActivateNativeHwndSurface(
                deviceResources,
                hwnd,
                width,
                height,
                cleanupBeforeCreate,
                onCreated),
            "native HWND swap-chain presentation surface",
            "NativeHwndSwapChain",
            enableLogging,
            notifyPresented);

    public bool CreateSecondaryNativeHwndSurface(
        bool isDisposed,
        D3D11DeviceResources deviceResources,
        string targetId,
        IntPtr hwnd,
        int width,
        int height,
        bool enableLogging)
    {
        if (isDisposed || hwnd == IntPtr.Zero || !deviceResources.HasDevice || width <= 0 || height <= 0)
        {
            return false;
        }

        var created = backends.CreateSecondaryNativeHwndTarget(deviceResources, targetId, hwnd, width, height);
        if (created && enableLogging)
        {
            Debug.WriteLine($"[VorticeD3D11] Created secondary native HWND compositor target '{targetId}' for {width}x{height}");
        }

        return created;
    }

    public bool RemoveSecondaryPresentationTarget(string targetId) => backends.RemoveSecondaryTarget(targetId);

    /// <summary>Stage 027A slice 5: attaches or resizes a secondary surface's managed frame sink.</summary>
    public bool CreateSecondaryManagedFrameSinkSurface(
        bool isDisposed,
        D3D11DeviceResources deviceResources,
        string targetId,
        IMediaPresentationFrameSink sink,
        int width,
        int height,
        bool enableLogging)
    {
        if (isDisposed || !deviceResources.HasDevice || width <= 0 || height <= 0)
            return false;

        var created = backends.CreateSecondaryManagedFrameSinkTarget(deviceResources, targetId, sink, width, height);
        if (created && enableLogging)
            Debug.WriteLine($"[VorticeD3D11] Secondary managed frame-sink target '{targetId}' at {width}x{height}");

        return created;
    }

    /// <summary>Stage 027A slice 5: attaches or resizes a secondary surface's shared textures.</summary>
    public bool CreateSecondarySharedTextureSurface(
        bool isDisposed,
        D3D11DeviceResources deviceResources,
        string targetId,
        IMediaSharedGpuImageConsumer consumer,
        int width,
        int height,
        bool enableLogging,
        out string? failureReason)
    {
        if (isDisposed || !deviceResources.HasDevice || width <= 0 || height <= 0)
        {
            failureReason = isDisposed ? "the renderer is disposed" : "the Direct3D11 device is not ready";
            return false;
        }

        var created = backends.CreateSecondarySharedTextureTarget(deviceResources, targetId, consumer, width, height, out failureReason);
        if (created && enableLogging)
            Debug.WriteLine($"[VorticeD3D11] Secondary shared-texture target '{targetId}' at {width}x{height}");

        return created;
    }

    public bool CreateManagedFrameSinkSurface(
        bool isDisposed,
        D3D11DeviceResources deviceResources,
        IMediaPresentationFrameSink sink,
        int width,
        int height,
        Action cleanupBeforeCreate,
        bool enableLogging,
        Action<int, int, bool> notifyPresented) => ActivatePresentationSurface(
            isDisposed,
            true,
            deviceResources,
            width,
            height,
            onCreated => ActivateManagedFrameSinkSurface(
                deviceResources,
                sink,
                width,
                height,
                cleanupBeforeCreate,
                onCreated),
            "managed BGRA frame-sink presentation surface",
            "WpfCompositedD3D11",
            enableLogging,
            notifyPresented);

    private bool ActivateManagedFrameSinkSurface(
        D3D11DeviceResources deviceResources,
        IMediaPresentationFrameSink sink,
        int width,
        int height,
        Action cleanupBeforeCreate,
        Action onCreated)
    {
        if (!backends.ManagedFrameSink.Matches(sink, width, height))
        {
            cleanupBeforeCreate();
            LastActivationCreated = backends.CreateManagedFrameSinkTarget(
                deviceResources,
                sink,
                width,
                height);
        }
        else
        {
            LastActivationCreated = false;
        }

        if (!backends.ManagedFrameSink.IsActive)
            return false;

        if (LastActivationCreated)
        {
            _ = backends.ClearActiveTargetToBlack(deviceResources);
            onCreated();
        }

        return true;
    }

    /// <summary>
    /// Stage 027A slice 2: activates the shared-texture primary for a UI compositor consumer, or
    /// re-exports it at a new size. <paramref name="failureReason"/> says why it was declined.
    /// </summary>
    public bool CreateSharedTextureSurface(
        bool isDisposed,
        D3D11DeviceResources deviceResources,
        IMediaSharedGpuImageConsumer consumer,
        int width,
        int height,
        Action cleanupBeforeCreate,
        bool enableLogging,
        Action<int, int, bool> notifyPresented,
        out string? failureReason)
    {
        string? reason = null;
        var activated = ActivatePresentationSurface(
            isDisposed,
            true,
            deviceResources,
            width,
            height,
            onCreated =>
            {
                D3D11SharedTextureBackend backend = backends.SharedTexture;
                if (!backend.Matches(consumer, width, height))
                {
                    cleanupBeforeCreate();
                    LastActivationCreated = backend.Create(deviceResources, consumer, width, height, out reason);
                }
                else
                {
                    LastActivationCreated = false;
                }

                if (!backend.IsActive)
                    return false;

                if (LastActivationCreated)
                {
                    _ = backends.ClearActiveTargetToBlack(deviceResources);
                    onCreated();
                }

                return true;
            },
            "shared-texture presentation surface",
            "SharedTextureD3D11",
            enableLogging,
            notifyPresented);
        failureReason = activated ? null : reason ?? (isDisposed ? "the renderer is disposed" : "the Direct3D11 device is not ready");
        return activated;
    }

    public bool CreateDirectCompositionSurface(
        bool isDisposed,
        D3D11DeviceResources deviceResources,
        IntPtr hwnd,
        int width,
        int height,
        Action cleanupBeforeCreate,
        bool enableLogging,
        Action<int, int, bool> notifyPresented) => ActivatePresentationSurface(
            isDisposed,
            hwnd != IntPtr.Zero,
            deviceResources,
            width,
            height,
            onCreated => ActivateDirectCompositionSurface(
                deviceResources,
                hwnd,
                width,
                height,
                cleanupBeforeCreate,
                onCreated),
            "DirectComposition native layered presentation surface",
            "DirectCompositionNativeLayers",
            enableLogging,
            notifyPresented);

    public bool ActivateNativeHwndSurface(
        D3D11DeviceResources deviceResources,
        IntPtr hwnd,
        int width,
        int height,
        Action cleanupBeforeCreate,
        Action onCreated)
    {
        if (!EnsureNativeHwndSurface(deviceResources, hwnd, width, height, cleanupBeforeCreate))
        {
            return false;
        }

        if (LastActivationCreated)
        {
            _ = backends.ClearActiveTargetToBlack(deviceResources);
            onCreated();
        }

        return true;
    }

    public bool ActivateDirectCompositionSurface(
        D3D11DeviceResources deviceResources,
        IntPtr hwnd,
        int width,
        int height,
        Action cleanupBeforeCreate,
        Action onCreated)
    {
        if (!EnsureDirectCompositionSurface(deviceResources, hwnd, width, height, cleanupBeforeCreate))
        {
            return false;
        }

        if (LastActivationCreated)
        {
            _ = backends.ClearActiveTargetToBlack(deviceResources);
            backends.DirectComposition.ClearOverlayToTransparent(deviceResources);
            onCreated();
            _ = backends.DirectComposition.PresentOverlayAndCommit();
        }

        return true;
    }

    public bool EnsureNativeHwndSurface(
        D3D11DeviceResources deviceResources,
        IntPtr hwnd,
        int width,
        int height,
        Action cleanupBeforeCreate)
    {
        if (backends.NativeHwnd.Matches(hwnd, width, height))
        {
            LastActivationCreated = false;
            return true;
        }

        cleanupBeforeCreate();
        Reset();

        if (!backends.NativeHwnd.Create(deviceResources, hwnd, width, height))
        {
            cleanupBeforeCreate();
            return false;
        }

        Adopt(backends.NativeHwnd, hwnd);
        LastActivationCreated = true;
        return true;
    }

    public bool EnsureDirectCompositionSurface(
        D3D11DeviceResources deviceResources,
        IntPtr hwnd,
        int width,
        int height,
        Action cleanupBeforeCreate)
    {
        if (backends.DirectComposition.Matches(hwnd, width, height))
        {
            LastActivationCreated = false;
            return true;
        }

        cleanupBeforeCreate();
        Reset();

        if (!backends.DirectComposition.Create(deviceResources, hwnd, width, height))
        {
            cleanupBeforeCreate();
            return false;
        }

        Adopt(backends.DirectComposition, hwnd);
        LastActivationCreated = true;
        return true;
    }

    public void QueueDirectCompositionResize(
        bool isDisposed,
        int width,
        int height)
    {
        if (isDisposed || !backends.DirectComposition.IsActive)
        {
            return;
        }

        var requestedWidth = Math.Max(1, width);
        var requestedHeight = Math.Max(1, height);
        if (requestedWidth != backends.DirectComposition.Width
            || requestedHeight != backends.DirectComposition.Height)
        {
            SuppressDirectCompositionOverlaySplitForResize();
        }

        backends.DirectComposition.QueueResize(requestedWidth, requestedHeight);
    }

    public bool NotifyFramePresented(
        bool liveResize,
        bool syncImmediate,
        Func<bool> isDisposed,
        Action raiseFramePresented) => backends.NotifyFramePresented(
            liveResize ? 1 : 0,
            syncImmediate,
            isDisposed,
            raiseFramePresented);

    public bool NotifyTargetsPresented(
        IReadOnlyList<D3D11PresentationTargetSnapshot> targets,
        bool liveResize,
        bool syncImmediate,
        Func<bool> isDisposed,
        Action<D3D11PresentationTargetSnapshot> framePresented) => backends.NotifyTargetsPresented(
            targets,
            liveResize ? 1 : 0,
            syncImmediate,
            isDisposed,
            framePresented);

    public void QueueNativeHwndResize(bool isDisposed, int width, int height)
    {
        if (isDisposed || !backends.NativeHwnd.IsActive)
        {
            return;
        }

        backends.NativeHwnd.QueueResize(Math.Max(1, width), Math.Max(1, height));
    }

    public bool ApplyPendingNativeHwndResizeIfNeeded()
    {
        if (!backends.NativeHwnd.IsActive)
        {
            return true;
        }

        var resized = backends.NativeHwnd.ApplyPendingResizeIfNeeded();
        SyncNativeHwndState();
        return resized;
    }

    public bool ApplyPendingPresentationResizeIfNeeded()
    {
        var directCompositionResized = ApplyPendingDirectCompositionResizeIfNeeded();
        var nativeHwndResized = ApplyPendingNativeHwndResizeIfNeeded();
        return directCompositionResized && nativeHwndResized;
    }

    private bool ApplyPendingDirectCompositionResizeIfNeeded()
    {
        if (!backends.DirectComposition.IsActive)
        {
            return true;
        }

        var hadPendingResize = backends.DirectComposition.HasPendingResize;
        var resized = backends.DirectComposition.ApplyPendingResizeIfNeeded();
        if (resized && hadPendingResize)
        {
            SuppressDirectCompositionOverlaySplitForResize();
        }

        SyncDirectCompositionState();
        return resized;
    }

    private void SuppressDirectCompositionOverlaySplitForResize() =>
        _directCompositionOverlaySplitSuppressedUntil = Stopwatch.GetTimestamp() + DirectCompositionResizeOverlaySplitSuppressionTicks;

    private void SyncNativeHwndState()
    {
        if (backends.NativeHwnd.IsActive)
        {
            Adopt(backends.NativeHwnd, backends.NativeHwnd.Hwnd);
        }
    }

    private void SyncDirectCompositionState()
    {
        if (backends.DirectComposition.IsActive)
        {
            Adopt(backends.DirectComposition, backends.DirectComposition.Hwnd);
        }
    }

    private void Adopt(ID3D11PresentationBackend backend, IntPtr nativeHwnd = default)
    {
        var structuralChange = Width != backend.Width
            || Height != backend.Height
            || NativeHwnd != nativeHwnd
            || _activeBackendKind != backend.Kind;

        TargetTexture = backend.TargetTexture;
        Width = backend.Width;
        Height = backend.Height;
        NativeHwnd = nativeHwnd;
        _activeBackendKind = backend.Kind;

        if (structuralChange)
        {
            TargetRevision++;
        }
    }
}
