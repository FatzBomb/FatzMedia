namespace FatzMedia.Video.Linux.Runtime;

/// <summary>
/// Finds host-supplied shared libraries. Hosts own the binaries (source, licence, version,
/// deployment); this locator only searches the host paths and the application's own folders.
/// System library directories are not searched implicitly.
/// </summary>
internal static class LinuxNativeLibraryLocator
{
    /// <summary>
    /// Application-relative default folders, searched after host paths: the app folder,
    /// <c>native/</c>, and <c>runtimes/linux-&lt;arch&gt;/native/</c>.
    /// </summary>
    public static IReadOnlyList<string> GetDefaultSearchPaths(string baseDirectory, string runtimeIdentifier) =>
    [
        baseDirectory,
        Path.Combine(baseDirectory, "native"),
        Path.Combine(baseDirectory, "runtimes", runtimeIdentifier, "native"),
    ];

    /// <summary>Gets the portable runtime identifier for the current process, e.g. <c>linux-x64</c>.</summary>
    public static string CurrentRuntimeIdentifier =>
        "linux-" + System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();

    /// <summary>
    /// Resolves host paths (absolute, or relative to <paramref name="baseDirectory"/>) followed by
    /// the defaults, keeping existing directories only and removing duplicates in order.
    /// </summary>
    public static IReadOnlyList<string> BuildSearchPaths(
        IEnumerable<string> hostPaths,
        string baseDirectory,
        string runtimeIdentifier,
        ICollection<string>? rejectedPaths = null)
    {
        var result = new List<string>();
        foreach (var hostPath in hostPaths)
        {
            if (string.IsNullOrWhiteSpace(hostPath))
                continue;

            var candidate = Path.GetFullPath(Path.IsPathRooted(hostPath) ? hostPath : Path.Combine(baseDirectory, hostPath.Trim()));
            if (Directory.Exists(candidate))
                AddDistinct(result, candidate);
            else
                rejectedPaths?.Add(candidate);
        }

        foreach (var defaultPath in GetDefaultSearchPaths(baseDirectory, runtimeIdentifier))
        {
            var candidate = Path.GetFullPath(defaultPath);
            if (Directory.Exists(candidate))
                AddDistinct(result, candidate);
        }

        return result;
    }

    /// <summary>
    /// Finds each file name in the first search path that contains it (Linux names are case
    /// sensitive, so matching is exact).
    /// </summary>
    public static Dictionary<string, string> Locate(IEnumerable<string> searchPaths, IEnumerable<string> fileNames)
    {
        var found = new Dictionary<string, string>(StringComparer.Ordinal);
        var paths = searchPaths.ToArray();
        foreach (var fileName in fileNames)
        {
            foreach (var path in paths)
            {
                var candidate = Path.Combine(path, fileName);
                if (File.Exists(candidate))
                {
                    found[fileName] = candidate;
                    break;
                }
            }
        }

        return found;
    }

    private static void AddDistinct(List<string> paths, string path)
    {
        if (!paths.Contains(path, StringComparer.Ordinal))
            paths.Add(path);
    }
}
