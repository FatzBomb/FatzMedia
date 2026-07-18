using System.IO;

namespace FatzBomb.MediaControl.Player.Configuration;

public static class PlayerNativeLibraries
{
    public static IReadOnlyList<string> ResolveSearchPaths()
    {
        IReadOnlyList<string> defaultPaths = WindowsNativeLibraryPaths.GetDefaultSearchPaths();
        return defaultPaths.Count > 0
            ? defaultPaths
            : [ResolveFromAppBase(WindowsNativeLibraryPaths.AppNativeRelativePath)];
    }

    private static string ResolveFromAppBase(string path)
    {
        var resolvedPath = Path.IsPathRooted(path)
            ? path
            : Path.Combine(AppContext.BaseDirectory, path);

        var fullPath = Path.GetFullPath(resolvedPath);
        // The validation host keeps a stable drop folder even before local native assets are supplied.
        _ = Directory.CreateDirectory(fullPath);
        return fullPath;
    }
}
