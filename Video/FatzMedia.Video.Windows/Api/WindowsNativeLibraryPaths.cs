namespace FatzMedia.Video;

/// <summary>Conventional host-owned native-library locations for the Windows x64 package.</summary>
public static class WindowsNativeLibraryPaths
{
    public const string AppNativeRelativePath = "native";
    public const string WinX64RuntimeRelativePath = "runtimes/win-x64/native";

    public static IReadOnlyList<string> DefaultRelativePaths { get; } =
    [
        AppNativeRelativePath,
        WinX64RuntimeRelativePath
    ];

    /// <summary>
    /// Returns existing conventional directories beneath <see cref="AppContext.BaseDirectory"/>.
    /// The host may pass this list to <c>Player</c> or provide its own directories instead.
    /// </summary>
    public static IReadOnlyList<string> GetDefaultSearchPaths() =>
        [.. DefaultRelativePaths
            .Select(path => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, path)))
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)];
}
