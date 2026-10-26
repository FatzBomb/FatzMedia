using Avalonia;
using FatzBomb.MediaControl.Player.Shared;
using FatzBomb.MediaControl.Player.Shared.Hosting;

namespace FatzBomb.MediaControl.Player.Avalonia;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        StartupOptions.Current = PlayerStartupOptions.Parse(args);
        using PlayerDebugLog debugLog = PlayerDebugLog.Attach(App.LogFilePath, "Player.Avalonia");
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
