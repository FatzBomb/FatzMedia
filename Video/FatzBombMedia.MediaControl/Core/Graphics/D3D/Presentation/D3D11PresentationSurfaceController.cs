using FatzBombMedia.MediaControl.Core.Graphics.D3D.Device;
using Vortice.Direct3D11;
using System.Diagnostics;

namespace FatzBombMedia.MediaControl.Core.Graphics.D3D.Presentation;

/// <summary>
/// Tracks the active presentation surface and centralizes backend activation/sizing.
/// The renderer should coordinate frame upload/composition; this controller owns the
/// bookkeeping that says which backend target texture is active and how large it is.
/// </summary>
internal sealed class D3D11PresentationSurfaceController(
    D3D11PresentationBackendSet backends,
    D3D11DeviceResources deviceResources) : ID3D11PresentationSurfaceProvider
{
    private static readonly long DirectCompositionResizeOverlaySplitSuppressionTicks = Math.Max(1, Stopwatch.Frequency / 4);

    public ID3D11Texture2D? TargetTexture { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }
    public long TargetRevision { get; private set; }
    public IntPtr NativeHwnd { get; private set; }
    private D3D11PresentationBackendKind _activeBackendKind = D3D11PresentationBackendKind.None;
    private long _directCompositionOverlaySplitSuppressedUntil;
    public bool LastActivationCreated { get; private set; }
    public bool IsReady => TryGetActiveTargetSnapshot(applyPendingNativeResize: false, reason: null, out _);
    public bool IsNativeHwndActive => backends.NativeHwnd.IsActive;
    public bool IsDirectCompositionActive => backends.DirectComposition.IsActive;
    public bool ShouldSuppressDirectCompositionOverlaySplit =>
        backends.DirectComposition.IsActive
        && (backends.DirectComposition.HasPendingResize
            || _directCompositionOverlaySplitSuppressedUntil > Stopwatch.GetTimestamp());
    public string PresentationBackend => backends.ActiveKind switch
    {
        D3D11PresentationBackendKind.DirectCompositionSingleTarget => "DirectCompositionNativeLayers",
        D3D11PresentationBackendKind.NativeHwndSwapChain => "NativeHwndSwapChain",
        D3D11PresentationBackendKind.UiSharedSurface => "UiSharedSurface",
        _ => "None"
    };


    public bool TryGetActiveTargetSnapshot(
        bool applyPendingNativeResize,
        string? reason,
        out D3D11PresentationTargetSnapshot snapshot)
    {
        IReadOnlyList<D3D11PresentationTargetSnapshot> targets = GetActiveTargetSnapshots(applyPendingNativeResize, reason);
        snapshot = targets.Count > 0 ? targets[0] : default;
        return snapshot.IsValid;
    }

    public IReadOnlyList<D3D11PresentationTargetSnapshot> GetActiveTargetSnapshots(
        bool applyPendingNativeResize,
        string? reason)
    {
        if (applyPendingNativeResize && !ApplyPendingPresentationResizeIfNeeded())
        {
            LogInvalidTarget(reason, "pending presentation resize failed");
            return [];
        }

        var snapshots = new List<D3D11PresentationTargetSnapshot>();
        ID3D11PresentationBackend? primary = backends.Active;
        foreach (D3D11PresentationBackendTarget candidate in backends.EnumerateActiveTargetBackends())
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
                ? Composition.D3D11.D3D11CompositionTargetDescriptor.PrimaryFinalTargetId
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
                isPrimary ? TargetRevision : backend.GetHashCode(),
                Composition.D3D11.D3D11CompositionTargetKind.FinalPresentation));
        }

        if (snapshots.Count == 0)
        {
            LogInvalidTarget(reason, "no active backend targets");
        }

        return snapshots;
    }

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
            Composition.D3D11.D3D11CompositionTargetKind.DirectCompositionOverlay);

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

    public bool CreateUiSharedSurface(
        bool isDisposed,
        D3D11DeviceResources deviceResources,
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
            onCreated => ActivateUiSharedSurface(
                deviceResources,
                width,
                height,
                cleanupBeforeCreate,
                onCreated),
            "UI shared presentation surface",
            "UiSharedSurface",
            enableLogging,
            notifyPresented);

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

    public bool ActivateUiSharedSurface(
        D3D11DeviceResources deviceResources,
        int width,
        int height,
        Action cleanupBeforeCreate,
        Action onCreated)
    {
        if (!EnsureUiSharedSurface(deviceResources, width, height, cleanupBeforeCreate))
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
            backends.DirectComposition.PresentOverlayAndCommit();
        }

        return true;
    }

    public bool EnsureUiSharedSurface(
        D3D11DeviceResources deviceResources,
        int width,
        int height,
        Action cleanupBeforeCreate)
    {
        if (backends.UiSharedSurface.Matches(width, height))
        {
            LastActivationCreated = false;
            return true;
        }

        cleanupBeforeCreate();
        Reset();

        if (!backends.UiSharedSurface.Create(deviceResources, width, height))
        {
            cleanupBeforeCreate();
            return false;
        }

        Adopt(backends.UiSharedSurface);
        LastActivationCreated = true;
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

    public void ClearPresentedFrame(
        D3D11DeviceResources deviceResources,
        bool isDisposed,
        bool liveResize,
        Action raiseFramePresented)
    {
        if (isDisposed)
        {
            return;
        }

        IReadOnlyList<D3D11PresentationTargetSnapshot> targets = GetActiveTargetSnapshots(
            applyPendingNativeResize: true,
            reason: "clear presented frame");
        backends.ClearPresentedTargets(
            deviceResources,
            targets,
            liveResize ? 1 : 0,
            syncImmediate: true,
            () => isDisposed,
            _ => raiseFramePresented());
    }

    public void NotifyFramePresented(
        bool liveResize,
        bool syncImmediate,
        Func<bool> isDisposed,
        Action raiseFramePresented) => backends.NotifyFramePresented(
            liveResize ? 1 : 0,
            syncImmediate,
            isDisposed,
            raiseFramePresented);

    public void NotifyTargetsPresented(
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

    public bool ResizeNativeHwndPresentation(bool isDisposed, int width, int height)
    {
        if (isDisposed || !backends.NativeHwnd.IsActive)
        {
            return false;
        }

        var resized = backends.NativeHwnd.Resize(Math.Max(1, width), Math.Max(1, height));
        SyncNativeHwndState();
        return resized;
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
        var resized = backends.DirectComposition.ApplyPendingResizeIfNeeded(deviceResources);
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
