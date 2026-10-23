using Avalonia;

namespace FatzBomb.MediaControl.Player.Avalonia;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        StartupOptions.Current = StartupOptions.Parse(args);
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
