using FatzBombMedia.MediaControl.Core.Graphics.D3D.Device;
using FatzBombMedia.MediaControl.Core.Graphics.D3D.Upload;
using FatzBombMedia.MediaControl.Core.Graphics.D3D.Video;
using Vortice.Direct3D11;
using System.Diagnostics;

namespace FatzBombMedia.MediaControl.Core.Graphics.D3D.Presentation;

/// <summary>
/// Tracks the active presentation surface and centralizes backend activation/sizing.
/// The renderer should coordinate frame upload/composition; this controller owns the
/// bookkeeping that says which backend target texture is active and how large it is.
/// </summary>
internal sealed class D3D11PresentationSurfaceController(D3D11PresentationBackendSet backends) : ID3D11PresentationSurfaceProvider
{
    public ID3D11Texture2D? TargetTexture { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }
    public long TargetRevision { get; private set; }
    public IntPtr NativeHwnd { get; private set; }
    public bool LastActivationCreated { get; private set; }
    public bool IsReady => TryGetActiveTargetSnapshot(applyPendingNativeResize: false, reason: null, out _);
    public bool IsNativeHwndActive => backends.NativeHwnd.IsActive;
    public bool IsDirectCompositionActive => backends.DirectComposition.IsActive;
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
        if (applyPendingNativeResize && !ApplyPendingNativeHwndResizeIfNeeded())
        {
            LogInvalidTarget(reason, "pending native HWND resize failed");
            return Array.Empty<D3D11PresentationTargetSnapshot>();
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
                ? FatzBombMedia.MediaControl.Core.Graphics.Composition.D3D11.D3D11CompositionTargetDescriptor.PrimaryFinalTargetId
                : candidate.TargetId;

            if (isPrimary && (!ReferenceEquals(texture, TargetTexture) || width != Width || height != Height || backend.Kind != backends.ActiveKind))
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
                isPrimary ? TargetRevision : backend.GetHashCode()));
        }

        if (snapshots.Count == 0)
        {
            LogInvalidTarget(reason, "no active backend targets");
        }

        return snapshots;
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

        bool created = backends.CreateSecondaryNativeHwndTarget(deviceResources, targetId, hwnd, width, height);
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

    public void SetDirectCompositionOutputSize(bool isDisposed, int width, int height, bool liveResize)
    {
        if (isDisposed || !backends.DirectComposition.IsActive || Width <= 0 || Height <= 0)
        {
            return;
        }

        backends.DirectComposition.SetOutputSize(
            width,
            height,
            Width,
            Height,
            liveResize);
    }

    public bool UploadDirectCompositionOverlayFrame(
        bool isDisposed,
        D3D11DeviceResources deviceResources,
        D3D11FrameUploadResources uploadResources,
        D3D11VideoProcessorPresenter videoProcessorPresenter,
        ReadOnlySpan<byte> pixelData,
        int width,
        int height,
        int stride,
        bool liveResize) =>
            !isDisposed && backends.DirectComposition.IsActive && width > 0 && height > 0 && backends.DirectComposition.UploadOverlayFrame(
                deviceResources,
                uploadResources.TextureCache,
                videoProcessorPresenter,
                pixelData,
                width,
                height,
                stride,
                liveResize,
                uploadResources.RecordUploadWithCurrentTarget);

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

    private void SyncNativeHwndState()
    {
        if (backends.NativeHwnd.IsActive)
        {
            Adopt(backends.NativeHwnd, backends.NativeHwnd.Hwnd);
        }
    }

    private void Adopt(ID3D11PresentationBackend backend, IntPtr nativeHwnd = default)
    {
        TargetTexture = backend.TargetTexture;
        Width = backend.Width;
        Height = backend.Height;
        NativeHwnd = nativeHwnd;
        TargetRevision++;
    }
}
