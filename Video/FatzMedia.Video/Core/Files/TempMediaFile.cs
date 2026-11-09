using System.Collections.Concurrent;
using FatzMedia.Video.Core.Abstractions;

namespace FatzMedia.Video.Core.Files;

/// <summary>
/// Shared helpers for transient media scratch files (archive-extracted media and converted
/// audio): unique paths, stale-file sweeping and best-effort deletion.
/// </summary>
static class TempMediaFile
{
    const string TempSubfolderName = "FatzMediaVideo";

    /// <summary>The extension of media scratch files.</summary>
    public const string MediaExtension = ".media";

    static readonly TimeSpan StaleAge = TimeSpan.FromDays(1);
    static readonly ConcurrentDictionary<string, byte> SweptExtensions = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Returns a new unique scratch-file path under the shared media temp subfolder, creating the
    /// subfolder if needed. The file itself is not created. The first call for an extension also
    /// deletes files of that extension a previous run left behind (a crash or a killed process);
    /// only files older than a day are touched, so another running instance's files are kept.
    /// </summary>
    public static string NewPath(string extension)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), TempSubfolderName);
        _ = Directory.CreateDirectory(tempDir);
        if (SweptExtensions.TryAdd(extension, 0))
            DeleteOlderThan(extension, StaleAge);

        return Path.Combine(tempDir, Guid.NewGuid().ToString("N") + extension);
    }

    /// <summary>
    /// Copies an archive entry to a new scratch file with <see cref="MediaExtension"/> and returns
    /// its path; the partial file is deleted if the copy fails.
    /// </summary>
    public static string CreateFromArchiveEntry(IArchiveEntryInfo archiveEntry)
    {
        var tempPath = NewPath(MediaExtension);
        try
        {
            using var output = File.Create(tempPath);
            archiveEntry.WriteTo(output);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }

        return tempPath;
    }

    /// <summary>
    /// Best-effort deletion of scratch files with <paramref name="extension"/> last written more
    /// than <paramref name="age"/> ago.
    /// </summary>
    public static void DeleteOlderThan(string extension, TimeSpan age)
    {
        try
        {
            var tempDir = Path.Combine(Path.GetTempPath(), TempSubfolderName);
            if (!Directory.Exists(tempDir))
                return;

            var cutoff = DateTime.UtcNow - age;
            foreach (var path in Directory.EnumerateFiles(tempDir, "*" + extension))
            {
                if (File.GetLastWriteTimeUtc(path) < cutoff)
                    TryDelete(path);
            }
        }
        catch
        {
            // Best-effort cleanup of transient media scratch files.
        }
    }

    /// <summary>
    /// Deletes <paramref name="path"/> if it exists. Best-effort: IO failures are
    /// swallowed, since scratch-file cleanup must never surface as an operation error.
    /// </summary>
    public static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Best-effort cleanup of transient media scratch files.
        }
    }
}
