namespace FatzMedia.Video.Sample.Player.Shared.Hosting;

/// <summary>Builds the native library search list the test players pass to the engine.</summary>
public static class PlayerNativePaths
{
    /// <summary>
    /// Gets <paramref name="requested"/> (<c>--native=</c>) then <paramref name="defaults"/> (the
    /// host's own folders), as full paths, without duplicates, keeping only directories that exist:
    /// the engine throws for a missing one.
    /// </summary>
    public static IReadOnlyList<string> Combine(IEnumerable<string> requested, IEnumerable<string> defaults) =>
        [.. requested.Concat(defaults)
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Select(static path => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)))
            .Where(Directory.Exists)
            .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)];
}
