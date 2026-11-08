using Avalonia;
using Avalonia.Headless;

[assembly: AvaloniaTestApplication(typeof(FatzBomb.MediaControl.Avalonia.Tests.TestApp))]

namespace FatzBomb.MediaControl.Avalonia.Tests;

public sealed class TestApp : Application
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<TestApp>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
