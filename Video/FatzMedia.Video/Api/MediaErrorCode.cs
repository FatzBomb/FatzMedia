#pragma warning disable IDE0130
namespace FatzMedia.Video;

/// <summary>
/// Categorizes host-facing media-control failures for diagnosability without
/// relying on <c>false</c> returns or Debug-only output.
/// </summary>
public enum MediaErrorCode
{
    /// <summary>No error. Reserved default value.</summary>
    None = 0,

    /// <summary>The failure could not be classified into a more specific category.</summary>
    Unknown,

    /// <summary>The requested media file or source could not be found on disk.</summary>
    FileNotFound,

    /// <summary>A required native library (FFmpeg) could not be located or loaded.</summary>
    NativeLibraryMissing,

    /// <summary>The file format is not supported by the current pipeline.</summary>
    UnsupportedFormat,

    /// <summary>The media engine failed to open an otherwise supported source.</summary>
    OpenFailed,

    /// <summary>The media engine failed to decode or read frames from an opened source.</summary>
    DecodeFailed,

    /// <summary>An internal engine error occurred that is not covered by a more specific code.</summary>
    InternalError,

    /// <summary>
    /// The active audio output device was lost or the system default device changed. Playback may
    /// be affected; the host should surface a user-visible diagnostic and decide how to recover.
    /// </summary>
    AudioDeviceLost,

    /// <summary>
    /// The renderer's GPU device was lost and could not be recovered (ENG-050; seen with Vulkan after a
    /// GPU reset on Windows). The player shows no picture until it is recreated with another renderer
    /// (for example Direct3D11 or the software renderer); sound continues. Raised once per loss.
    /// </summary>
    RendererDeviceLost,

    /// <summary>
    /// The source is an online item (Stage 060A) but this player has no web view host to play it.
    /// Hosts play web media through a UI adapter's web view (for example FatzMedia.Video.WebView2).
    /// </summary>
    WebMediaUnavailable,
}
