#pragma warning disable IDE0130
namespace FatzBombMedia.MediaControl;

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
    MediaPresentationSurfaceKind Kind,
    IntPtr NativeHandle,
    int PixelWidth,
    int PixelHeight,
    double DpiScaleX = 1.0,
    double DpiScaleY = 1.0)
{
    /// <summary>The conventional id for the primary framework-control surface.</summary>
    public const string PrimarySurfaceId = "primary";

    /// <summary>Creates a native HWND descriptor.</summary>
    public static MediaPresentationSurfaceDescriptor NativeHwnd(
        string id,
        MediaPresentationSurfaceRole role,
        IntPtr hwnd,
        int pixelWidth,
        int pixelHeight,
        double dpiScaleX = 1.0,
        double dpiScaleY = 1.0) => new(
            string.IsNullOrWhiteSpace(id) ? PrimarySurfaceId : id.Trim(),
            role,
            MediaPresentationSurfaceKind.NativeHwnd,
            hwnd,
            pixelWidth,
            pixelHeight,
            dpiScaleX,
            dpiScaleY);

    /// <summary>Gets whether the descriptor has enough information to attach a surface.</summary>
    public bool IsValid => Kind != MediaPresentationSurfaceKind.None
        && !string.IsNullOrWhiteSpace(Id)
        && PixelWidth > 0
        && PixelHeight > 0
        && (Kind != MediaPresentationSurfaceKind.NativeHwnd || NativeHandle != IntPtr.Zero);

    /// <summary>Creates a copy with a new pixel size.</summary>
    public MediaPresentationSurfaceDescriptor WithSize(int pixelWidth, int pixelHeight) => this with
    {
        PixelWidth = pixelWidth,
        PixelHeight = pixelHeight
    };
}
