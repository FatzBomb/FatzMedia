using FatzMedia.Video.Controllers;
using FatzMedia.Video.Core.Abstractions;
using FatzMedia.Video.Core.Configuration;
using FatzMedia.Video.Core.Threading;
using FatzMedia.Video.Text;

namespace FatzMedia.Video.Vulkan;

/// <summary>
/// Creates a Core presentation coordinator backed by the preview Vulkan renderer. Hosts pass it
/// to any platform factory exactly like the software or Direct3D11 factories.
/// </summary>
public sealed class VulkanPresentationCoordinatorFactory : IMediaPresentationCoordinatorFactory, IMediaPresentationAvailabilityProbe
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

    /// <summary>
    /// Available when <see cref="IsSupported"/> finds a usable device, so a fallback chain moves
    /// past Vulkan on machines without a loader or driver. Like the software renderer it draws
    /// only into managed frame-sink surfaces (or, Stage 027A slice 2, into exported shared GPU
    /// images an adapter's compositor imports; never native windows). It takes hardware frames
    /// when FFmpeg can decode on its device (Stage 045A slice 2: Vulkan Video); otherwise it asks
    /// for software decoding, and the detail says why.
    /// </summary>
    public MediaPresentationAvailability ProbeAvailability()
    {
        try
        {
            using VulkanOffscreenCompositor? compositor = VulkanOffscreenCompositor.TryCreate(out var reason);
            MediaPresentationAvailability availability = compositor is null
                ? MediaPresentationAvailability.Unavailable(reason ?? "Vulkan initialization failed.")
                : MediaPresentationAvailability.Available($"{compositor.DeviceName} ({compositor.DeviceType}), {DescribeVideoDecode(compositor)}");
            // Stage 045A slice 2: hardware frames when FFmpeg can decode on the device.
            return availability with { RequiresSoftwareDecoding = compositor?.VideoDecode is null, RequiresFrameSinkSurface = true };
        }
        catch (Exception ex)
        {
            return MediaPresentationAvailability.Unavailable(ex.Message);
        }
    }

    /// <summary>
    /// Creates the coordinator. Subtitles and karaoke lyrics are drawn by
    /// <see cref="SkiaSubtitleRasterizer"/> with the configuration's subtitle and karaoke styles.
    /// </summary>
    public IMediaPresentationCoordinator Create(IMediaEngine mediaEngine, MediaPlayerConfiguration configuration) =>
        new MediaPresentationCoordinator(
            new VulkanRendererBackend(CreateCompositor),
            new SkiaSubtitleRasterizer(configuration.SubtitleStyle, configuration.KaraokeStyle),
            mediaEngine,
            configuration,
            _precisionWaiterFactory);

    private static VulkanOffscreenCompositor? CreateCompositor()
    {
        VulkanOffscreenCompositor? compositor = VulkanOffscreenCompositor.TryCreate(out var failure);
        if (compositor is null)
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Error, "Vulkan", failure ?? "Vulkan initialization failed.");
        else
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Info, "Vulkan", $"Vulkan device: {compositor.DeviceName} ({compositor.DeviceType}), {DescribeVideoDecode(compositor)}");
        return compositor;
    }

    /// <summary>Whether the device decodes with Vulkan Video, and which codecs, or why not.</summary>
    private static string DescribeVideoDecode(VulkanOffscreenCompositor compositor) =>
        compositor.VideoDecode is { } decode
            ? $"Vulkan Video decoding: {decode.Describe()}"
            : $"no Vulkan Video decoding: {compositor.VideoDecodeUnavailableReason ?? "unknown"}";
}
