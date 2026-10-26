namespace FatzBomb.MediaControl.Player.Shared;

/// <summary>
/// The renderer a test player is asked for (<c>--renderer=</c>, Stage 046A slice 3). Neutral: each
/// host maps it to the renderer factories it has and logs what it chose.
/// </summary>
public enum PlayerRendererRequest
{
    /// <summary>
    /// GPU first (the default): see <see cref="PlayerRendererRules.AutoOrder"/>. Direct3D11 on
    /// Windows (the GPU, then WARP), else Vulkan; the software renderer last.
    /// </summary>
    Auto,

    /// <summary>The platform's GPU renderer on a hardware device: Direct3D11 on the GPU on Windows, Vulkan elsewhere.</summary>
    Gpu,

    /// <summary>Direct3D11 on WARP (Windows' software Direct3D driver), with software decoding. Windows only.</summary>
    Warp,

    /// <summary>The software renderer, with software decoding (also <c>--software</c>).</summary>
    Software,

    /// <summary>The Vulkan renderer (frame sink or shared GPU image, as the adapter allows).</summary>
    Vulkan,

    /// <summary>Direct3D11 with its own device choice (the GPU, else WARP), without the software fallback. Windows only.</summary>
    Direct3D11,
}

/// <summary>The renderer-name parser and the GPU-first <c>auto</c> rule every test player shares.</summary>
public static class PlayerRendererRules
{
    /// <summary>The accepted <c>--renderer=</c> names, for warnings.</summary>
    public const string Usage = "auto, gpu, warp, software, vulkan or direct3d11";

    /// <summary>
    /// Parses a <c>--renderer=</c> value (case-insensitive, trimmed). Aliases: <c>hardware</c> for
    /// <c>gpu</c>, <c>cpu</c> for <c>software</c>, <c>d3d11</c> for <c>direct3d11</c>.
    /// </summary>
    public static bool TryParse(string? value, out PlayerRendererRequest request)
    {
        PlayerRendererRequest? parsed = value?.Trim().ToLowerInvariant() switch
        {
            "auto" => PlayerRendererRequest.Auto,
            "gpu" or "hardware" => PlayerRendererRequest.Gpu,
            "warp" => PlayerRendererRequest.Warp,
            "software" or "cpu" => PlayerRendererRequest.Software,
            "vulkan" => PlayerRendererRequest.Vulkan,
            "direct3d11" or "d3d11" => PlayerRendererRequest.Direct3D11,
            _ => null,
        };
        request = parsed ?? PlayerRendererRequest.Auto;
        return parsed is not null;
    }

    /// <summary>
    /// Gets the request of a command line: a valid <c>--renderer=</c> wins; otherwise
    /// <c>--software</c> asks for <see cref="PlayerRendererRequest.Software"/>; otherwise
    /// <see cref="PlayerRendererRequest.Auto"/>. An invalid value is returned for a warning.
    /// </summary>
    public static (PlayerRendererRequest Request, string? UnknownValue) Resolve(string? renderer, bool software)
    {
        if (renderer is not null && TryParse(renderer, out PlayerRendererRequest request))
            return (request, null);

        return (software ? PlayerRendererRequest.Software : PlayerRendererRequest.Auto, renderer);
    }

    /// <summary>
    /// The GPU-first order <see cref="PlayerRendererRequest.Auto"/> tries, the first usable one
    /// winning (owner decision 4 of Stage 046A):
    /// on Windows Direct3D11 on the GPU, then Direct3D11 on WARP, then the software renderer
    /// (hosts build it as <c>FallbackPresentationCoordinatorFactory(Direct3D11 Auto, Software)</c>,
    /// Direct3D11's own Auto device choice being the GPU, then WARP); elsewhere Vulkan, then the
    /// software renderer. Vulkan is never chosen automatically on Windows (Direct3D11 is the
    /// platform's renderer there); ask for it with <c>--renderer=vulkan</c>.
    /// </summary>
    public static IReadOnlyList<PlayerRendererRequest> AutoOrder(bool windows) => windows
        ? [PlayerRendererRequest.Gpu, PlayerRendererRequest.Warp, PlayerRendererRequest.Software]
        : [PlayerRendererRequest.Vulkan, PlayerRendererRequest.Software];

    /// <summary>Gets the command-line name of <paramref name="request"/> (<c>auto</c>, <c>gpu</c>, ...).</summary>
    public static string Name(PlayerRendererRequest request) => request.ToString().ToLowerInvariant();
}
