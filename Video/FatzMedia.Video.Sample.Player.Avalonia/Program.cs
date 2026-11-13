using Avalonia;
using FatzMedia.Video.Sample.Player.Shared;
using FatzMedia.Video.Sample.Player.Shared.Hosting;
using FatzMedia.Video.Sample.Player.Shared.Presentation;

namespace FatzMedia.Video.Sample.Player.Avalonia;

static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        StartupOptions.Current = PlayerStartupOptions.Parse(args);
        StartupOptions.AbSwapDemo = AbSwapDemoOptions.Parse(args);
        using var debugLog = PlayerDebugLog.Attach(App.LogFilePath, "Player.Avalonia");
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
