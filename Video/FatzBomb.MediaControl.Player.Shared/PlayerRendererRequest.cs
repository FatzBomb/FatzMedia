using FatzBomb.MediaControl.Core.Configuration;

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

    /// <summary>
    /// The renderer to rebuild the players with after <paramref name="current"/> lost its GPU device
    /// and could not recover it (ENG-050), or null when there is nothing further (the software
    /// renderer). On Windows: Vulkan to Direct3D11 (another driver path on the same GPU), a GPU
    /// Direct3D11 to WARP, WARP to software; elsewhere every GPU renderer goes to software.
    /// </summary>
    public static PlayerRendererRequest? FallbackAfterDeviceLoss(PlayerRendererRequest current, bool windows) => (current, windows) switch
    {
        (PlayerRendererRequest.Software, _) => null,
        (PlayerRendererRequest.Vulkan, true) => PlayerRendererRequest.Direct3D11,
        (PlayerRendererRequest.Warp, true) => PlayerRendererRequest.Software,
        (_, true) => PlayerRendererRequest.Warp,
        _ => PlayerRendererRequest.Software,
    };

    /// <summary>
    /// <see cref="FallbackAfterDeviceLoss"/> followed until a renderer the window can offer
    /// (<paramref name="isAvailable"/>), or null when none is left.
    /// </summary>
    public static PlayerRendererRequest? NextAvailableAfterDeviceLoss(PlayerRendererRequest current, bool windows, Func<PlayerRendererRequest, bool> isAvailable)
    {
        ArgumentNullException.ThrowIfNull(isAvailable);
        PlayerRendererRequest? next = FallbackAfterDeviceLoss(current, windows);
        while (next is { } candidate && !isAvailable(candidate))
            next = FallbackAfterDeviceLoss(candidate, windows);
        return next;
    }

    /// <summary>
    /// How video is decoded for a renderer and the status/log wording for it (Stage 045A; one table
    /// for every test player since ENG-039). A renderer that presents hardware frames gets
    /// <see cref="VideoDecodingMethod.Auto"/> (hardware where the GPU decodes the codec, else
    /// software): Direct3D11 on a GPU (D3D11VA) and Vulkan on a device with Vulkan Video. A renderer
    /// whose probe says it needs software decoding
    /// (<see cref="Controllers.MediaPresentationAvailability.RequiresSoftwareDecoding"/>) gets
    /// <see cref="VideoDecodingMethod.Software"/>, and <paramref name="renderer"/> words why. Pass
    /// the renderer actually chosen (<see cref="PlayerRendererRequest.Direct3D11"/>,
    /// <see cref="PlayerRendererRequest.Warp"/>, <see cref="PlayerRendererRequest.Vulkan"/> or
    /// <see cref="PlayerRendererRequest.Software"/>); any other value gets the generic wording.
    /// </summary>
    public static (VideoDecodingMethod Decoding, string Detail) DescribeDecoding(PlayerRendererRequest renderer, bool requiresSoftwareDecoding) =>
        (requiresSoftwareDecoding, renderer) switch
        {
            (false, PlayerRendererRequest.Direct3D11) => (VideoDecodingMethod.Auto, "hardware decoding (D3D11VA) where the GPU supports the codec"),
            (false, PlayerRendererRequest.Vulkan) => (VideoDecodingMethod.Auto, "hardware decoding (Vulkan Video) where the GPU supports the codec"),
            (false, _) => (VideoDecodingMethod.Auto, "hardware decoding where the GPU supports the codec"),
            (true, PlayerRendererRequest.Direct3D11 or PlayerRendererRequest.Warp) => (VideoDecodingMethod.Software, "software decoding (the renderer runs on a software rasterizer)"),
            (true, PlayerRendererRequest.Vulkan) => (VideoDecodingMethod.Software, "software decoding (the device has no Vulkan Video decoding)"),
            (true, _) => (VideoDecodingMethod.Software, "software decoding (the renderer takes no hardware frames)"),
        };

    /// <summary>Gets the command-line name of <paramref name="request"/> (<c>auto</c>, <c>gpu</c>, ...).</summary>
    public static string Name(PlayerRendererRequest request) => request.ToString().ToLowerInvariant();
}
