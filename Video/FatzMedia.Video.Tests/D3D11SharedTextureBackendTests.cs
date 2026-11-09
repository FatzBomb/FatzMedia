using FatzMedia.Video.Core.Configuration;
using FatzMedia.Video.Core.Graphics.D3D.Diagnostics;
using FatzMedia.Video.Core.Graphics.D3D.Presentation;
using FatzMedia.Video.Core.Graphics.D3D.Presentation.DirectComposition;
using FatzMedia.Video.Core.Graphics.D3D.Presentation.Managed;
using FatzMedia.Video.Core.Graphics.D3D.Presentation.NativeHwnd;
using FatzMedia.Video.Core.Graphics.D3D.Presentation.Shared;
using FatzMedia.Video.Direct3D11;

namespace FatzMedia.Video.Tests;

/// <summary>
/// Stage 027A slice 2: the hardware-free parts of the Direct3D11 shared-texture backend. Buffer
/// rotation and keyed-mutex key sequencing are <see cref="MediaSharedGpuImageRing"/>'s (portable
/// tests); exporting, the keyed-mutex hand-off and the Avalonia import need a GPU and are manual
/// smoke checks (docs/roadmap/updates/027A_2).
/// </summary>
public sealed class D3D11SharedTextureBackendTests
{
    [Fact]
    public void LuidBytes_AreLowPartThenHighPartLittleEndian() =>
        Assert.Equal(new byte[] { 0x78, 0x56, 0x34, 0x12, 0xFF, 0xFF, 0xFF, 0xFF }, D3D11SharedTextureBackend.LuidBytes(0x12345678, -1));

    [Fact]
    public void ProducerCapabilities_MeetACompositorThatPrefersNtHandles()
    {
        // What Avalonia's Direct3D11 (ANGLE) backend offers, in the adapter's preference order.
        var request = new MediaSharedGpuImageRequest(
        [
            new MediaSharedGpuImageCapability(MediaSharedGpuImageHandleKind.D3D11TextureNtHandle, MediaSharedGpuImageSyncKinds.KeyedMutex),
            new MediaSharedGpuImageCapability(MediaSharedGpuImageHandleKind.D3D11TextureGlobalSharedHandle, MediaSharedGpuImageSyncKinds.KeyedMutex)
        ]);

        Assert.True(request.TrySelect(D3D11SharedTextureBackend.ProducerCapabilities, [], out MediaSharedGpuImageSelection selection, out _));
        Assert.Equal(MediaSharedGpuImageHandleKind.D3D11TextureNtHandle, selection.HandleKind);
        Assert.Equal(MediaSharedGpuImageSyncKinds.KeyedMutex, selection.SyncKind);
    }

    [Fact]
    public void ProducerCapabilities_DoNotMeetADescriptorOnlyCompositor()
    {
        var request = new MediaSharedGpuImageRequest(
            [new MediaSharedGpuImageCapability(MediaSharedGpuImageHandleKind.VulkanOpaqueFd, MediaSharedGpuImageSyncKinds.Semaphores)],
            [MediaSharedGpuSemaphoreHandleKind.VulkanOpaqueFd]);

        Assert.False(request.TrySelect(D3D11SharedTextureBackend.ProducerCapabilities, [], out _, out var reason));
        Assert.Contains("no common image handle kind", reason);
    }

    [Fact]
    public void InactiveSharedTextureBackend_IsNeitherActiveNorEnumerated()
    {
        using var shared = new D3D11SharedTextureBackend();
        using D3D11PresentationBackendSet backends = CreateBackendSet(shared);

        Assert.False(shared.IsActive);
        Assert.Equal(D3D11PresentationBackendKind.SharedTexture, shared.Kind);
        Assert.False(shared.Present(0, syncImmediate: false));
        Assert.Same(shared, backends.SharedTexture);
        Assert.Null(backends.Active);
        Assert.False(backends.IsOffscreenPrimaryActive);
        Assert.Empty(backends.EnumerateActiveTargetBackends());
    }

    private static D3D11PresentationBackendSet CreateBackendSet(D3D11SharedTextureBackend shared)
    {
        var configuration = new MediaPlayerConfiguration();
        var diagnostics = new D3D11PresentationDiagnostics(configuration);
        return new D3D11PresentationBackendSet(
            new D3D11NativeHwndSwapChainBackend(configuration, NativeSwapChainPresentMode.VSync, diagnostics, () => { }, () => { }),
            new D3D11DirectCompositionSingleTargetBackend(configuration, NativeSwapChainPresentMode.VSync, diagnostics, () => { }),
            new D3D11ManagedFrameSinkBackend(),
            sharedTexture: shared);
    }
}
