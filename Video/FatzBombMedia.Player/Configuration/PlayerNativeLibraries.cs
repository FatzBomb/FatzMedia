using System.IO;

namespace FatzBombMedia.Player.Configuration;

public static class PlayerNativeLibraries
{
    private static readonly string[] NativeFolders =
    [
        "native"
    ];

    public static IReadOnlyList<string> ResolveSearchPaths()
    {
        return [.. NativeFolders.Select(ResolveFromAppBase)];
    }

    private static string ResolveFromAppBase(string path)
    {
        var resolvedPath = Path.IsPathRooted(path)
            ? path
            : Path.Combine(AppContext.BaseDirectory, path);

        var fullPath = Path.GetFullPath(resolvedPath);
        Directory.CreateDirectory(fullPath);
        return fullPath;
    }
}
