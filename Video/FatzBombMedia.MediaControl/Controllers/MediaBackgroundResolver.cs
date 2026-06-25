using FatzBombMedia.MediaControl.Core.Abstractions;
using FatzBombMedia.MediaControl.Core.Files;
using System.IO;

namespace FatzBombMedia.MediaControl.Controllers;

internal static class MediaBackgroundResolver
{
    public static MediaBackgroundCandidate? Resolve(string sourcePath, IArchiveService archiveService, MediaContent content)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
        {
            return null;
        }

        if (content.HasFlag(MediaContent.Video) || content.HasFlag(MediaContent.Image))
        {
            return null;
        }

        if (!content.HasFlag(MediaContent.Audio) && !content.HasFlag(MediaContent.Cdg))
        {
            return null;
        }

        return FileTypes.IsArchive(sourcePath)
            ? ResolveArchiveBackground(sourcePath, archiveService)
            : ResolveSidecarBackground(sourcePath);
    }

    private static MediaBackgroundCandidate? ResolveArchiveBackground(string sourcePath, IArchiveService archiveService)
    {
        using var archive = archiveService.Open(sourcePath);
        var entries = archive.Entries
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Key))
            .ToArray();

        var mediaBases = entries
            .Where(entry => FileTypes.IsAudio(entry.Key) || FileTypes.IsCdg(entry.Key))
            .Select(entry => Path.GetFileNameWithoutExtension(entry.Key))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var backgroundEntries = entries
            .Select(entry => new
            {
                Entry = entry,
                Kind = GetBackgroundKind(entry.Key),
                BaseName = Path.GetFileNameWithoutExtension(entry.Key)
            })
            .Where(item => item.Kind != null)
            .ToArray();

        var selected = backgroundEntries
            .Where(item => !string.IsNullOrWhiteSpace(item.BaseName) && mediaBases.Contains(item.BaseName))
            .OrderBy(item => GetKindPriority(item.Kind!.Value))
            .ThenBy(item => item.Entry.Key, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault()
            ?? backgroundEntries
                .OrderBy(item => GetKindPriority(item.Kind!.Value))
                .ThenBy(item => item.Entry.Key, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();

        if (selected != null)
        {
            return MediaBackgroundCandidate.FromArchiveEntry(selected.Kind!.Value, selected.Entry);
        }

        return ResolveSidecarBackground(sourcePath);
    }

    private static MediaBackgroundCandidate? ResolveSidecarBackground(string sourcePath)
    {
        var directory = Path.GetDirectoryName(sourcePath);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return null;
        }

        var sourceBase = Path.GetFileNameWithoutExtension(sourcePath);
        foreach (var kind in PreferredKindOrder())
        {
            foreach (var extension in GetExtensions(kind))
            {
                var candidatePath = Path.Combine(directory, sourceBase + extension);
                if (File.Exists(candidatePath))
                {
                    return MediaBackgroundCandidate.FromPath(kind, candidatePath);
                }
            }
        }

        return null;
    }

    private static MediaBackgroundKind? GetBackgroundKind(string path)
    {
        if (FileTypes.IsVideo(path))
        {
            return MediaBackgroundKind.Video;
        }

        if (FileTypes.IsImage(path))
        {
            return MediaBackgroundKind.Image;
        }

        return null;
    }

    private static IEnumerable<MediaBackgroundKind> PreferredKindOrder()
    {
        yield return MediaBackgroundKind.Video;
        yield return MediaBackgroundKind.Image;
    }

    private static IEnumerable<string> GetExtensions(MediaBackgroundKind kind)
    {
        return kind == MediaBackgroundKind.Video ? FileTypes.Video : FileTypes.Image;
    }

    private static int GetKindPriority(MediaBackgroundKind kind)
    {
        return kind == MediaBackgroundKind.Video ? 0 : 1;
    }
}
