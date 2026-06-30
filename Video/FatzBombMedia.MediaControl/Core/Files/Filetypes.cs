using FatzBombMedia.MediaControl.Core.Abstractions;

namespace FatzBombMedia.MediaControl.Core.Files;

public enum FileType { Unsupported, FileNotFound, Audio, AudioCdg, Video, Archive, Image, Cdg, AudioArchive, VideoArchive, CdgArchive, AudioCdgArchive, UnsupportedArchive }

/// <summary>
/// Accepted extensions grouped by kind. Kept minimal and aligned with bundled codecs/plugins.
/// Also provides helpers to detect media kinds and classify archives.
/// </summary>
public static class FileTypes
{
    public static readonly HashSet<string> Audio = new(StringComparer.OrdinalIgnoreCase);
    public static readonly HashSet<string> Video = new(StringComparer.OrdinalIgnoreCase);
    public static readonly HashSet<string> Archive = new(StringComparer.OrdinalIgnoreCase) { ".7z", ".rar", ".zip", ".tar", ".gz", ".bz2", ".xz", ".tgz", ".tbz2" };
    public static readonly HashSet<string> Image = new(StringComparer.OrdinalIgnoreCase) { ".bmp", ".gif", ".jpg", ".jpeg", ".png", ".tif", ".tiff" }; //.webp
    public static readonly HashSet<string> Cdg = new(StringComparer.OrdinalIgnoreCase) { ".cdg" };

    public static bool IsAudio(string filename) => Audio.Contains(Path.GetExtension(filename));
    public static bool IsVideo(string filename) => Video.Contains(Path.GetExtension(filename));
    public static bool IsArchive(string filename) => Archive.Contains(Path.GetExtension(filename));
    public static bool IsImage(string filename) => Image.Contains(Path.GetExtension(filename));
    public static bool IsCdg(string filename) => Cdg.Contains(Path.GetExtension(filename));

    // Single-pass archive analysis to avoid repeated enumerations
    private sealed class ArchiveAnalysis
    {
        public bool HasAudio, HasVideo, HasCdg, HasAudioCdgPair;
        public HashSet<string> AudioBases { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> CdgBases { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private static ArchiveAnalysis AnalyzeArchive(IArchiveHandle archive)
    {
        var a = new ArchiveAnalysis();
        foreach (IArchiveEntryInfo entry in archive.Entries)
        {
            var key = entry.Key;
            if (string.IsNullOrEmpty(key)) continue;
            var ext = Path.GetExtension(key);
            if (string.IsNullOrEmpty(ext)) continue;

            if (!a.HasAudio && Audio.Contains(ext))
            {
                a.HasAudio = true;
                var b = Path.GetFileNameWithoutExtension(key);
                if (!string.IsNullOrWhiteSpace(b))
                {
                    if (!a.AudioBases.Add(b)) { /* already present */ }
                    if (a.CdgBases.Contains(b)) { a.HasAudioCdgPair = true; break; }
                }
            }
            else if (!a.HasCdg && Cdg.Contains(ext))
            {
                a.HasCdg = true;
                var b = Path.GetFileNameWithoutExtension(key);
                if (!string.IsNullOrWhiteSpace(b))
                {
                    if (!a.CdgBases.Add(b)) { /* already present */ }
                    if (a.AudioBases.Contains(b)) { a.HasAudioCdgPair = true; break; }
                }
            }

            if (!a.HasVideo && Video.Contains(ext)) a.HasVideo = true;
        }
        return a;
    }

}
