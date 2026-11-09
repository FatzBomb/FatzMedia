using FatzMedia.Video;
using FatzMedia.Video.Core.Configuration;
using FatzMedia.Video.Direct3D11;

try
{
    IReadOnlyList<string> nativeLibraryPaths = args.Length == 0 ? [] : args;
    var factory = new WindowsDirect3D11PlatformFactory(
        new Direct3D11PresentationOptions());
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
