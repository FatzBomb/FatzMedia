using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace FatzMedia.Audio.Tests;

/// <summary>
/// Engines made without a cache directory use the OS's per-user cache folder. The tests must not touch the real one:
/// point it into a temp folder before the first engine exists (Windows reads LOCALAPPDATA, Linux
/// XDG_CACHE_HOME; .NET's SetEnvironmentVariable does not reach the native getenv off Windows, so call setenv).
/// </summary>
internal static partial class TestCacheHome
{
    [LibraryImport("libc", EntryPoint = "setenv", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int SetEnv(string name, string value, int overwrite);

    [ModuleInitializer]
    internal static void Init()
    {
        var home = Path.Combine(Path.GetTempPath(), "fatzaudio-tests-os-cache");
        if (OperatingSystem.IsWindows())
            Environment.SetEnvironmentVariable("LOCALAPPDATA", home);
        else if (OperatingSystem.IsLinux())
            SetEnv("XDG_CACHE_HOME", home, 1);
    }
}
