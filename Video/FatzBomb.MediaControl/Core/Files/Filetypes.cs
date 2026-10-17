namespace FatzBomb.MediaControl.Core.Files;

public enum FileType { Unsupported, FileNotFound, Audio, AudioCdg, Video, VideoSubtitle, Archive, Image, Cdg, AudioArchive, VideoArchive, CdgArchive, AudioCdgArchive, UnsupportedArchive }

/// <summary>
/// Accepted extensions grouped by kind. Kept minimal and aligned with bundled codecs/plugins.
/// Also provides helpers to detect media kinds by extension.
/// </summary>
public static class FileTypes
{
    public static readonly HashSet<string> Audio = new(StringComparer.OrdinalIgnoreCase);
    public static readonly HashSet<string> Video = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Audio-only extensions FFmpeg can decode. Filled by <see cref="RegisterFfmpegAudio"/> once
    /// FFmpeg loads. Extensions also in <see cref="Audio"/> play through BASS; the rest are
    /// decoded by FFmpeg and played through the same BASS chain (Stage 033A).
    /// </summary>
    public static readonly HashSet<string> FfmpegAudio = new(StringComparer.OrdinalIgnoreCase);
    public static readonly HashSet<string> Archive = new(StringComparer.OrdinalIgnoreCase) { ".7z", ".rar", ".zip", ".tar", ".gz", ".bz2", ".xz", ".tgz", ".tbz2" };
    public static readonly HashSet<string> Image = new(StringComparer.OrdinalIgnoreCase) { ".bmp", ".gif", ".jpg", ".jpeg", ".png", ".tif", ".tiff" }; //.webp
    public static readonly HashSet<string> Cdg = new(StringComparer.OrdinalIgnoreCase) { ".cdg" };
    // Text subtitle MVP formats. Bitmap subtitle formats remain unsupported.
    public static readonly HashSet<string> Subtitle = new(StringComparer.OrdinalIgnoreCase) { ".srt", ".ass", ".ssa" };

    public static bool IsAudio(string filename) => Audio.Contains(Path.GetExtension(filename));

    /// <summary>True when a BASS plugin or the FFmpeg fallback can play the audio file.</summary>
    public static bool IsPlayableAudio(string filename)
    {
        var extension = Path.GetExtension(filename);
        return Audio.Contains(extension) || FfmpegAudio.Contains(extension);
    }

    /// <summary>
    /// Registers the FFmpeg fallback audio extensions. Platform packages call this after the
    /// FFmpeg libraries load successfully.
    /// </summary>
    public static void RegisterFfmpegAudio() => FfmpegAudio.UnionWith(FfmpegAudioExtensions);

    internal static IReadOnlyList<string> FfmpegAudioExtensions { get; } =
    [
        ".aac", ".ac3", ".aif", ".aifc", ".aiff", ".amr", ".ape", ".au", ".caf", ".dff", ".dsf", ".dts",
        ".eac3", ".flac", ".m4a", ".m4b", ".mka", ".mp2", ".mp3", ".mpc", ".oga", ".ogg", ".opus", ".spx",
        ".tak", ".tta", ".w64", ".wav", ".wma", ".wv",
    ];
    public static bool IsVideo(string filename) => Video.Contains(Path.GetExtension(filename));
    public static bool IsArchive(string filename) => Archive.Contains(Path.GetExtension(filename));
    public static bool IsImage(string filename) => Image.Contains(Path.GetExtension(filename));
    public static bool IsCdg(string filename) => Cdg.Contains(Path.GetExtension(filename));
    public static bool IsSubtitle(string filename) => Subtitle.Contains(Path.GetExtension(filename));
}
