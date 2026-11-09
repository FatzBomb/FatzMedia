#pragma warning disable IDE0130
namespace FatzMedia.Video;

/// <summary>
/// How an adapter's media surface shows the renderer's frames (Stage 046A: moved from the
/// Avalonia adapter so every adapter reports its path the same way).
/// </summary>
public enum MediaSurfacePresentationPath
{
    /// <summary>No surface is attached.</summary>
    None,

    /// <summary>The renderer reads each frame back to the CPU and the surface shows it as a bitmap.</summary>
    FrameSink,

    /// <summary>
    /// The renderer exports double-buffered GPU images that the UI framework's compositor imports
    /// and shows directly (Stage 027A slice 2): no CPU copy.
    /// </summary>
    SharedGpuImage,

    /// <summary>The renderer presents straight into a native window the surface owns (an HWND swap chain).</summary>
    NativeWindow,
}

/// <summary>Text for <see cref="MediaSurfacePresentationPath"/> values, shared by the adapters' diagnostics.</summary>
public static class MediaSurfacePresentationPathText
{
    /// <summary>A short lower-case name of <paramref name="path"/> ("frame sink", "shared GPU image", ...).</summary>
    public static string Describe(MediaSurfacePresentationPath path) => path switch
    {
        MediaSurfacePresentationPath.FrameSink => "frame sink",
        MediaSurfacePresentationPath.SharedGpuImage => "shared GPU image",
        MediaSurfacePresentationPath.NativeWindow => "native window",
        _ => "none",
    };

    /// <summary>
    /// The Info line an adapter raises when a surface's path is chosen:
    /// <c>{surface}: {path} path ({reason})</c>, the reason left out when there is none.
    /// </summary>
    public static string DescribeChoice(string surface, MediaSurfacePresentationPath path, string? reason) =>
        $"{surface}: {Describe(path)} path{(string.IsNullOrEmpty(reason) ? string.Empty : $" ({reason})")}";

    /// <summary>The Info line an adapter raises when it clears its surface because the session is in Audio mode.</summary>
    public static string DescribeAudioModeCleared(string surface) =>
        $"{surface}: cleared, the player is in Audio mode (nothing presents to it)";

    /// <summary>The Warning line an adapter raises when the session does not accept its surface.</summary>
    public static string DescribeAttachRefused(string surface) =>
        $"{surface}: the player did not accept the surface, so nothing is shown on it "
        + "(no renderer, or the renderer rejected the surface; its own diagnostics say why). "
        + "The surface tries again on its next resize.";
}
