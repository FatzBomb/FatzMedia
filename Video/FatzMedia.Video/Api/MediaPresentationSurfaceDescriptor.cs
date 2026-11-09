#pragma warning disable IDE0130
namespace FatzMedia.Video;

/// <summary>
/// Framework-neutral description of a presentation surface supplied by a UI adapter.
/// </summary>
/// <remarks>
/// Adapters create or discover platform surfaces, then pass descriptors to the core media-control
/// session. The media-control engine owns renderer selection, swap-chain creation, retained-layer
/// composition, target fan-out, and presentation policy after a surface is attached.
/// </remarks>
public sealed record MediaPresentationSurfaceDescriptor(
    string Id,
    MediaPresentationSurfaceRole Role,
    MediaNativeSurfaceDescriptor NativeSurface,
    int PixelWidth,
    int PixelHeight,
    double DpiScaleX = 1.0,
    double DpiScaleY = 1.0)
{
    /// <summary>The conventional id for the primary framework-control surface.</summary>
    public const string PrimarySurfaceId = "primary";

    /// <summary>Gets whether the descriptor has enough information to attach a surface.</summary>
    public bool IsValid => !string.IsNullOrWhiteSpace(Id)
        && PixelWidth > 0
        && PixelHeight > 0
        && NativeSurface is { IsValid: true };

    /// <summary>Creates a copy with a new pixel size.</summary>
    public MediaPresentationSurfaceDescriptor WithSize(int pixelWidth, int pixelHeight) => this with
    {
        PixelWidth = pixelWidth,
        PixelHeight = pixelHeight
    };
}
