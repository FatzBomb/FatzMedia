using FatzBomb.MediaControl.Controllers;
using FatzBomb.MediaControl.Core.Configuration;
using FatzBomb.MediaControl.Rendering.Software;
using FatzBomb.MediaControl.Rendering.Vulkan;
#if WINDOWS
using FatzBomb.MediaControl.Rendering.Direct3D11;
#endif

namespace FatzBomb.MediaControl.Player.Avalonia;

internal enum RendererKind
{
    Software,
    Vulkan,

    /// <summary>Windows build only (Stage 027A slice 2: shared textures into Avalonia's compositor).</summary>
    Direct3D11
}

/// <summary>
/// A renderer the player can offer, whether it is usable on this machine (<see cref="Detail"/>
/// describes its composition, or why it is unusable), and how video is decoded for it
/// (<see cref="Renderers.ChooseDecoding"/>).
/// </summary>
internal sealed record RendererOption(
    RendererKind Kind,
    bool IsAvailable,
    string Detail,
    VideoDecodingMethod Decoding = VideoDecodingMethod.Software,
    string DecodingDetail = "software decoding")
{
    public string Label => IsAvailable ? Kind.ToString() : $"{Kind} (unavailable)";
}

/// <summary>
/// Renderers for the composited Avalonia surface: software and Vulkan everywhere, Direct3D11 in the
/// Windows build. Each presents through the surface's shared GPU image path when it and Avalonia's
/// compositor support it, else through the frame sink (see the status line).
/// </summary>
internal static class Renderers
{
    public static IReadOnlyList<RendererOption> Probe()
    {
        List<RendererOption> options =
        [
            Describe(RendererKind.Software, new SoftwarePresentationCoordinatorFactory().ProbeAvailability(), "CPU composition"),
            Describe(RendererKind.Vulkan, new VulkanPresentationCoordinatorFactory().ProbeAvailability(), "Vulkan composition")
        ];
#if WINDOWS
        options.Add(Describe(
            RendererKind.Direct3D11,
            WindowsDirect3D11MediaControlPlatformFactory.CreatePresentationFactory(new Direct3D11PresentationOptions()).ProbeAvailability(),
            "Direct3D11 composition"));
#endif
        return options;
    }

    private static RendererOption Describe(RendererKind kind, MediaPresentationAvailability availability, string composition)
    {
        (VideoDecodingMethod decoding, var decodingDetail) = ChooseDecoding(kind, availability);
        return new RendererOption(
            kind,
            availability.IsAvailable,
            !availability.IsAvailable ? availability.Detail
                : string.IsNullOrEmpty(availability.Detail) || availability.Detail == composition ? composition
                : $"{composition} ({availability.Detail})",
            decoding,
            decodingDetail);
    }

    /// <summary>
    /// How video is decoded for a renderer (Stage 045A): a host choice, paired with what the
    /// renderer's probe says it can present (<see cref="MediaPresentationAvailability.RequiresSoftwareDecoding"/>).
    /// A renderer that presents hardware frames gets <see cref="VideoDecodingMethod.Auto"/>
    /// (hardware where the GPU decodes the codec, else software): Direct3D11 on a GPU, which keeps
    /// D3D11VA frames on the GPU through both surface paths. The software and Vulkan renderers take
    /// no hardware frames, and Direct3D11 on a software rasterizer (WARP) would be handed frames on
    /// another device, so those decode in software. <paramref name="kind"/> only words the reason.
    /// </summary>
    internal static (VideoDecodingMethod Decoding, string Detail) ChooseDecoding(RendererKind kind, MediaPresentationAvailability availability) =>
        (availability.RequiresSoftwareDecoding, kind) switch
        {
            (false, RendererKind.Direct3D11) => (VideoDecodingMethod.Auto, "hardware decoding (D3D11VA) where the GPU supports the codec"),
            (false, _) => (VideoDecodingMethod.Auto, "hardware decoding where the GPU supports the codec"),
            (true, RendererKind.Direct3D11) => (VideoDecodingMethod.Software, "software decoding (the renderer runs on a software rasterizer)"),
            (true, _) => (VideoDecodingMethod.Software, "software decoding (the renderer takes no hardware frames)")
        };

    /// <summary>Resolves a <c>--renderer=</c> value against what the probe found.</summary>
    public static RendererKind Choose(string requested, IReadOnlyList<RendererOption> options)
    {
        var vulkanUsable = options.Any(static option => option is { Kind: RendererKind.Vulkan, IsAvailable: true });
        var direct3DUsable = options.Any(static option => option is { Kind: RendererKind.Direct3D11, IsAvailable: true });
        return requested switch
        {
            "direct3d11" or "d3d11" when direct3DUsable => RendererKind.Direct3D11,
            "vulkan" when vulkanUsable => RendererKind.Vulkan,
            "auto" when vulkanUsable => RendererKind.Vulkan,
            _ => RendererKind.Software
        };
    }

    // One factory per player: nothing is shared between players.
    public static IMediaPresentationCoordinatorFactory Create(RendererKind kind) => kind switch
    {
        RendererKind.Vulkan => new VulkanPresentationCoordinatorFactory(),
#if WINDOWS
        RendererKind.Direct3D11 => WindowsDirect3D11MediaControlPlatformFactory.CreatePresentationFactory(new Direct3D11PresentationOptions()),
#endif
        _ => new SoftwarePresentationCoordinatorFactory()
    };
}
