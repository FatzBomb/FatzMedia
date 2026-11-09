using FatzMedia.Video.Controllers;
#if !WINDOWS
using FatzMedia.Video.Linux;
#endif

namespace FatzMedia.Video.Sample.Player.Avalonia;

/// <summary>
/// The one place that picks the OS platform package. The Windows build (net10.0-windows7.0)
/// uses <c>FatzMedia.Video.Windows</c>; the portable build (net10.0) uses
/// <c>FatzMedia.Video.Linux</c>. Both pair with the frame-sink renderers chosen separately.
/// </summary>
static class PlatformComposition
{
#if WINDOWS
    public static string Name => "Windows";

    public static IPlatformFactory Create(IMediaPresentationCoordinatorFactory renderer) =>
        new WindowsPlatformFactory(renderer);
#else
    public static string Name => "Linux";

    public static IPlatformFactory Create(IMediaPresentationCoordinatorFactory renderer) =>
        OperatingSystem.IsLinux()
            ? new LinuxPlatformFactory(renderer, new LinuxPlatformOptions { DisplayRefreshRateHz = 60 })
            : throw new PlatformNotSupportedException(
                "The net10.0 build of this player supports Linux only; on Windows run the net10.0-windows7.0 build (dotnet run -f net10.0-windows7.0).");
#endif
}
