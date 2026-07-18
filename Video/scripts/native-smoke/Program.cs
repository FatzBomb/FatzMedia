using FatzBomb.MediaControl;
using FatzBomb.MediaControl.Core.Configuration;

try
{
    IReadOnlyList<string> nativeLibraryPaths = args.Length == 0 ? [] : args;
    var factory = new WindowsDirect3D11MediaControlPlatformFactory();
    IServiceProvider serviceProvider = factory.CreateServiceProvider(
        MediaPlayerConfiguration.Auto,
        nativeLibraryPaths);

    try
    {
        Console.WriteLine($"Managed package graph loaded from: {AppContext.BaseDirectory}");
        Console.WriteLine($"Native search mode: {(nativeLibraryPaths.Count == 0 ? "predefined paths" : "caller paths")}");
        Console.WriteLine("BASS and FFmpeg native initialization succeeded.");
        return 0;
    }
    finally
    {
        (serviceProvider as IDisposable)?.Dispose();
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex);
    return 1;
}
