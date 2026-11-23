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
        // --surface=framesink: the surfaces' diagnostic switch (read when a surface is created), for comparisons.
        if (StartupOptions.Current.PreferFrameSinkSurface)
            Environment.SetEnvironmentVariable("FATZMEDIA_AVALONIA_FRAMESINK", "1");
        StartupOptions.AbSwapDemo = AbSwapDemoOptions.Parse(args);
        StartupOptions.UiStall = UiStallProbe.Create(args);
        using var debugLog = PlayerDebugLog.Attach(App.LogFilePath, "Player.Avalonia");
        using var uiStall = StartupOptions.UiStall;
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
