using FFmpeg.AutoGen;

namespace FatzMedia.Video.Linux.Runtime;

/// <summary>
/// Linux shared-library names for the host-supplied FFmpeg binaries.
/// </summary>
static class LinuxNativeLibraryCatalog
{
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
}
