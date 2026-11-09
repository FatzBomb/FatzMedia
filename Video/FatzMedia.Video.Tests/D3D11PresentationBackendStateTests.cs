using FatzMedia.Video.Core.Configuration;
using FatzMedia.Video.Core.Graphics.D3D.Diagnostics;
using FatzMedia.Video.Core.Graphics.D3D.Presentation;
using FatzMedia.Video.Core.Graphics.D3D.Presentation.DirectComposition;
using FatzMedia.Video.Core.Graphics.D3D.Presentation.Managed;
using FatzMedia.Video.Core.Graphics.D3D.Presentation.NativeHwnd;
using FatzMedia.Video.Core.Graphics.D3D.SwapChain;
using FatzMedia.Video.Core.Graphics.Rendering;
using FatzMedia.Video.Direct3D11;

namespace FatzMedia.Video.Tests;

/// <summary>
/// Pure, hardware-free state tests for the renderer-only presentation-backend changes:
/// the void-to-bool Present contract (frame-presented must not be raised when presentation
/// fails), the primary-only cleanup seam, and the no-mixed-target enumeration guard.
///
/// Manual verification gaps (require a live GPU / D3D11 device and are covered by
/// docs/VALIDATION.md smoke checks):
///   - D3D11ManagedFrameSinkBackend classifies real Map failures and device-removed reasons and
///     latches the "D3D11.DeviceLost" diagnostic until a successful present resets it.
///   - D3D11NativeHwndSwapChainBackend / D3D11DirectCompositionSingleTargetBackend propagate a
///     failed DXGI Present as false so the frame-presented event is suppressed.
///   - Secondary native HWND targets are retained across a primary backend recreate/resize/detach
///     and resume when a native primary returns (needs live swap chains to observe retention).
///   - When ManagedFrameSink is active it is the sole enumerated/presented target (needs a live
///     backend with real textures to activate).
///   - DirectComposition CreateTargetForHwnd/SetContent/AddVisual/SetRoot failures fail creation
///     and route a diagnostic (needs a live DirectComposition device to provoke failures).
/// </summary>
public sealed class D3D11PresentationBackendStateTests
{
    static D3D11PresentationBackendSet CreateInactiveBackendSet()
    {
        var configuration = new MediaPlayerConfiguration();
        var diagnostics = new D3D11PresentationDiagnostics(configuration);
        var nativeHwnd = new D3D11NativeHwndSwapChainBackend(
            configuration,
            NativeSwapChainPresentMode.VSync,
            diagnostics,
            () => { },
            () => { });
        var directComposition = new D3D11DirectCompositionSingleTargetBackend(
            configuration,
            NativeSwapChainPresentMode.VSync,
            diagnostics,
            () => { });
        var managedFrameSink = new D3D11ManagedFrameSinkBackend();
        return new D3D11PresentationBackendSet(nativeHwnd, directComposition, managedFrameSink);
    }

    [Fact]
    public void Active_IsNull_WhenNoBackendIsActive()
    {
        using var backends = CreateInactiveBackendSet();

        Assert.Null(backends.Active);
        Assert.Equal(D3D11PresentationBackendKind.None, backends.ActiveKind);
        Assert.Null(backends.ActiveTargetTexture);
        Assert.False(backends.HasMultipleActiveTargets);
        Assert.Empty(backends.EnumerateActiveTargetBackends());
    }

    [Fact]
    public void NotifyFramePresented_ReturnsFalse_AndDoesNotRaise_WhenNoBackendIsActive()
    {
        using var backends = CreateInactiveBackendSet();
        var framePresented = false;

        var presented = backends.NotifyFramePresented(0, syncImmediate: false, () => false, () => framePresented = true);

        Assert.False(presented);
        Assert.False(framePresented);
    }

    [Fact]
    public void NotifyTargetsPresented_WithEmptyTargets_ReturnsFalseAndDoesNotPresent()
    {
        using var backends = CreateInactiveBackendSet();
        var framePresented = false;

        var allPresented = backends.NotifyTargetsPresented([], 0, syncImmediate: false, () => false, _ => framePresented = true);

        Assert.False(allPresented);
        Assert.False(framePresented);
    }

    [Fact]
    public void OwnsTexture_ReturnsFalse_ForNullTexture()
    {
        using var backends = CreateInactiveBackendSet();

        Assert.False(backends.OwnsTexture(null));
    }

    [Fact]
    public void RemoveSecondaryTarget_ReturnsFalse_WhenTargetDoesNotExist()
    {
        using var backends = CreateInactiveBackendSet();

        Assert.False(backends.RemoveSecondaryTarget("missing-secondary-target"));
    }

    [Fact]
    public void CleanupPrimary_Then_FullCleanup_AreSafe_OnInactiveBackends()
    {
        var backends = CreateInactiveBackendSet();

        backends.CleanupPrimary();
        backends.Cleanup();
        backends.Dispose();

        Assert.Null(backends.Active);
    }

    [Fact]
    public void SwapChainPresenterPresent_ReturnsFalse_WhenSwapChainIsNull()
    {
        var diagnostics = new D3D11PresentationDiagnostics(new MediaPlayerConfiguration());
        var deviceLostReported = false;

        var presented = D3D11SwapChainPresenter.Present(
            swapChain: null,
            NativeSwapChainPresentMode.VSync,
            liveResizeFlag: 0,
            syncImmediate: false,
            diagnostics,
            ref deviceLostReported,
            new RendererDiagnosticLatch());

        Assert.False(presented);
        Assert.False(deviceLostReported);
    }
}
