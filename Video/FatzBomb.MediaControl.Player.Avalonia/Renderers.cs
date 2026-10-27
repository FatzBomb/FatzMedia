using FatzBomb.MediaControl.Controllers;
using FatzBomb.MediaControl.Core.Configuration;
using FatzBomb.MediaControl.Player.Shared;
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

    /// <summary>Direct3D11 on the GPU. Windows build only (Stage 027A slice 2: shared textures into Avalonia's compositor).</summary>
    Direct3D11,

    /// <summary>Direct3D11 on WARP, with software decoding. Windows build only (Stage 046A slice 3).</summary>
    Warp
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
/// Renderers for the composited Avalonia surface: software and Vulkan everywhere, Direct3D11 on the
/// GPU and on WARP in the Windows build. Each presents through the surface's shared GPU image path
/// when it and Avalonia's compositor support it, else through the frame sink (see the status line).
/// </summary>
internal static class Renderers
{
#if WINDOWS
    private const bool IsWindowsBuild = true;
#else
    private const bool IsWindowsBuild = false;
#endif

    public static IReadOnlyList<RendererOption> Probe()
    {
        List<RendererOption> options =
        [
            Describe(RendererKind.Software, new SoftwarePresentationCoordinatorFactory().ProbeAvailability(), "CPU composition"),
            Describe(RendererKind.Vulkan, new VulkanPresentationCoordinatorFactory().ProbeAvailability(), "Vulkan composition")
        ];
#if WINDOWS
        options.Add(Describe(RendererKind.Direct3D11, Direct3D11(Direct3D11DeviceSelection.Hardware).ProbeAvailability(), "Direct3D11 composition"));
        options.Add(Describe(RendererKind.Warp, Direct3D11(Direct3D11DeviceSelection.Warp).ProbeAvailability(), "Direct3D11 WARP composition"));
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
    /// D3D11VA frames on the GPU through both surface paths, and Vulkan on a device with Vulkan
    /// Video (slice 2: FFmpeg decodes on the renderer's device). The software renderer takes no
    /// hardware frames, Vulkan without Vulkan Video has nothing to decode with (its probe detail
    /// says why), and Direct3D11 on a software rasterizer (WARP) would be handed frames on another
    /// device, so those decode in software. <paramref name="kind"/> only words the reason.
    /// </summary>
    internal static (VideoDecodingMethod Decoding, string Detail) ChooseDecoding(RendererKind kind, MediaPresentationAvailability availability) =>
        (availability.RequiresSoftwareDecoding, kind) switch
        {
            (false, RendererKind.Direct3D11) => (VideoDecodingMethod.Auto, "hardware decoding (D3D11VA) where the GPU supports the codec"),
            (false, RendererKind.Vulkan) => (VideoDecodingMethod.Auto, "hardware decoding (Vulkan Video) where the GPU supports the codec"),
            (false, _) => (VideoDecodingMethod.Auto, "hardware decoding where the GPU supports the codec"),
            (true, RendererKind.Direct3D11 or RendererKind.Warp) => (VideoDecodingMethod.Software, "software decoding (the renderer runs on a software rasterizer)"),
            (true, RendererKind.Vulkan) => (VideoDecodingMethod.Software, "software decoding (the device has no Vulkan Video decoding)"),
            (true, _) => (VideoDecodingMethod.Software, "software decoding (the renderer takes no hardware frames)")
        };

    /// <summary>
    /// Resolves a <c>--renderer=</c> request against what the probe found. <c>auto</c> takes the
    /// first usable renderer of the shared GPU-first order (<see cref="PlayerRendererRules.AutoOrder"/>:
    /// Direct3D11 on the GPU, then WARP, then software in the Windows build; Vulkan, then software
    /// elsewhere), the same rule <c>FallbackPresentationCoordinatorFactory</c> applies in the other
    /// players, over this player's probe results so the toolbar can list them. A renderer that is
    /// not usable here falls back to the auto choice, with a warning.
    /// </summary>
    public static (RendererKind Kind, string? Warning) Choose(PlayerRendererRequest request, IReadOnlyList<RendererOption> options)
    {
        bool Usable(RendererKind kind) => options.Any(option => option.Kind == kind && option.IsAvailable);

        RendererKind auto = PlayerRendererRules.AutoOrder(IsWindowsBuild)
            .Select(ToKind)
            .FirstOrDefault(kind => kind is { } candidate && Usable(candidate)) ?? RendererKind.Software;

        RendererKind? requested = request switch
        {
            PlayerRendererRequest.Auto => auto,
            // Direct3D11 with its own device choice: the GPU, else WARP.
            PlayerRendererRequest.Direct3D11 => Usable(RendererKind.Direct3D11) ? RendererKind.Direct3D11
                : Usable(RendererKind.Warp) ? RendererKind.Warp : null,
            _ => ToKind(request) is { } kind && Usable(kind) ? kind : null,
        };

        return requested is { } chosen
            ? (chosen, null)
            : (auto, $"--renderer={PlayerRendererRules.Name(request)} is not usable here; using {auto} (the auto choice).");
    }

    private static RendererKind? ToKind(PlayerRendererRequest request) => request switch
    {
        PlayerRendererRequest.Gpu => IsWindowsBuild ? RendererKind.Direct3D11 : RendererKind.Vulkan,
        PlayerRendererRequest.Warp when IsWindowsBuild => RendererKind.Warp,
        PlayerRendererRequest.Vulkan => RendererKind.Vulkan,
        PlayerRendererRequest.Software => RendererKind.Software,
        _ => null,
    };

    // One factory per player: nothing is shared between players.
    public static IMediaPresentationCoordinatorFactory Create(RendererKind kind) => kind switch
    {
        RendererKind.Vulkan => new VulkanPresentationCoordinatorFactory(),
#if WINDOWS
        RendererKind.Direct3D11 => Direct3D11(Direct3D11DeviceSelection.Hardware),
        RendererKind.Warp => Direct3D11(Direct3D11DeviceSelection.Warp),
#endif
        _ => new SoftwarePresentationCoordinatorFactory()
    };

#if WINDOWS
    private static Direct3D11PresentationCoordinatorFactory Direct3D11(Direct3D11DeviceSelection device) =>
        WindowsDirect3D11MediaControlPlatformFactory.CreatePresentationFactory(new Direct3D11PresentationOptions { DeviceSelection = device });
#endif
}
