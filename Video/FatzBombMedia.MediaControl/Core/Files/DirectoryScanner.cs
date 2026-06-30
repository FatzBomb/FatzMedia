namespace FatzBombMedia.MediaControl.Core.Files;

/// <summary>
/// Defines compatibility levels for binary file analysis during directory scanning operations.
/// </summary>
public enum CompatibilityLevel
{
    /// <summary>
    /// No compatibility checking is performed. All files are considered valid.
    /// </summary>
    All,

    /// <summary>
    /// Files that are compatible with the current process architecture are considered valid (e.g. AnyCPU on x64).
    /// </summary>
    Compatible
}

/// <summary>
/// Provides functionality for scanning directories and locating files with optional binary compatibility checking.
/// Supports breadth-first directory traversal and various file search patterns.
/// </summary>
public static class DirectoryScanner
{
    private static readonly EnumerationOptions EnumerateFilesRecursive = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.System | FileAttributes.Temporary | FileAttributes.ReparsePoint,
        MatchCasing = MatchCasing.CaseInsensitive
    };

    private static readonly EnumerationOptions EnumerateFilesNoRecurse = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.System | FileAttributes.Temporary,
        MatchCasing = MatchCasing.CaseInsensitive
    };

    private static readonly EnumerationOptions EnumerateDirsNoRecurse = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = true,
        MatchCasing = MatchCasing.CaseInsensitive
    };


    /// <summary>
    /// Determines the common file extension from a collection of file names.
    /// If all files share the same extension, returns a pattern like "*.extension".
    /// If files have different extensions or no extensions, returns "*".
    /// </summary>
    /// <param name="files">Collection of file names to analyze.</param>
    /// <returns>A pattern string representing the common extension, or "*" if no common extension exists.</returns>
    private static string CommonExtension(IEnumerable<string> files)
    {
        string? ext = null;
        foreach (var file in files)
        {
            if (string.IsNullOrWhiteSpace(file) || !Path.HasExtension(file))
                return "*";

            var e = Path.GetExtension(file);
            if (ext is null)
                ext = e;
            else if (!string.Equals(ext, e, StringComparison.OrdinalIgnoreCase))
                return "*";
        }
        return ext is null ? "*" : $"*{ext}";
    }

    /// <summary>
    /// Finds the first directory (root or any subdirectory) that contains all required files (by file name).
    /// Uses breadth-first search to traverse the directory tree and checks for binary compatibility when specified.
    /// </summary>
    /// <param name="rootPaths">The root directory to search in. Must exist and not be null or whitespace.</param>
    /// <param name="fileNames">Collection of file names to search for. All files must be found in the same directory.</param>
    /// <param name="compatibility">The compatibility level to check for binary files.
    /// - All: No compatibility checking
    /// - Compatible: Files that are compatible with current process</param>
    /// <returns>The full path to the directory containing all specified files, or null if not found or invalid input.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="fileNames"/> is null.</exception>
    public static string? LocateFilesDirectory(IEnumerable<string> rootPaths, IEnumerable<string> fileNames, CompatibilityLevel compatibility = CompatibilityLevel.All)
    {
        if (!rootPaths.Any() || !fileNames.Any())
            return null;

        var pattern = CommonExtension(fileNames);
        var targets = new HashSet<string>(fileNames.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(), StringComparer.OrdinalIgnoreCase);

        if (targets.Count == 0 || !rootPaths.Any())
            return null;

        foreach (var searchPath in rootPaths)
        {
            var dirQueue = new Queue<string>();
            dirQueue.Enqueue(searchPath);

            try
            {
                foreach (var d in Directory.EnumerateDirectories(searchPath, "*", EnumerateDirsNoRecurse))
                    dirQueue.Enqueue(d);
            }
            catch { }

            while (dirQueue.Count > 0)
            {
                var dir = dirQueue.Dequeue();
                if (dir != searchPath)
                    try
                    {
                        foreach (var child in Directory.EnumerateDirectories(dir, "*", EnumerateDirsNoRecurse))
                            dirQueue.Enqueue(child);
                    }
                    catch { }

                var matched = 0;
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                try
                {
                    foreach (var full in Directory.EnumerateFiles(dir, pattern, EnumerateFilesNoRecurse))
                    {
                        var name = Path.GetFileName(full);
                        if (!targets.Contains(name)) continue;
                        if (!MatchesCompatibility(full, compatibility)) continue;
                        if (!seen.Add(name)) continue;

                        matched++;
                        if (matched == targets.Count)
                            return dir;
                    }
                }
                catch { }
            }
        }
        return null;
    }

    /// <summary>
    /// Locates multiple files and returns a dictionary mapping file names to their full paths.
    /// Searches in the specified directories and optionally their subdirectories.
    /// </summary>
    /// <param name="rootPaths">Collection of directory paths to search in.</param>
    /// <param name="fileNames">Collection of file names to search for.</param>
    /// <param name="subDirectories">Whether to search subdirectories recursively. Default is true.</param>
    /// <param name="compatibility">The compatibility level to check for binary files.
    /// - All: No compatibility checking
    /// - Compatible: Files that are compatible with current process</param>
    /// <returns>A dictionary mapping found file names to their full paths.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="rootPaths"/> or <paramref name="fileNames"/> is null.</exception>
    public static Dictionary<string, string> LocateFiles(IEnumerable<string> rootPaths, IEnumerable<string> fileNames, bool subDirectories = true, CompatibilityLevel compatibility = CompatibilityLevel.All)
    {
        if (!rootPaths.Any() || !fileNames.Any()) return new(StringComparer.OrdinalIgnoreCase);

        var pattern = CommonExtension(fileNames);
        HashSet<string> targets = CleanList(fileNames);

        if (targets.Count == 0) return new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string> found = new(StringComparer.OrdinalIgnoreCase);

        EnumerationOptions opts = subDirectories ? EnumerateFilesRecursive : EnumerateFilesNoRecurse;

        foreach (var dir in rootPaths)
        {
            if (targets.Count == 0)
                break;

            foreach (var full in Directory.EnumerateFiles(dir, pattern, opts))
            {
                var name = Path.GetFileName(full);
                if (targets.Contains(name) && !found.ContainsKey(name))
                {
                    if (!MatchesCompatibility(full, compatibility))
                        continue;

                    found[name] = full;
                    _ = targets.Remove(name);
                    if (targets.Count == 0)
                        break;
                }
            }
        }

        return found;
    }

    public static HashSet<string> CleanList(IEnumerable<string> fileNames) => new(fileNames.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(), StringComparer.OrdinalIgnoreCase);

    public static bool MatchesCompatibility(string file, CompatibilityLevel compatibility)
    {
        if (compatibility == CompatibilityLevel.All) return true;

        CompatibilityInfo compat = BinaryCompatibility.Analyze(file);
        return compat.IsCompatible;
    }
}
