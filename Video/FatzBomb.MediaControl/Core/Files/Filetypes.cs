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
    public static readonly HashSet<string> Archive = new(StringComparer.OrdinalIgnoreCase) { ".7z", ".rar", ".zip", ".tar", ".gz", ".bz2", ".xz", ".tgz", ".tbz2" };
    public static readonly HashSet<string> Image = new(StringComparer.OrdinalIgnoreCase) { ".bmp", ".gif", ".jpg", ".jpeg", ".png", ".tif", ".tiff" }; //.webp
    public static readonly HashSet<string> Cdg = new(StringComparer.OrdinalIgnoreCase) { ".cdg" };
    // Text subtitle MVP formats. Bitmap subtitle formats remain unsupported.
    public static readonly HashSet<string> Subtitle = new(StringComparer.OrdinalIgnoreCase) { ".srt", ".ass", ".ssa" };

    public static bool IsAudio(string filename) => Audio.Contains(Path.GetExtension(filename));
    public static bool IsVideo(string filename) => Video.Contains(Path.GetExtension(filename));
    public static bool IsArchive(string filename) => Archive.Contains(Path.GetExtension(filename));
    public static bool IsImage(string filename) => Image.Contains(Path.GetExtension(filename));
    public static bool IsCdg(string filename) => Cdg.Contains(Path.GetExtension(filename));
    public static bool IsSubtitle(string filename) => Subtitle.Contains(Path.GetExtension(filename));
}
