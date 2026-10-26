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
/// A renderer the player can offer, whether it is usable on this machine, and how video is decoded
/// for it (<see cref="Renderers.ChooseDecoding"/>).
/// </summary>
internal sealed record RendererOption(RendererKind Kind, bool IsAvailable, string Detail, VideoDecodingMethod Decoding = VideoDecodingMethod.Software)
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
        var vulkanUsable = VulkanPresentationCoordinatorFactory.IsSupported(out var reason);
        (VideoDecodingMethod softwareDecoding, var softwareDecodingDetail) = ChooseDecoding(RendererKind.Software, rendererRequiresSoftwareDecoding: false);
        (VideoDecodingMethod vulkanDecoding, var vulkanDecodingDetail) = ChooseDecoding(RendererKind.Vulkan, rendererRequiresSoftwareDecoding: false);
        List<RendererOption> options =
        [
            new RendererOption(RendererKind.Software, true, $"CPU composition, {softwareDecodingDetail}", softwareDecoding),
            new RendererOption(
                RendererKind.Vulkan,
                vulkanUsable,
                vulkanUsable ? $"Vulkan composition, {vulkanDecodingDetail}" : reason ?? "no usable Vulkan device",
                vulkanDecoding)
        ];
#if WINDOWS
        MediaPresentationAvailability direct3D = WindowsDirect3D11MediaControlPlatformFactory
            .CreatePresentationFactory(new Direct3D11PresentationOptions())
            .ProbeAvailability();
        (VideoDecodingMethod direct3DDecoding, var direct3DDecodingDetail) = ChooseDecoding(RendererKind.Direct3D11, direct3D.RequiresSoftwareDecoding);
        options.Add(new RendererOption(
            RendererKind.Direct3D11,
            direct3D.IsAvailable,
            direct3D.IsAvailable ? $"Direct3D11 composition ({direct3D.Detail}), {direct3DDecodingDetail}" : direct3D.Detail,
            direct3DDecoding));
#endif
        return options;
    }

    /// <summary>
    /// How video is decoded for <paramref name="kind"/> (Stage 045A): a host choice, paired with
    /// what the renderer can present. Direct3D11 presents hardware-decoded (D3D11VA) frames on the
    /// GPU through both surface paths, so it asks for <see cref="VideoDecodingMethod.Auto"/>
    /// (hardware where the GPU decodes the codec, else software), unless its probe found only a
    /// software rasterizer (WARP): a GPU decoder would hand it frames on another device. The
    /// software and Vulkan renderers cannot present Direct3D11 frames, so they decode in software.
    /// </summary>
    internal static (VideoDecodingMethod Decoding, string Detail) ChooseDecoding(RendererKind kind, bool rendererRequiresSoftwareDecoding) => kind switch
    {
        RendererKind.Direct3D11 when rendererRequiresSoftwareDecoding =>
            (VideoDecodingMethod.Software, "software decoding (the renderer runs on a software rasterizer)"),
        RendererKind.Direct3D11 => (VideoDecodingMethod.Auto, "hardware decoding (D3D11VA) where the GPU supports the codec"),
        RendererKind.Vulkan => (VideoDecodingMethod.Software, "software decoding (the Vulkan renderer takes no hardware frames yet)"),
        _ => (VideoDecodingMethod.Software, "software decoding")
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
