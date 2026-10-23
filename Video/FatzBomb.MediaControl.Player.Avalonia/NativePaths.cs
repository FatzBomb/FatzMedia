using System.Runtime.InteropServices;

namespace FatzBomb.MediaControl.Player.Avalonia;

/// <summary>
/// Where the player looks for the host-owned BASS and FFmpeg libraries, highest priority first:
/// <c>--native=</c> directories, the <c>FATZBOMB_LINUX_*</c> variables the Linux tests use,
/// then <c>native/</c> and <c>runtimes/&lt;rid&gt;/native</c> next to the app. Only existing
/// directories are returned (the engine throws for a missing one).
/// </summary>
internal static class NativePaths
{
    public static IReadOnlyList<string> Resolve(StartupOptions options)
    {
        List<string> candidates = [.. options.NativeDirectories];
        foreach (var variable in new[] { "FATZBOMB_LINUX_FFMPEG_DIR", "FATZBOMB_LINUX_BASS_DIR" })
        {
            if (Environment.GetEnvironmentVariable(variable) is { Length: > 0 } fromEnvironment)
                candidates.Add(fromEnvironment);
        }

        var appNative = Path.Combine(AppContext.BaseDirectory, "native");
        try
        {
            // A stable drop folder even before any binaries are supplied, like the WPF player.
            _ = Directory.CreateDirectory(appNative);
        }
        catch (Exception)
        {
            // Read-only install location: fall through to whatever already exists.
        }

        candidates.Add(appNative);
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "runtimes", RuntimeInformation.RuntimeIdentifier, "native"));

        return [.. candidates
            .Select(static path => Path.GetFullPath(path))
            .Where(Directory.Exists)
            .Distinct(StringComparer.Ordinal)];
    }
}
