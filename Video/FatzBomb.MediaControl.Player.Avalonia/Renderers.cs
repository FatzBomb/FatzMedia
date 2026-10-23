using FatzBomb.MediaControl.Controllers;
using FatzBomb.MediaControl.Rendering.Software;
using FatzBomb.MediaControl.Rendering.Vulkan;

namespace FatzBomb.MediaControl.Player.Avalonia;

internal enum RendererKind
{
    Software,
    Vulkan
}

/// <summary>A renderer the player can offer and whether it is usable on this machine.</summary>
internal sealed record RendererOption(RendererKind Kind, bool IsAvailable, string Detail)
{
    public string Label => IsAvailable ? Kind.ToString() : $"{Kind} (unavailable)";
}

/// <summary>
/// Frame-sink renderers for the composited Avalonia surface. Direct3D11 draws to native windows, so
/// it is not offered here (use the WinForms or WPF player for it).
/// </summary>
internal static class Renderers
{
    public static IReadOnlyList<RendererOption> Probe()
    {
        var vulkanUsable = VulkanPresentationCoordinatorFactory.IsSupported(out var reason);
        return
        [
            new RendererOption(RendererKind.Software, true, "CPU composition, software decoding"),
            new RendererOption(RendererKind.Vulkan, vulkanUsable, vulkanUsable ? "Vulkan composition, software decoding" : reason ?? "no usable Vulkan device")
        ];
    }

    /// <summary>Resolves a <c>--renderer=</c> value against what the probe found.</summary>
    public static RendererKind Choose(string requested, IReadOnlyList<RendererOption> options)
    {
        var vulkanUsable = options.Any(static option => option is { Kind: RendererKind.Vulkan, IsAvailable: true });
        return requested switch
        {
            "vulkan" when vulkanUsable => RendererKind.Vulkan,
            "auto" when vulkanUsable => RendererKind.Vulkan,
            _ => RendererKind.Software
        };
    }

    // One factory per player: nothing is shared between players.
    public static IMediaPresentationCoordinatorFactory Create(RendererKind kind) => kind switch
    {
        RendererKind.Vulkan => new VulkanPresentationCoordinatorFactory(),
        _ => new SoftwarePresentationCoordinatorFactory()
    };
}
