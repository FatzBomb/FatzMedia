#pragma warning disable IDE0005 // needed in the WPF build; the linked WinForms copy has System.IO implicitly
using System.IO;
#pragma warning restore IDE0005

namespace FatzMedia.Video.Sample.Player.Configuration;

/// <summary>
/// The native-library search paths of the Windows test players (WPF, and WinForms through a
/// linked copy of this file).
/// </summary>
public static class PlayerNativeLibraries
{
    public static IReadOnlyList<string> ResolveSearchPaths()
    {
        var defaultPaths = WindowsNativeLibraryPaths.GetDefaultSearchPaths();
        return defaultPaths.Count > 0
            ? defaultPaths
            : [ResolveFromAppBase(WindowsNativeLibraryPaths.AppNativeRelativePath)];
    }

    static string ResolveFromAppBase(string path)
    {
        var resolvedPath = Path.IsPathRooted(path)
            ? path
            : Path.Combine(AppContext.BaseDirectory, path);

        var fullPath = Path.GetFullPath(resolvedPath);
        try
        {
            // The validation host keeps a stable drop folder even before local native assets are supplied.
            _ = Directory.CreateDirectory(fullPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Read-only install location (ENG-039 W3): the folder is still searched if it appears later.
        }

        return fullPath;
    }
}
