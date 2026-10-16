using FatzBomb.MediaControl.Controllers;
using FatzBomb.MediaControl.Core.Abstractions;
using FatzBomb.MediaControl.Core.Configuration;
using FatzBomb.MediaControl.Core.Subtitle;
using FatzBomb.MediaControl.Core.Threading;

namespace FatzBomb.MediaControl.Rendering.Vulkan;

/// <summary>
/// Creates a Core presentation coordinator backed by the preview Vulkan renderer. Hosts pass it
/// to any platform factory exactly like the software or Direct3D11 factories.
/// </summary>
public sealed class VulkanPresentationCoordinatorFactory : IMediaPresentationCoordinatorFactory
{
    private readonly IPrecisionWaiterFactory _precisionWaiterFactory;

    public VulkanPresentationCoordinatorFactory()
        : this(new SystemPrecisionWaiterFactory())
    {
    }

    public VulkanPresentationCoordinatorFactory(IPrecisionWaiterFactory precisionWaiterFactory)
    {
        _precisionWaiterFactory = precisionWaiterFactory ?? throw new ArgumentNullException(nameof(precisionWaiterFactory));
    }

    public string BackendName => "Vulkan";

    /// <summary>
    /// Probes for a usable Vulkan device. Returns <see langword="false"/> with a reason when the
    /// loader, an ICD, or a BGRA8-blit-capable device is missing.
    /// </summary>
    public static bool IsSupported(out string? reason)
    {
        using VulkanOffscreenCompositor? compositor = VulkanOffscreenCompositor.TryCreate(out reason);
        return compositor is not null;
    }

    public IMediaPresentationCoordinator Create(IMediaEngine mediaEngine, MediaPlayerConfiguration configuration) =>
        new MediaPresentationCoordinator(
            new VulkanRendererBackend(CreateCompositor),
            new NullSubtitleRasterizer(),
            mediaEngine,
            configuration,
            _precisionWaiterFactory);

    private static VulkanOffscreenCompositor? CreateCompositor()
    {
        VulkanOffscreenCompositor? compositor = VulkanOffscreenCompositor.TryCreate(out var failure);
        if (compositor is null)
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Error, "Vulkan", failure ?? "Vulkan initialization failed.");
        else
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Info, "Vulkan", $"Vulkan device: {compositor.DeviceName} ({compositor.DeviceType})");
        return compositor;
    }

    /// <summary>Subtitle rasterization (DirectWrite/Skia) is a later slice.</summary>
    private sealed class NullSubtitleRasterizer : ISubtitleRasterizer
    {
        public SubtitleRasterResult Render(string text, int width, int height) => default;

        public SubtitleRasterResult RenderKaraoke(KaraokeCueTiming timing, TimeSpan position, int width, int height) => default;

        public void Dispose()
        {
        }
    }
}
