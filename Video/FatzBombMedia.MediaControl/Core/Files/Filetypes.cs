using FatzBombMedia.MediaControl.Core.Abstractions;
using System.IO;

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
        foreach (var entry in archive.Entries)
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

    public static FileType GetFileType(string path, IArchiveService svc, bool searchArchive = true, bool searchCompanionFile = true)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return FileType.FileNotFound;

        var ext = Path.GetExtension(path);
        if (Audio.Contains(ext)) return File.Exists(System.IO.Path.ChangeExtension(path, ".cdg")) ? FileType.AudioCdg : FileType.Audio;
        if (Video.Contains(ext)) return FileType.Video;
        if (Image.Contains(ext)) return FileType.Image;

        if (Cdg.Contains(ext))
        {
            if (!searchCompanionFile) return FileType.Cdg;
            foreach (string audioExt in Audio) // check same name with audio extension
            {
                if (System.IO.File.Exists(System.IO.Path.ChangeExtension(path, audioExt)))
                    return FileType.AudioCdg;
            }
            return FileType.Cdg;
        }

        if (Archive.Contains(ext))
        {
            if (searchArchive)
            {
                using var h = svc.Open(path);
                var a = AnalyzeArchive(h);
                if (a.HasAudioCdgPair) return FileType.AudioCdgArchive;
                if (a.HasVideo) return FileType.VideoArchive;
                if (a.HasAudio) return FileType.AudioArchive;
                if (a.HasCdg) return FileType.CdgArchive;
                return FileType.UnsupportedArchive;
            }
            return FileType.Archive;
        }
        return FileType.Unsupported;
    }

}
