namespace FatzBomb.MediaControl.Core.Files;

/// <summary>
/// Shared helpers for transient media scratch files written from in-memory byte
/// buffers (e.g. archive-extracted or streamed audio/video). Consolidates temp-file
/// creation and best-effort deletion previously duplicated across the video and
/// audio pipelines.
/// </summary>
internal static class TempMediaFile
{
    private const string TempSubfolderName = "FatzBombMediaControl";
    /// <summary>The extension of media scratch files.</summary>
    public const string MediaExtension = ".media";

    /// <summary>
    /// Writes <paramref name="data"/> to a uniquely named scratch file under the
    /// shared media temp subfolder and returns its full path. The subfolder is
    /// created if it does not already exist.
    /// </summary>
    public static string Create(byte[] data)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), TempSubfolderName);
        _ = Directory.CreateDirectory(tempDir);
        var tempPath = Path.Combine(tempDir, Guid.NewGuid().ToString("N") + MediaExtension);
        File.WriteAllBytes(tempPath, data);
        return tempPath;
    }

    /// <summary>
    /// Returns a new unique scratch-file path under the shared media temp subfolder, creating the
    /// subfolder if needed. The file itself is not created.
    /// </summary>
    public static string NewPath(string extension)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), TempSubfolderName);
        _ = Directory.CreateDirectory(tempDir);
        return Path.Combine(tempDir, Guid.NewGuid().ToString("N") + extension);
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

            DateTime cutoff = DateTime.UtcNow - age;
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
