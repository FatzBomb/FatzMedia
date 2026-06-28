using System.IO;
using FatzBombMedia.MediaControl.Core.Abstractions;

namespace FatzBombMedia.MediaControl.Core.Files;

public sealed class FileTree : IDisposable
{
    public FileType Type { get; internal set; } = FileType.Unsupported;
    public string? SourcePath { get; internal set; }

    // Direct file paths (non-archive scenarios)
    public string? AudioPath { get; internal set; }
    public string? CdgPath { get; internal set; }
    public string? VideoPath { get; internal set; }
    public string? ImagePath { get; internal set; }

    // Archive context and first-matched entries
    internal IArchiveHandle? ArchiveHandle { get; set; }
    internal IArchiveEntryInfo? AudioEntry { get; set; }
    internal IArchiveEntryInfo? CdgEntry { get; set; }
    internal IArchiveEntryInfo? VideoEntry { get; set; }
    internal IArchiveEntryInfo? ImageEntry { get; set; }

    public bool IsArchive => ArchiveHandle != null;

    public void Dispose()
    {
        try { ArchiveHandle?.Dispose(); } catch { }
        ArchiveHandle = null;
    }
}

public static class FileTreeBuilder
{
    public static FileTree GetFileTree(string path, IArchiveService archiveService, bool searchArchive = true, bool searchCompanionFile = true)
    {
        var tree = new FileTree { SourcePath = path };
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            tree.Type = FileType.FileNotFound;
            return tree;
        }

        var ext = Path.GetExtension(path);
        if (FileTypes.Archive.Contains(ext))
        {
            if (!searchArchive)
            {
                tree.Type = FileType.Archive;
                return tree;
            }

            IArchiveHandle handle = archiveService.Open(path);
            tree.ArchiveHandle = handle;

            var firstAudio = (IArchiveEntryInfo?)null;
            var firstCdg = (IArchiveEntryInfo?)null;
            var firstVideo = (IArchiveEntryInfo?)null;
            var firstImage = (IArchiveEntryInfo?)null;

            var audioByBase = new Dictionary<string, IArchiveEntryInfo>(StringComparer.OrdinalIgnoreCase);
            var cdgByBase = new Dictionary<string, IArchiveEntryInfo>(StringComparer.OrdinalIgnoreCase);

            foreach (IArchiveEntryInfo e in handle.Entries)
            {
                var key = e.Key;
                if (string.IsNullOrEmpty(key)) continue;
                var eExt = Path.GetExtension(key);
                if (string.IsNullOrEmpty(eExt)) continue;

                if (firstVideo == null && FileTypes.Video.Contains(eExt)) firstVideo = e;
                if (firstImage == null && FileTypes.Image.Contains(eExt)) firstImage = e;

                if (FileTypes.Audio.Contains(eExt))
                {
                    firstAudio ??= e;
                    var b = Path.GetFileNameWithoutExtension(key);
                    if (!string.IsNullOrWhiteSpace(b) && !audioByBase.ContainsKey(b)) audioByBase[b] = e;
                }
                else if (FileTypes.Cdg.Contains(eExt))
                {
                    firstCdg ??= e;
                    var b = Path.GetFileNameWithoutExtension(key);
                    if (!string.IsNullOrWhiteSpace(b) && !cdgByBase.ContainsKey(b)) cdgByBase[b] = e;
                }
            }

            IArchiveEntryInfo? pairedAudio = null, pairedCdg = null;
            foreach ((var b, IArchiveEntryInfo? cdg) in cdgByBase)
            {
                if (audioByBase.TryGetValue(b, out IArchiveEntryInfo? aud)) { pairedAudio = aud; pairedCdg = cdg; break; }
            }

            if (pairedAudio != null && pairedCdg != null)
            {
                tree.AudioEntry = pairedAudio;
                tree.CdgEntry = pairedCdg;
                tree.Type = FileType.AudioCdgArchive;
                return tree;
            }

            if (firstVideo != null)
            {
                tree.VideoEntry = firstVideo;
                tree.Type = FileType.VideoArchive;
                return tree;
            }
            if (firstAudio != null)
            {
                tree.AudioEntry = firstAudio;
                tree.Type = FileType.AudioArchive;
                return tree;
            }
            if (firstCdg != null)
            {
                tree.CdgEntry = firstCdg;
                tree.Type = FileType.CdgArchive;
                return tree;
            }

            tree.Type = FileType.UnsupportedArchive;
            return tree;
        }

        // Non-archive file
        if (FileTypes.Video.Contains(ext))
        {
            tree.VideoPath = path;
            tree.Type = FileType.Video;
            return tree;
        }
        if (FileTypes.Image.Contains(ext))
        {
            tree.ImagePath = path;
            tree.Type = FileType.Image;
            return tree;
        }
        if (FileTypes.Audio.Contains(ext))
        {
            tree.AudioPath = path;
            if (searchCompanionFile)
            {
                var cdgPath = Path.ChangeExtension(path, ".cdg");
                if (File.Exists(cdgPath))
                {
                    tree.CdgPath = cdgPath;
                    tree.Type = FileType.AudioCdg;
                    return tree;
                }
            }
            tree.Type = FileType.Audio;
            return tree;
        }
        if (FileTypes.Cdg.Contains(ext))
        {
            tree.CdgPath = path;
            if (searchCompanionFile)
            {
                // find first existing audio companion by trying known audio extensions
                foreach (var aExt in FileTypes.Audio)
                {
                    var audioPath = Path.ChangeExtension(path, aExt);
                    if (File.Exists(audioPath))
                    {
                        tree.AudioPath = audioPath;
                        tree.Type = FileType.AudioCdg;
                        return tree;
                    }
                }
            }
            tree.Type = FileType.Cdg;
            return tree;
        }

        tree.Type = FileType.Unsupported;
        return tree;
    }
}
