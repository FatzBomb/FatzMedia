using System.Diagnostics;
using FatzMedia.Video.Controllers;
using FatzMedia.Video.Core.Abstractions;
using FatzMedia.Video.Core.Configuration;
using FatzMedia.Video.Core.Threading;
using FatzMedia.Video.Linux.Runtime;
using FatzMedia.Video.Linux.Threading;
using FatzMedia.Video.Linux.Video;
using Microsoft.Extensions.DependencyInjection;

namespace FatzMedia.Video.Linux.Tests;

public sealed class LinuxPrecisionWaiterTests
{
    [Fact]
    public void Wait_ReachesTheDeadline()
    {
        using var waiter = new LinuxPrecisionWaiterFactory().Create();
        using var never = new ManualResetEvent(false);
        var duration = TimeSpan.FromMilliseconds(15);

        var start = Stopwatch.GetTimestamp();
        waiter.Wait(duration, never);
        var elapsed = Stopwatch.GetElapsedTime(start);

        Assert.True(waiter.IsHighResolution);
        Assert.True(elapsed >= duration, $"elapsed {elapsed.TotalMilliseconds:F3} ms");
    }

    [Fact]
    public void Wait_ReturnsEarlyWhenCancelled()
    {
        using var waiter = new LinuxPrecisionWaiterFactory().Create();
        using var cancelled = new ManualResetEvent(true);

        var start = Stopwatch.GetTimestamp();
        waiter.Wait(TimeSpan.FromSeconds(5), cancelled);

        Assert.True(Stopwatch.GetElapsedTime(start) < TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void Wait_NonPositiveDurationReturnsImmediately()
    {
        using var waiter = new LinuxPrecisionWaiterFactory().Create();
        using var never = new ManualResetEvent(false);

        waiter.Wait(TimeSpan.Zero, never);
        waiter.Wait(TimeSpan.FromMilliseconds(-5), never);
    }
}

[Collection("File type registry")]
public sealed class LinuxPlatformCompositionTests
{
    [Fact]
    public void Options_ClampValues()
    {
        Assert.Equal(60, LinuxPlatformOptions.Default.DisplayRefreshRateHz);
        Assert.Equal(24, new LinuxPlatformOptions { DisplayRefreshRateHz = 1 }.DisplayRefreshRateHz);
        Assert.Equal(360, new LinuxPlatformOptions { DisplayRefreshRateHz = 1000 }.DisplayRefreshRateHz);
        Assert.Equal(60, new LinuxPlatformOptions { DisplayRefreshRateHz = double.NaN }.DisplayRefreshRateHz);
    }

    [Fact]
    public void VideoPipelineServices_UseConfiguredRefreshRate() =>
        Assert.Equal(1.0 / 144, new LinuxVideoPipelinePlatformServices(new LinuxPlatformOptions { DisplayRefreshRateHz = 144 }).GetPresentationRefreshIntervalSeconds(), precision: 12);

    [Fact]
    public void AddPlatformServices_RegistersEveryPlatformContract()
    {
        var dir = CreateFakeLibraries([.. LinuxNativeLibraryCatalog.RequiredFfmpeg]);
        try
        {
            var platform = new LinuxPlatformServices(new LinuxNativeLibraryBootstrapper(new LoadingOperations(), dir, "linux-x64"), LinuxPlatformOptions.Default);
            platform.EnsureNativeLibrariesLoaded([dir]);
            var services = new ServiceCollection();
            platform.AddPlatformServices(services, MediaPlayerConfiguration.Auto);
            using var provider = services.BuildServiceProvider();

            Assert.IsType<LinuxPrecisionWaiterFactory>(provider.GetRequiredService<IPrecisionWaiterFactory>());
            Assert.IsType<LinuxVideoPipelinePlatformServices>(provider.GetRequiredService<IVideoPipelinePlatformServices>());
            Assert.Same(SoftwareOnlyVideoHardwareAccelerationServices.Instance, provider.GetRequiredService<IVideoHardwareAccelerationServices>());
            Assert.Null(provider.GetService<IAudioDeviceMonitor>());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void WithoutAnAudioEngine_StartsWithoutAudioAndRegistersNoDeviceMonitor()
    {
        var dir = CreateFakeLibraries([.. LinuxNativeLibraryCatalog.RequiredFfmpeg]);
        try
        {
            var platform = new LinuxPlatformServices(new LinuxNativeLibraryBootstrapper(new LoadingOperations(), dir, "linux-x64"), LinuxPlatformOptions.Default);
            var configuration = new MediaPlayerConfiguration { AudioEngineFactory = new FailingAudioEngineFactory() };

            var provider = PlayerServiceProvider.Create(configuration, [dir], platform);

            Assert.Null(provider.GetService<IAudioDeviceMonitor>());
            _ = Assert.IsType<FatzMedia.Video.Pipeline.SilentAudioEngine>(provider.GetRequiredService<IAudioEngine>());
            Assert.NotNull(provider.GetRequiredService<IMediaEngine>());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    static string CreateFakeLibraries(IEnumerable<string> fileNames)
    {
        var dir = Directory.CreateTempSubdirectory("fbm-linux-libs-").FullName;
        foreach (var fileName in fileNames)
            File.WriteAllText(Path.Combine(dir, fileName), "");
        return dir;
    }

    [Fact]
    public void EnsureNativeLibrariesLoaded_MissingLibrariesThrowsWithSearchDetails()
    {
        var empty = Directory.CreateTempSubdirectory("fbm-linux-empty-").FullName;
        try
        {
            var services = new LinuxPlatformServices(new LinuxNativeLibraryBootstrapper(new NoOperations(), empty, "linux-x64"), LinuxPlatformOptions.Default);

            var exception = Assert.Throws<InvalidOperationException>(() => services.EnsureNativeLibrariesLoaded([empty]));

            Assert.Contains("libavcodec.so", exception.Message, StringComparison.Ordinal);
            Assert.Contains("FFmpeg", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(empty, recursive: true);
        }
    }

    [Fact]
    public void Factory_DelegatesPresentationAndReportsEmptyCapabilitiesWithoutFfmpeg()
    {
        var presentation = new RecordingPresentationFactory();
        var factory = new LinuxPlatformFactory(presentation, null, new LinuxNativeLibraryBootstrapper(new NoOperations(), Path.GetTempPath(), "linux-x64"));

        _ = factory.CreatePresentationCoordinator(null!, MediaPlayerConfiguration.Auto);

        Assert.Equal(1, presentation.Created);
        Assert.Empty(factory.GetVideoDecodeCapabilities().Codecs);
        Assert.Equal(string.Empty, factory.LastNativeLoadMessage);
    }

    [Fact]
    public void Factory_RejectsNullPresentationFactory() =>
        Assert.Throws<ArgumentNullException>(() => new LinuxPlatformFactory(null!));

    sealed class NoOperations : ILinuxNativeOperations
    {
        public bool TryLoad(string path, out IntPtr handle)
        {
            handle = IntPtr.Zero;
            return false;
        }

        public bool TryActivateFfmpeg(string directory) => false;
    }

    /// <summary>Pretends every library loads, so no native code runs.</summary>
    sealed class LoadingOperations : ILinuxNativeOperations
    {
        public bool TryLoad(string path, out IntPtr handle)
        {
            handle = new IntPtr(1);
            return true;
        }

        public bool TryActivateFfmpeg(string directory) => true;
    }

    sealed class FailingAudioEngineFactory : IAudioEngineFactory
    {
        public string Name => "Failing";

        public IAudioEngine Create(MediaPlayerConfiguration configuration) => throw new DllNotFoundException("no audio library");
    }

    sealed class RecordingPresentationFactory : IMediaPresentationCoordinatorFactory
    {
        public string BackendName => "Recording";

        public int Created { get; private set; }

        public IMediaPresentationCoordinator Create(IMediaEngine mediaEngine, MediaPlayerConfiguration configuration)
        {
            Created++;
            return null!;
        }
    }
}
