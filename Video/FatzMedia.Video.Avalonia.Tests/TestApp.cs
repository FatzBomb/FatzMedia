using Avalonia;
using Avalonia.Headless;

[assembly: AvaloniaTestApplication(typeof(FatzMedia.Video.Avalonia.Tests.TestApp))]

namespace FatzMedia.Video.Avalonia.Tests;

public sealed class TestApp : Application
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<TestApp>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
