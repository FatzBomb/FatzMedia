using FatzBomb.MediaControl.Controllers;
#if !WINDOWS
using FatzBomb.MediaControl.Linux;
#endif

namespace FatzBomb.MediaControl.Player.Avalonia;

/// <summary>
/// The one place that picks the OS platform package. The Windows build (net10.0-windows7.0)
/// uses <c>FatzBomb.MediaControl.Windows</c>; the portable build (net10.0) uses
/// <c>FatzBomb.MediaControl.Linux</c>. Both pair with the frame-sink renderers chosen separately.
/// </summary>
internal static class PlatformComposition
{
#if WINDOWS
    public static string Name => "Windows";

    public static IMediaControlPlatformFactory Create(IMediaPresentationCoordinatorFactory renderer) =>
        new WindowsMediaControlPlatformFactory(renderer);
#else
    public static string Name => "Linux";

    public static IMediaControlPlatformFactory Create(IMediaPresentationCoordinatorFactory renderer) =>
        OperatingSystem.IsLinux()
            ? new LinuxMediaControlPlatformFactory(renderer, new LinuxPlatformOptions { DisplayRefreshRateHz = 60 })
            : throw new PlatformNotSupportedException(
                "The net10.0 build of this player supports Linux only; on Windows run the net10.0-windows7.0 build (dotnet run -f net10.0-windows7.0).");
#endif
}
