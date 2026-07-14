#pragma warning disable IDE0130
namespace FatzBomb.MediaControl;

/// <summary>
/// Host-owned native DLL path settings used when a player is created without constructor paths.
/// </summary>
public static class DLLPath
{
    /// <summary>
    /// Gets or sets the folder containing BASS native DLLs. Leave empty to use the default host native folder.
    /// </summary>
    public static string? Bass { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the folder containing FFmpeg native DLLs. Leave empty to use the default host native folder.
    /// </summary>
    public static string? FFmpeg { get; set; } = string.Empty;

    internal static IReadOnlyList<string> GetConfiguredNativeLibraryPaths() => [.. new[] { Bass, FFmpeg }
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)];
}
