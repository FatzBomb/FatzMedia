using System.IO;

namespace FatzBomb.MediaControl.Player.Configuration;

public static class PlayerNativeLibraries
{
    private static readonly string[] NativeFolders =
    [
        "native"
    ];

    public static IReadOnlyList<string> ResolveSearchPaths() => [.. NativeFolders.Select(ResolveFromAppBase)];

    private static string ResolveFromAppBase(string path)
    {
        var resolvedPath = Path.IsPathRooted(path)
            ? path
            : Path.Combine(AppContext.BaseDirectory, path);

        var fullPath = Path.GetFullPath(resolvedPath);
        _ = Directory.CreateDirectory(fullPath);
        return fullPath;
    }
}
