using FatzMedia.Video.Core.Graphics.Composition;
using FatzMedia.Video.Core.Graphics.Composition.Planning;
using FatzMedia.Video.Core.Graphics.D3D.Device;
using FatzMedia.Video.Core.Graphics.D3D.Presentation.DirectComposition;
using FatzMedia.Video.Core.Graphics.D3D.Presentation.Managed;
using FatzMedia.Video.Core.Graphics.D3D.Presentation.NativeHwnd;
using FatzMedia.Video.Core.Graphics.D3D.Presentation.Shared;
using FatzMedia.Video.Core.Graphics.Rendering;
using Vortice.Direct3D11;
using Vortice.Mathematics;

namespace FatzMedia.Video.Core.Graphics.D3D.Presentation;

/// <summary>
/// Owns the currently-supported D3D11 presentation backends and centralizes the
/// backend selection order. Stage 10M keeps the existing primary backend behavior
/// while allowing additional native HWND swap-chain targets to receive the same
/// composed frame through the unified compositor path. Stage 027A slice 5 adds offscreen
/// secondary targets (managed frame sinks and shared textures), one backend instance per
/// secondary surface, so a composited UI (Avalonia) can show one player on several screens.
/// </summary>
internal sealed class D3D11PresentationBackendSet(
    D3D11NativeHwndSwapChainBackend nativeHwnd,
    D3D11DirectCompositionSingleTargetBackend directComposition,
    D3D11ManagedFrameSinkBackend managedFrameSink,
    Func<D3D11NativeHwndSwapChainBackend>? createSecondaryNativeHwndBackend = null,
    D3D11SharedTextureBackend? sharedTexture = null) : IDisposable
{
    // Secondary targets in ordinal id order, the order they are composed in. Sorted lists, so the
    // per-frame enumeration below is an index loop with no LINQ or iterator (ENG-026).
    private readonly SortedList<string, D3D11NativeHwndSwapChainBackend> _secondaryNativeHwndTargets = new(StringComparer.Ordinal);
    // Managed frame-sink or shared-texture backends of secondary surfaces, by prefixed target id.
    private readonly SortedList<string, ID3D11PresentationBackend> _secondaryOffscreenTargets = new(StringComparer.Ordinal);
    private readonly Func<D3D11NativeHwndSwapChainBackend>? _createSecondaryNativeHwndBackend = createSecondaryNativeHwndBackend;
    // The device the secondary native windows were created on: a queued resize that cannot be
    // applied re-creates the swap chain on it.
    private D3D11DeviceResources? _secondaryDeviceResources;
    private readonly RendererDiagnosticLatch _clearFailure = new();

    public const string DirectCompositionTargetId = "directcomposition-final";
    public const string DirectCompositionOverlayTargetId = "directcomposition-overlay";
    public const string NativeHwndTargetId = "native-hwnd-final";
    public const string ManagedFrameSinkTargetId = "managed-frame-sink-final";
    public const string SharedTextureTargetId = "shared-texture-final";
    public const string SecondaryNativeHwndTargetPrefix = "native-hwnd-secondary:";
    public const string SecondaryOffscreenTargetPrefix = "offscreen-secondary:";

    public D3D11NativeHwndSwapChainBackend NativeHwnd { get; } = nativeHwnd;
    public D3D11DirectCompositionSingleTargetBackend DirectComposition { get; } = directComposition;
    public D3D11ManagedFrameSinkBackend ManagedFrameSink { get; } = managedFrameSink;
    public D3D11SharedTextureBackend SharedTexture { get; } = sharedTexture ?? new D3D11SharedTextureBackend();

    /// <summary>
    /// Primary backend priority is intentional. DirectComposition is the most specific native
    /// layered mode, then plain native HWND. Secondary targets do not change this primary identity;
    /// they are fan-out outputs for the same composed frame.
    /// </summary>
    public ID3D11PresentationBackend? Active =>
        SharedTexture.IsActive
            ? SharedTexture
            : ManagedFrameSink.IsActive
                ? ManagedFrameSink
                : DirectComposition.IsActive ? DirectComposition : NativeHwnd.IsActive ? NativeHwnd : null;

    /// <summary>Whether an offscreen primary (managed frame sink or shared texture) owns presentation.</summary>
    public bool IsOffscreenPrimaryActive => SharedTexture.IsActive || ManagedFrameSink.IsActive;

    public D3D11PresentationBackendKind ActiveKind => Active?.Kind ?? D3D11PresentationBackendKind.None;

    public ID3D11Texture2D? ActiveTargetTexture => Active?.TargetTexture;

    public bool HasMultipleActiveTargets => CountActiveTargetBackends() > 1;

    public bool OwnsTexture(ID3D11Texture2D? texture)
    {
        if (texture == null)
            return false;

        if ((NativeHwnd.IsActive && ReferenceEquals(texture, NativeHwnd.TargetTexture))
            || (DirectComposition.IsActive && ReferenceEquals(texture, DirectComposition.TargetTexture))
            || (ManagedFrameSink.IsActive && ReferenceEquals(texture, ManagedFrameSink.TargetTexture))
            || (SharedTexture.IsActive && ReferenceEquals(texture, SharedTexture.TargetTexture)))
        {
            return true;
        }

        IList<D3D11NativeHwndSwapChainBackend> natives = _secondaryNativeHwndTargets.Values;
        for (var i = 0; i < natives.Count; i++)
            if (natives[i].IsActive && ReferenceEquals(texture, natives[i].TargetTexture))
                return true;

        IList<ID3D11PresentationBackend> offscreen = _secondaryOffscreenTargets.Values;
        for (var i = 0; i < offscreen.Count; i++)
            if (offscreen[i].IsActive && ReferenceEquals(texture, offscreen[i].TargetTexture))
                return true;

        return false;
    }

    public bool CreateManagedFrameSinkTarget(
        D3D11DeviceResources deviceResources,
        IMediaPresentationFrameSink sink,
        int width,
        int height) =>
        ManagedFrameSink.Matches(sink, width, height)
        || ManagedFrameSink.Create(deviceResources, sink, width, height);

    public bool CreateSecondaryNativeHwndTarget(D3D11DeviceResources deviceResources, string targetId, IntPtr hwnd, int width, int height)
    {
        if (IsOffscreenPrimaryActive
            || _createSecondaryNativeHwndBackend == null
            || string.IsNullOrWhiteSpace(targetId)
            || hwnd == IntPtr.Zero
            || width <= 0
            || height <= 0)
            return false;

        var normalizedId = NormalizeSecondaryNativeHwndTargetId(targetId);
        // A surface id changes kind (an offscreen window reopened as a native one): one target per id.
        RemoveSecondaryOffscreenTarget(targetId);
        if (!_secondaryNativeHwndTargets.TryGetValue(normalizedId, out D3D11NativeHwndSwapChainBackend? backend))
        {
            backend = _createSecondaryNativeHwndBackend();
            _secondaryNativeHwndTargets.Add(normalizedId, backend);
        }

        _secondaryDeviceResources = deviceResources;
        if (backend.Matches(hwnd, width, height))
            return true;

        // ENG-026: a resize of the same window queues ResizeBuffers for the next frame boundary,
        // as the primary does, instead of re-creating the swap chain.
        if (backend.IsActive && backend.Hwnd == hwnd)
        {
            backend.QueueResize(width, height);
            return true;
        }

        return backend.Create(deviceResources, hwnd, width, height);
    }

    /// <summary>
    /// Applies the queued resizes of the secondary native windows at a frame boundary (ENG-026).
    /// A window whose buffers cannot be resized gets a new swap chain at its new size.
    /// </summary>
    public void ApplyPendingSecondaryResizes()
    {
        if (_secondaryDeviceResources is not { } deviceResources)
            return;

        IList<D3D11NativeHwndSwapChainBackend> natives = _secondaryNativeHwndTargets.Values;
        for (var i = 0; i < natives.Count; i++)
            _ = natives[i].ApplyPendingResizeOrRecreate(deviceResources);
    }

    /// <summary>
    /// Stage 027A slice 5: creates (or re-creates at a new size) the managed frame-sink target of
    /// a secondary surface. Each secondary has its own offscreen texture and readback.
    /// </summary>
    public bool CreateSecondaryManagedFrameSinkTarget(
        D3D11DeviceResources deviceResources,
        string targetId,
        IMediaPresentationFrameSink sink,
        int width,
        int height)
    {
        if (string.IsNullOrWhiteSpace(targetId) || width <= 0 || height <= 0)
            return false;

        D3D11ManagedFrameSinkBackend backend = GetOrAddSecondaryOffscreenTarget(targetId, static () => new D3D11ManagedFrameSinkBackend());
        if (backend.Matches(sink, width, height) || backend.Create(deviceResources, sink, width, height))
            return true;

        RemoveSecondaryOffscreenTarget(targetId);
        return false;
    }

    /// <summary>
    /// Stage 027A slice 5: creates (or re-exports at a new size) the shared-texture target of a
    /// secondary surface, with its own pair of keyed-mutex textures. <paramref name="reason"/>
    /// says why it was declined.
    /// </summary>
    public bool CreateSecondarySharedTextureTarget(
        D3D11DeviceResources deviceResources,
        string targetId,
        IMediaSharedGpuImageConsumer consumer,
        int width,
        int height,
        out string? reason)
    {
        reason = null;
        if (string.IsNullOrWhiteSpace(targetId) || width <= 0 || height <= 0)
        {
            reason = "the secondary surface has no id or size";
            return false;
        }

        D3D11SharedTextureBackend backend = GetOrAddSecondaryOffscreenTarget(targetId, static () => new D3D11SharedTextureBackend());
        if (backend.Matches(consumer, width, height) || backend.Create(deviceResources, consumer, width, height, out reason))
            return true;

        RemoveSecondaryOffscreenTarget(targetId);
        return false;
    }

    public bool RemoveSecondaryTarget(string targetId)
    {
        var removedOffscreen = RemoveSecondaryOffscreenTarget(targetId);
        var normalizedId = NormalizeSecondaryNativeHwndTargetId(targetId);
        if (!_secondaryNativeHwndTargets.TryGetValue(normalizedId, out D3D11NativeHwndSwapChainBackend? backend))
            return removedOffscreen;

        _ = _secondaryNativeHwndTargets.Remove(normalizedId);
        backend.Dispose();
        return true;
    }

    /// <summary>Gets the secondary offscreen backends by target id (tests and diagnostics).</summary>
    internal IReadOnlyDictionary<string, ID3D11PresentationBackend> SecondaryOffscreenTargets => _secondaryOffscreenTargets;

    private TBackend GetOrAddSecondaryOffscreenTarget<TBackend>(string targetId, Func<TBackend> create)
        where TBackend : class, ID3D11PresentationBackend
    {
        // One target per surface id: a native window of the same id, or a target of the other
        // offscreen kind (the adapter fell back from shared textures to its frame sink), goes.
        var normalizedId = NormalizeSecondaryOffscreenTargetId(targetId);
        var nativeId = NormalizeSecondaryNativeHwndTargetId(targetId);
        if (_secondaryNativeHwndTargets.TryGetValue(nativeId, out D3D11NativeHwndSwapChainBackend? native))
        {
            _ = _secondaryNativeHwndTargets.Remove(nativeId);
            native.Dispose();
        }

        if (_secondaryOffscreenTargets.TryGetValue(normalizedId, out ID3D11PresentationBackend? existing))
        {
            if (existing is TBackend matching)
                return matching;

            existing.Dispose();
        }

        TBackend backend = create();
        _secondaryOffscreenTargets[normalizedId] = backend;
        return backend;
    }

    private bool RemoveSecondaryOffscreenTarget(string targetId)
    {
        var normalizedId = NormalizeSecondaryOffscreenTargetId(targetId);
        if (!_secondaryOffscreenTargets.TryGetValue(normalizedId, out ID3D11PresentationBackend? backend))
            return false;

        _ = _secondaryOffscreenTargets.Remove(normalizedId);
        backend.Dispose();
        return true;
    }

    /// <summary>The active targets in composition order, in a new list (tests and diagnostics).</summary>
    public IEnumerable<D3D11PresentationBackendTarget> EnumerateActiveTargetBackends()
    {
        var targets = new List<D3D11PresentationBackendTarget>();
        CollectActiveTargetBackends(targets);
        return targets;
    }

    /// <summary>
    /// Fills <paramref name="targets"/> (cleared first) with the active targets in composition
    /// order. Allocation-free once the list has grown, for the per-frame path (ENG-026).
    /// </summary>
    public void CollectActiveTargetBackends(List<D3D11PresentationBackendTarget> targets)
    {
        targets.Clear();

        // No-mixed-target policy: while an offscreen primary (managed frame sink or shared
        // texture) owns presentation, secondary native HWND targets are retained so they resume
        // when a native primary returns, but they are neither enumerated nor presented. Offscreen
        // secondaries (Stage 027A slice 5) are enumerated with any primary, or none: they present
        // into their own textures and never wait on a swap chain.
        if (SharedTexture.IsActive)
        {
            targets.Add(new D3D11PresentationBackendTarget(SharedTextureTargetId, SharedTexture, IntPtr.Zero, "SharedTextureD3D11"));
            AddSecondaryOffscreenTargets(targets);
            return;
        }

        if (ManagedFrameSink.IsActive)
        {
            targets.Add(new D3D11PresentationBackendTarget(ManagedFrameSinkTargetId, ManagedFrameSink, IntPtr.Zero, "WpfCompositedD3D11"));
            AddSecondaryOffscreenTargets(targets);
            return;
        }

        if (DirectComposition.IsActive)
            targets.Add(new D3D11PresentationBackendTarget(DirectCompositionTargetId, DirectComposition, DirectComposition.Hwnd, "DirectCompositionNativeLayers"));

        if (NativeHwnd.IsActive)
            targets.Add(new D3D11PresentationBackendTarget(NativeHwndTargetId, NativeHwnd, NativeHwnd.Hwnd, "NativeHwndSwapChain"));

        IList<string> nativeIds = _secondaryNativeHwndTargets.Keys;
        IList<D3D11NativeHwndSwapChainBackend> natives = _secondaryNativeHwndTargets.Values;
        for (var i = 0; i < natives.Count; i++)
            if (natives[i].IsActive)
                targets.Add(new D3D11PresentationBackendTarget(nativeIds[i], natives[i], natives[i].Hwnd, "NativeHwndSwapChain"));

        AddSecondaryOffscreenTargets(targets);
    }

    /// <summary>How many targets <see cref="CollectActiveTargetBackends"/> would list.</summary>
    private int CountActiveTargetBackends()
    {
        var count = 0;
        IList<ID3D11PresentationBackend> offscreen = _secondaryOffscreenTargets.Values;
        for (var i = 0; i < offscreen.Count; i++)
            if (offscreen[i].IsActive)
                count++;

        if (IsOffscreenPrimaryActive)
            return count + 1;

        if (DirectComposition.IsActive)
            count++;
        if (NativeHwnd.IsActive)
            count++;

        IList<D3D11NativeHwndSwapChainBackend> natives = _secondaryNativeHwndTargets.Values;
        for (var i = 0; i < natives.Count; i++)
            if (natives[i].IsActive)
                count++;

        return count;
    }

    private void AddSecondaryOffscreenTargets(List<D3D11PresentationBackendTarget> targets)
    {
        IList<string> ids = _secondaryOffscreenTargets.Keys;
        IList<ID3D11PresentationBackend> backends = _secondaryOffscreenTargets.Values;
        for (var i = 0; i < backends.Count; i++)
        {
            ID3D11PresentationBackend backend = backends[i];
            if (backend.IsActive)
            {
                targets.Add(new D3D11PresentationBackendTarget(
                    ids[i],
                    backend,
                    IntPtr.Zero,
                    backend.Kind == D3D11PresentationBackendKind.SharedTexture ? "SharedTextureD3D11" : "WpfCompositedD3D11"));
            }
        }
    }

    /// <summary>
    /// Presents or invalidates the active backend after a complete frame has been rendered.
    /// Native backends are DXGI-presented immediately. Returns true only when the frame was
    /// actually presented; <paramref name="framePresented"/> is only raised on success so a
    /// failed presentation does not surface as a presented frame.
    /// </summary>
    public bool NotifyFramePresented(
        int liveResizeFlag,
        bool syncImmediate,
        Func<bool> isDisposed,
        Action framePresented)
    {
        ID3D11PresentationBackend? active = Active;
        if (active == null || !PresentBackend(active, liveResizeFlag, syncImmediate, isDisposed))
            return false;

        framePresented();
        return true;
    }

    /// <summary>
    /// Presents every valid target and reports success when the primary target presented. If no
    /// primary is attached, any successfully presented secondary keeps the presentation active.
    /// Individual secondary failures do not turn a successful primary present into a failed frame.
    /// </summary>
    public bool NotifyTargetsPresented(
        IReadOnlyList<D3D11PresentationTargetSnapshot> targets,
        int liveResizeFlag,
        bool syncImmediate,
        Func<bool> isDisposed,
        Action<D3D11PresentationTargetSnapshot> framePresented)
    {
        // Index loop and no per-target closure: this runs every frame (ENG-026). A target that
        // cannot present still counts, so a missing primary backend fails the frame.
        var outcome = new CompositionPresentOutcome();
        for (var i = 0; i < targets.Count; i++)
        {
            D3D11PresentationTargetSnapshot target = targets[i];
            var presented = target.IsValid
                && TryGetBackend(target.TargetId, target.BackendKind, out ID3D11PresentationBackend? backend)
                && backend is not null
                && PresentBackend(backend, liveResizeFlag, syncImmediate, isDisposed);
            if (presented)
                framePresented(target);

            outcome.Record(target.TargetId == CompositionTargetDescriptor.PrimaryFinalTargetId, presented);
        }

        return outcome.Succeeded;
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
        return deviceResources.Device != null && deviceResources.Context != null && target != null && ClearTargetToBlack(deviceResources, target, flush);
    }

    public void Dispose() => Cleanup();

    private bool TryGetBackend(string targetId, D3D11PresentationBackendKind kind, out ID3D11PresentationBackend? backend)
    {
        backend = null;
        if (targetId == CompositionTargetDescriptor.PrimaryFinalTargetId)
            backend = Active;
        else if (kind == D3D11PresentationBackendKind.DirectCompositionSingleTarget && targetId == DirectCompositionTargetId)
            backend = DirectComposition;
        else if (kind == D3D11PresentationBackendKind.NativeHwndSwapChain && targetId == NativeHwndTargetId)
            backend = NativeHwnd;
        else if (kind == D3D11PresentationBackendKind.NativeHwndSwapChain)
        {
            _ = _secondaryNativeHwndTargets.TryGetValue(NormalizeSecondaryNativeHwndTargetId(targetId), out D3D11NativeHwndSwapChainBackend? secondary);
            backend = secondary;
        }
        else if (kind == D3D11PresentationBackendKind.ManagedFrameSink && targetId == ManagedFrameSinkTargetId)
            backend = ManagedFrameSink;
        else if (kind == D3D11PresentationBackendKind.SharedTexture && targetId == SharedTextureTargetId)
            backend = SharedTexture;
        else if (kind is D3D11PresentationBackendKind.ManagedFrameSink or D3D11PresentationBackendKind.SharedTexture
            && _secondaryOffscreenTargets.TryGetValue(targetId, out ID3D11PresentationBackend? offscreen)
            && offscreen.Kind == kind)
        {
            backend = offscreen;
        }

        return backend?.IsActive == true;
    }

    /// <summary>Presents one backend; true when it presented and the owner is still alive.</summary>
    private static bool PresentBackend(
        ID3D11PresentationBackend backend,
        int liveResizeFlag,
        bool syncImmediate,
        Func<bool> isDisposed) =>
        !isDisposed()
        && backend.Present(liveResizeFlag, syncImmediate)
        && !isDisposed();

    /// <summary>
    /// Disposes the primary backends (DirectComposition, native HWND, managed frame sink, shared
    /// texture) while retaining secondary targets. Used when a primary surface is recreated,
    /// resized via surface recreation, or detached so secondary displays survive the primary
    /// transition.
    /// </summary>
    public void CleanupPrimary()
    {
        DirectComposition.Dispose();
        NativeHwnd.Dispose();
        // Cleanup, not Dispose: the frame sink's readback buffer survives a re-creation at a new
        // size (ENG-026); Cleanup below releases it.
        ManagedFrameSink.Cleanup();
        SharedTexture.Dispose();
    }

    public void Cleanup()
    {
        CleanupPrimary();
        ManagedFrameSink.Dispose();
        _secondaryDeviceResources = null;

        foreach (D3D11NativeHwndSwapChainBackend target in _secondaryNativeHwndTargets.Values)
        {
            target.Dispose();
        }

        _secondaryNativeHwndTargets.Clear();

        foreach (ID3D11PresentationBackend target in _secondaryOffscreenTargets.Values)
            target.Dispose();

        _secondaryOffscreenTargets.Clear();
    }

    private bool ClearTargetToBlack(D3D11DeviceResources deviceResources, ID3D11Texture2D? target, bool flush) =>
        ClearTarget(deviceResources, target, new Color4(0f, 0f, 0f, 1f), flush);

    private bool ClearTarget(D3D11DeviceResources deviceResources, ID3D11Texture2D? target, Color4 color, bool flush)
    {
        if (deviceResources.Device == null || deviceResources.Context == null || target == null)
            return false;

        try
        {
            using ID3D11RenderTargetView renderTargetView = deviceResources.Device.CreateRenderTargetView(target);
            deviceResources.Context.ClearRenderTargetView(renderTargetView, color);
            if (flush)
                deviceResources.Context.Flush();

            return true;
        }
        catch (Exception ex)
        {
            _ = _clearFailure.Raise(MediaDiagnosticLevel.Warning, "D3D11", $"Presentation target black clear failed: {ex.Message}", ex);
            return false;
        }
    }

    /// <summary>The surface id inside a secondary target id (either prefix), for target filters.</summary>
    public static string ToSecondarySurfaceId(string targetId) =>
        targetId.StartsWith(SecondaryNativeHwndTargetPrefix, StringComparison.Ordinal)
            ? targetId[SecondaryNativeHwndTargetPrefix.Length..]
            : targetId.StartsWith(SecondaryOffscreenTargetPrefix, StringComparison.Ordinal)
                ? targetId[SecondaryOffscreenTargetPrefix.Length..]
                : targetId;

    private static string NormalizeSecondaryOffscreenTargetId(string targetId) =>
        SecondaryOffscreenTargetPrefix + (string.IsNullOrWhiteSpace(targetId) ? "secondary" : targetId.Trim());

    private static string NormalizeSecondaryNativeHwndTargetId(string targetId)
    {
        var trimmed = string.IsNullOrWhiteSpace(targetId) ? "secondary" : targetId.Trim();
        return trimmed.StartsWith(SecondaryNativeHwndTargetPrefix, StringComparison.Ordinal)
            ? trimmed
            : SecondaryNativeHwndTargetPrefix + trimmed;
    }
}
