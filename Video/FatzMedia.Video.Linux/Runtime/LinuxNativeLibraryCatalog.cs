using FFmpeg.AutoGen;

namespace FatzMedia.Video.Linux.Runtime;

/// <summary>
/// A BASS shared library and the audio extensions it enables. BASS is loaded (audio available)
/// only when every <paramref name="IsCore"/> library loads; the others are plugins.
/// </summary>
sealed record BassLibrary(string FileName, string DllImportName, IReadOnlyList<string> Extensions, bool IsCore);

/// <summary>
/// Linux shared-library names for the host-supplied BASS and FFmpeg binaries. Mirrors the
/// Windows catalog (`bass.dll` -> `libbass.so`) without Windows-only plugins.
/// </summary>
static class LinuxNativeLibraryCatalog
{
    public static IReadOnlyList<BassLibrary> Bass { get; } =
    [
        new("libbass.so", "bass", [".aif", ".aiff", ".mp3", ".mp3pro", ".ogg", ".wav", ".mod", ".it", ".s3m", ".xm", ".mo3", ".mtm", ".umx"], true),
        new("libbass_fx.so", "bass_fx", [], true),
        new("libbass_aac.so", "bass_aac", [".aac", ".m4a", ".m4b"], false),
        new("libbass_ac3.so", "bass_ac3", [".ac3"], false),
        new("libbassflac.so", "bassflac", [".flac"], false),
        new("libbassmidi.so", "bassmidi", [".kar", ".mid", ".midi", ".rmi"], false),
        new("libbassopus.so", "bassopus", [".opus"], false),
        new("libbasswv.so", "basswv", [".wv"], false),
    ];

    /// <summary>
    /// FFmpeg shared libraries in dependency order. The libraries reference each other by
    /// soname and usually carry no <c>$ORIGIN</c> run path, so they are preloaded in this order
    /// before FFmpeg.AutoGen resolves them.
    /// </summary>
    public static IReadOnlyList<string> FfmpegInLoadOrder { get; } =
    [
        $"libavutil.so.{ffmpeg.LIBAVUTIL_VERSION_MAJOR}",
        $"libswresample.so.{ffmpeg.LIBSWRESAMPLE_VERSION_MAJOR}",
        $"libswscale.so.{ffmpeg.LIBSWSCALE_VERSION_MAJOR}",
        $"libavcodec.so.{ffmpeg.LIBAVCODEC_VERSION_MAJOR}",
        $"libavformat.so.{ffmpeg.LIBAVFORMAT_VERSION_MAJOR}",
        $"libavfilter.so.{ffmpeg.LIBAVFILTER_VERSION_MAJOR}",
        $"libavdevice.so.{ffmpeg.LIBAVDEVICE_VERSION_MAJOR}",
    ];

    /// <summary>FFmpeg libraries playback cannot work without.</summary>
    public static IReadOnlySet<string> RequiredFfmpeg { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        $"libavutil.so.{ffmpeg.LIBAVUTIL_VERSION_MAJOR}",
        $"libswresample.so.{ffmpeg.LIBSWRESAMPLE_VERSION_MAJOR}",
        $"libswscale.so.{ffmpeg.LIBSWSCALE_VERSION_MAJOR}",
        $"libavcodec.so.{ffmpeg.LIBAVCODEC_VERSION_MAJOR}",
        $"libavformat.so.{ffmpeg.LIBAVFORMAT_VERSION_MAJOR}",
    };

    /// <summary>Video container extensions registered once FFmpeg loads (same set as Windows).</summary>
    public static IReadOnlyList<string> VideoExtensions { get; } =
    [
        ".3g2", ".3gp", ".asf", ".avi", ".f4v", ".flv", ".m2ts", ".m2v", ".m4v", ".mkv", ".mov", ".mp4", ".mpeg",
        ".mpg", ".mts", ".mxf", ".ogm", ".ogv", ".qt", ".rm", ".rmvb", ".ts", ".vob", ".webm", ".wmv",
    ];

    /// <summary>
    /// Maps a managed <c>DllImport</c> library name (as used by ManagedBass) to its Linux file
    /// name, or <see langword="null"/> when the name is not a BASS library.
    /// </summary>
    public static string? MapDllImportName(string libraryName)
    {
        if (string.IsNullOrWhiteSpace(libraryName))
            return null;

        var name = libraryName.Trim();
        if (name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            name = name[..^4];
        if (name.StartsWith("lib", StringComparison.Ordinal) && name.EndsWith(".so", StringComparison.Ordinal))
            name = name[3..^3];

        foreach (var library in Bass)
        {
            if (string.Equals(library.DllImportName, name, StringComparison.OrdinalIgnoreCase))
                return library.FileName;
        }

        return null;
    }
}
