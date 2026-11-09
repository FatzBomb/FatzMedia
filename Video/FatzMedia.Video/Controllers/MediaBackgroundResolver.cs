using FatzMedia.Video.Core.Abstractions;
using FatzMedia.Video.Core.Files;

namespace FatzMedia.Video.Controllers;

internal static class MediaBackgroundResolver
{
    public static MediaBackgroundCandidate? Resolve(string sourcePath, IArchiveService archiveService, MediaContent content) => string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath)
            ? null
            : content.HasFlag(MediaContent.Video) || content.HasFlag(MediaContent.Image)
                ? null
                : FileTypes.IsArchive(sourcePath)
                    ? ResolveArchiveBackground(sourcePath, archiveService)
                    : ResolveSidecarBackground(sourcePath);

    private static MediaBackgroundCandidate? ResolveArchiveBackground(string sourcePath, IArchiveService archiveService)
    {
        using IArchiveHandle archive = archiveService.Open(sourcePath);
        IArchiveEntryInfo[] entries = [.. archive.Entries.Where(entry => !string.IsNullOrWhiteSpace(entry.Key))];
        List<MediaBackgroundMatch> matches = [];

        foreach (IArchiveEntryInfo entry in entries)
        {
            MediaBackgroundKind? kind = GetBackgroundKind(entry.Key);
            if (kind != null)
                matches.Add(MediaBackgroundMatch.FromArchiveEntry(kind.Value, sourcePath, entry.Key));
        }

        CollectSidecarBackgrounds(sourcePath, matches);

        MediaBackgroundMatch? selected = SelectRandom(matches);
        return selected?.ToCandidate();
    }

    private static MediaBackgroundCandidate? ResolveSidecarBackground(string sourcePath)
    {
        List<MediaBackgroundMatch> matches = [];
        CollectSidecarBackgrounds(sourcePath, matches);
        MediaBackgroundMatch? selected = SelectRandom(matches);
        return selected?.ToCandidate();
    }

    private static void CollectSidecarBackgrounds(string sourcePath, List<MediaBackgroundMatch> matches)
    {
        var directory = Path.GetDirectoryName(sourcePath);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return;

        var sourceBase = Path.GetFileNameWithoutExtension(sourcePath);
        CollectSidecarBackgrounds(directory, sourceBase, matches);
    }

    private static void CollectSidecarBackgrounds(string directory, string sourceBase, List<MediaBackgroundMatch> matches)
    {
        if (string.IsNullOrWhiteSpace(sourceBase))
        {
            return;
        }

        // Case-insensitive file systems (Windows, macOS default) already match extensions in any
        // case, so a few exact-name probes suffice. Case-sensitive ones (Linux) need a directory
        // listing to match "song.PNG"; that costs one pass over the folder per load.
        if (!IsCaseSensitiveFileSystem)
        {
            foreach (MediaBackgroundKind kind in PreferredKindOrder())
            {
                foreach (var extension in GetExtensions(kind))
                {
                    var candidatePath = Path.Combine(directory, sourceBase + extension);
                    if (File.Exists(candidatePath))
                        matches.Add(MediaBackgroundMatch.FromPath(kind, candidatePath));
                }
            }

            return;
        }

        List<string> candidates = EnumerateSidecarCandidates(directory, sourceBase);
        foreach (MediaBackgroundKind kind in PreferredKindOrder())
        {
            HashSet<string> extensions = GetExtensions(kind);
            foreach (var candidatePath in candidates)
            {
                if (extensions.Contains(Path.GetExtension(candidatePath)))
                    matches.Add(MediaBackgroundMatch.FromPath(kind, candidatePath));
            }
        }
    }

    private static bool IsCaseSensitiveFileSystem => !OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS();

    private static List<string> EnumerateSidecarCandidates(string directory, string sourceBase)
    {
        const StringComparison baseComparison = StringComparison.Ordinal;

        // '*' and '?' are legal in Linux file names but are wildcards in search patterns.
        var pattern = sourceBase.AsSpan().IndexOfAny('*', '?') >= 0 ? "*" : sourceBase + ".*";
        try
        {
            return [.. Directory.EnumerateFiles(directory, pattern, new EnumerationOptions
                {
                    MatchCasing = MatchCasing.PlatformDefault,
                    IgnoreInaccessible = true,
                    RecurseSubdirectories = false,
                })
                .Where(path => string.Equals(Path.GetFileNameWithoutExtension(path), sourceBase, baseComparison))
                .Order(StringComparer.Ordinal)];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static MediaBackgroundMatch? SelectRandom(IReadOnlyList<MediaBackgroundMatch> matches) =>
        matches.Count == 0 ? null : matches[Random.Shared.Next(matches.Count)];

    private static MediaBackgroundKind? GetBackgroundKind(string path) => FileTypes.IsVideo(path)
        ? MediaBackgroundKind.Video
        : FileTypes.IsImage(path)
            ? MediaBackgroundKind.Image
            : null;

    private static IEnumerable<MediaBackgroundKind> PreferredKindOrder()
    {
        yield return MediaBackgroundKind.Video;
        yield return MediaBackgroundKind.Image;
    }

    private static HashSet<string> GetExtensions(MediaBackgroundKind kind) =>
        kind == MediaBackgroundKind.Video ? FileTypes.Video : FileTypes.Image;

    private readonly record struct MediaBackgroundMatch(
        MediaBackgroundKind Kind,
        string? Path,
        string? ArchivePath,
        string? ArchiveEntryKey)
    {
        public static MediaBackgroundMatch FromPath(MediaBackgroundKind kind, string path) =>
            new(kind, path, ArchivePath: null, ArchiveEntryKey: null);

        public static MediaBackgroundMatch FromArchiveEntry(
            MediaBackgroundKind kind,
            string archivePath,
            string entryKey) => new(kind, Path: null, archivePath, entryKey);

        public MediaBackgroundCandidate ToCandidate() => ArchivePath != null && ArchiveEntryKey != null
            ? MediaBackgroundCandidate.FromArchiveEntryReference(Kind, ArchivePath, ArchiveEntryKey)
            : MediaBackgroundCandidate.FromPath(Kind, Path!);
    }
}
