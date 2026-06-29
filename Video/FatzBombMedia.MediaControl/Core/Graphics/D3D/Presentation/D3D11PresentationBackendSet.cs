using System.Diagnostics;
using FatzBombMedia.MediaControl.Core.Graphics.Composition.D3D11;
using FatzBombMedia.MediaControl.Core.Graphics.D3D.Device;
using FatzBombMedia.MediaControl.Core.Graphics.D3D.Presentation.DirectComposition;
using FatzBombMedia.MediaControl.Core.Graphics.D3D.Presentation.NativeHwnd;
using Vortice.Direct3D11;
using Vortice.Mathematics;

namespace FatzBombMedia.MediaControl.Core.Graphics.D3D.Presentation;

/// <summary>
/// Owns the currently-supported D3D11 presentation backends and centralizes the
/// backend selection order. Stage 10M keeps the existing primary backend behavior
/// while allowing additional native HWND swap-chain targets to receive the same
/// composed frame through the unified compositor path.
/// </summary>
internal sealed class D3D11PresentationBackendSet(
    D3D11NativeHwndSwapChainBackend nativeHwnd,
    ID3D11UiSharedSurfacePresentationBackend uiSharedSurface,
    D3D11DirectCompositionSingleTargetBackend directComposition,
    Func<D3D11NativeHwndSwapChainBackend>? createSecondaryNativeHwndBackend = null) : IDisposable
{
    private readonly Dictionary<string, D3D11NativeHwndSwapChainBackend> _secondaryNativeHwndTargets = [];
    private readonly Func<D3D11NativeHwndSwapChainBackend>? _createSecondaryNativeHwndBackend = createSecondaryNativeHwndBackend;

    public const string DirectCompositionTargetId = "directcomposition-final";
    public const string NativeHwndTargetId = "native-hwnd-final";
    public const string UiSharedSurfaceTargetId = "ui-shared-final";
    public const string SecondaryNativeHwndTargetPrefix = "native-hwnd-secondary:";

    public D3D11NativeHwndSwapChainBackend NativeHwnd { get; } = nativeHwnd;
    public ID3D11UiSharedSurfacePresentationBackend UiSharedSurface { get; } = uiSharedSurface;
    public D3D11DirectCompositionSingleTargetBackend DirectComposition { get; } = directComposition;

    /// <summary>
    /// Primary backend priority is intentional. DirectComposition is the most specific native
    /// layered mode, then plain native HWND, then UI shared-surface fallback. Secondary targets do
    /// not change this primary identity; they are fan-out outputs for the same composed frame.
    /// </summary>
    public ID3D11PresentationBackend? Active =>
        DirectComposition.IsActive ? DirectComposition : NativeHwnd.IsActive ? NativeHwnd : UiSharedSurface.IsActive ? UiSharedSurface : null;

    public D3D11PresentationBackendKind ActiveKind => Active?.Kind ?? D3D11PresentationBackendKind.None;

    public ID3D11Texture2D? ActiveTargetTexture => Active?.TargetTexture;

    public bool HasNativePresentingBackend => ActiveKind is D3D11PresentationBackendKind.NativeHwndSwapChain
        or D3D11PresentationBackendKind.DirectCompositionSingleTarget;

    public bool HasUiSharedSurfaceBackend => ActiveKind == D3D11PresentationBackendKind.UiSharedSurface;

    public bool HasMultipleActiveTargets => EnumerateActiveTargetBackends().Skip(1).Any();

    public bool OwnsTexture(ID3D11Texture2D? texture) => texture != null
            && ((NativeHwnd.IsActive && ReferenceEquals(texture, NativeHwnd.TargetTexture))
                || (DirectComposition.IsActive && ReferenceEquals(texture, DirectComposition.TargetTexture))
                || (UiSharedSurface.IsActive && ReferenceEquals(texture, UiSharedSurface.TargetTexture))
                || _secondaryNativeHwndTargets.Values.Any(target => target.IsActive && ReferenceEquals(texture, target.TargetTexture)));

    public bool CreateSecondaryNativeHwndTarget(D3D11DeviceResources deviceResources, string targetId, IntPtr hwnd, int width, int height)
    {
        if (_createSecondaryNativeHwndBackend == null
            || string.IsNullOrWhiteSpace(targetId)
            || hwnd == IntPtr.Zero
            || width <= 0
            || height <= 0)
        {
            return false;
        }

        string normalizedId = NormalizeSecondaryNativeHwndTargetId(targetId);
        if (!_secondaryNativeHwndTargets.TryGetValue(normalizedId, out D3D11NativeHwndSwapChainBackend? backend))
        {
            backend = _createSecondaryNativeHwndBackend();
            _secondaryNativeHwndTargets.Add(normalizedId, backend);
        }

        if (backend.Matches(hwnd, width, height))
        {
            return true;
        }

        return backend.Create(deviceResources, hwnd, width, height);
    }

    public bool RemoveSecondaryTarget(string targetId)
    {
        string normalizedId = NormalizeSecondaryNativeHwndTargetId(targetId);
        if (!_secondaryNativeHwndTargets.Remove(normalizedId, out D3D11NativeHwndSwapChainBackend? backend))
        {
            return false;
        }

        backend.Dispose();
        return true;
    }

    public IEnumerable<D3D11PresentationBackendTarget> EnumerateActiveTargetBackends()
    {
        if (DirectComposition.IsActive)
        {
            yield return new D3D11PresentationBackendTarget(DirectCompositionTargetId, DirectComposition, DirectComposition.Hwnd, "DirectCompositionNativeLayers");
        }

        if (NativeHwnd.IsActive)
        {
            yield return new D3D11PresentationBackendTarget(NativeHwndTargetId, NativeHwnd, NativeHwnd.Hwnd, "NativeHwndSwapChain");
        }

        if (UiSharedSurface.IsActive)
        {
            yield return new D3D11PresentationBackendTarget(UiSharedSurfaceTargetId, UiSharedSurface, IntPtr.Zero, "UiSharedSurface");
        }

        foreach (KeyValuePair<string, D3D11NativeHwndSwapChainBackend> pair in _secondaryNativeHwndTargets.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            if (pair.Value.IsActive)
            {
                yield return new D3D11PresentationBackendTarget(pair.Key, pair.Value, pair.Value.Hwnd, "NativeHwndSwapChain");
            }
        }
    }

    public void Present(int liveResizeFlag, bool syncImmediate)
    {
        ID3D11PresentationBackend? active = Active;
        if (active == null || active.Kind == D3D11PresentationBackendKind.UiSharedSurface)
        {
            return;
        }

        active.Present(liveResizeFlag, syncImmediate);
    }

    /// <summary>
    /// Presents or invalidates the active backend after a complete frame has been rendered.
    /// Native backends are DXGI-presented immediately; the WPF backend is notified through
    /// UI shared-surface dirty-rect invalidation.
    /// </summary>
    public void NotifyFramePresented(
        int liveResizeFlag,
        bool syncImmediate,
        Func<bool> isDisposed,
        Action framePresented)
    {
        ID3D11PresentationBackend? active = Active;
        if (active == null)
        {
            return;
        }

        NotifyBackendPresented(active, liveResizeFlag, syncImmediate, isDisposed, framePresented);
    }

    public void NotifyTargetsPresented(
        IReadOnlyList<D3D11PresentationTargetSnapshot> targets,
        int liveResizeFlag,
        bool syncImmediate,
        Func<bool> isDisposed,
        Action<D3D11PresentationTargetSnapshot> framePresented)
    {
        foreach (D3D11PresentationTargetSnapshot target in targets)
        {
            if (!target.IsValid
                || !TryGetBackend(target.TargetId, target.BackendKind, out ID3D11PresentationBackend? backend)
                || backend is null)
            {
                continue;
            }

            NotifyBackendPresented(
                backend,
                liveResizeFlag,
                syncImmediate,
                isDisposed,
                () => framePresented(target));
        }
    }

    /// <summary>
    /// Clears the active backend's base target to black. This is backend-facing presentation
    /// cleanup, used when switching to audio-only media or when a new surface is created.
    /// DirectComposition overlay transparency is intentionally separate because it targets
    /// the overlay swap chain, not the base presentation target.
    /// </summary>
    public bool ClearActiveTargetToBlack(D3D11DeviceResources deviceResources, bool flush = true)
    {
        ID3D11Texture2D? target = ActiveTargetTexture;
        if (deviceResources.Device == null || deviceResources.Context == null || target == null)
        {
            return false;
        }

        return ClearTargetToBlack(deviceResources, target, flush);
    }

    public void ClearPresentedTargets(
        D3D11DeviceResources deviceResources,
        IReadOnlyList<D3D11PresentationTargetSnapshot> targets,
        int liveResizeFlag,
        bool syncImmediate,
        Func<bool> isDisposed,
        Action<D3D11PresentationTargetSnapshot> framePresented)
    {
        if (targets.Count == 0)
        {
            return;
        }

        foreach (D3D11PresentationTargetSnapshot target in targets)
        {
            _ = ClearTargetToBlack(deviceResources, target.Texture, flush: false);
        }

        if (DirectComposition.IsActive)
        {
            DirectComposition.ClearOverlayToTransparent(deviceResources);
        }

        NotifyTargetsPresented(targets, liveResizeFlag, syncImmediate, isDisposed, framePresented);
        deviceResources.Context?.Flush();
    }

    /// <summary>
    /// Clears the base frame and, for DirectComposition, the retained overlay chain. Then
    /// immediately presents/invalidates the active backend so stale video frames disappear
    /// when the current media has no visual layer.
    /// </summary>
    public void ClearPresentedFrame(
        D3D11DeviceResources deviceResources,
        int liveResizeFlag,
        bool syncImmediate,
        Func<bool> isDisposed,
        Action framePresented)
    {
        if (Active == null)
        {
            return;
        }

        _ = ClearActiveTargetToBlack(deviceResources, flush: false);

        if (DirectComposition.IsActive)
        {
            DirectComposition.ClearOverlayToTransparent(deviceResources);
        }

        NotifyFramePresented(liveResizeFlag, syncImmediate, isDisposed, framePresented);
        deviceResources.Context?.Flush();
    }

    public void Cleanup()
    {
        DirectComposition.Dispose();
        NativeHwnd.Dispose();
        UiSharedSurface.Dispose();

        foreach (D3D11NativeHwndSwapChainBackend target in _secondaryNativeHwndTargets.Values)
        {
            target.Dispose();
        }

        _secondaryNativeHwndTargets.Clear();
    }

    public void Dispose() => Cleanup();

    private bool TryGetBackend(string targetId, D3D11PresentationBackendKind kind, out ID3D11PresentationBackend? backend)
    {
        backend = null;
        if (targetId == D3D11CompositionTargetDescriptor.PrimaryFinalTargetId)
        {
            backend = Active;
        }
        else if (kind == D3D11PresentationBackendKind.DirectCompositionSingleTarget && targetId == DirectCompositionTargetId)
        {
            backend = DirectComposition;
        }
        else if (kind == D3D11PresentationBackendKind.NativeHwndSwapChain && targetId == NativeHwndTargetId)
        {
            backend = NativeHwnd;
        }
        else if (kind == D3D11PresentationBackendKind.UiSharedSurface && targetId == UiSharedSurfaceTargetId)
        {
            backend = UiSharedSurface;
        }
        else if (kind == D3D11PresentationBackendKind.NativeHwndSwapChain)
        {
            _ = _secondaryNativeHwndTargets.TryGetValue(NormalizeSecondaryNativeHwndTargetId(targetId), out D3D11NativeHwndSwapChainBackend? secondary);
            backend = secondary;
        }

        return backend?.IsActive == true;
    }

    private void NotifyBackendPresented(
        ID3D11PresentationBackend backend,
        int liveResizeFlag,
        bool syncImmediate,
        Func<bool> isDisposed,
        Action framePresented)
    {
        if (backend.Kind == D3D11PresentationBackendKind.UiSharedSurface)
        {
            UiSharedSurface.NotifyDirty(isDisposed, framePresented);
            return;
        }

        backend.Present(liveResizeFlag, syncImmediate);
        framePresented();
    }

    private static bool ClearTargetToBlack(D3D11DeviceResources deviceResources, ID3D11Texture2D? target, bool flush)
    {
        if (deviceResources.Device == null || deviceResources.Context == null || target == null)
        {
            return false;
        }

        try
        {
            using ID3D11RenderTargetView renderTargetView = deviceResources.Device.CreateRenderTargetView(target);
            deviceResources.Context.ClearRenderTargetView(renderTargetView, new Color4(0f, 0f, 0f, 1f));
            if (flush)
            {
                deviceResources.Context.Flush();
            }

            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VorticeD3D11] Presentation target black clear failed: {ex.Message}");
            return false;
        }
    }

    private static string NormalizeSecondaryNativeHwndTargetId(string targetId)
    {
        string trimmed = string.IsNullOrWhiteSpace(targetId) ? "secondary" : targetId.Trim();
        return trimmed.StartsWith(SecondaryNativeHwndTargetPrefix, StringComparison.Ordinal)
            ? trimmed
            : SecondaryNativeHwndTargetPrefix + trimmed;
    }
}
