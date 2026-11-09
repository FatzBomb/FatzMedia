using System.Diagnostics;
using FatzMedia.Video.Controllers;
using FatzMedia.Video.Core.Abstractions;
using FatzMedia.Video.Core.Configuration;
using FatzMedia.Video.Core.Threading;
using FatzMedia.Video.Linux.Audio;
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

public sealed class BassDeviceAudioMonitorTests
{
    [Fact]
    public void Poll_NoChange_ReturnsNull()
    {
        var source = new FakeSource(new(48000, 1, "default", "default"));
        var monitor = new BassDeviceAudioMonitor(source, TimeSpan.FromSeconds(1));
        _ = monitor.Poll();

        Assert.Null(monitor.Poll());
    }

    [Fact]
    public void Poll_DefaultDeviceNameChange_IsDefaultDeviceChanged()
    {
        var source = new FakeSource(new(48000, 1, "default", "HDA Intel PCH"));
        var monitor = new BassDeviceAudioMonitor(source, TimeSpan.FromSeconds(1));
        AudioDeviceChangeInfo? raised = null;
        monitor.DeviceChanged += (_, e) => raised = e;
        _ = monitor.Poll();
        source.Snapshot = source.Snapshot with { DefaultDeviceName = "USB Audio" };

        var change = monitor.Poll();

        Assert.Equal(AudioDeviceChangeKind.DefaultDeviceChanged, change!.Kind);
        Assert.Same(change, raised);
    }

    [Fact]
    public void Poll_ZeroSampleRate_IsDeviceRemoved()
    {
        var source = new FakeSource(new(48000, 1, "USB Audio", null));
        var monitor = new BassDeviceAudioMonitor(source, TimeSpan.FromSeconds(1));
        _ = monitor.Poll();
        source.Snapshot = source.Snapshot with { SampleRate = 0 };

        Assert.Equal(AudioDeviceChangeKind.DeviceRemoved, monitor.Poll()!.Kind);
    }

    [Fact]
    public void Poll_UnreadableSource_IsIgnored()
    {
        var source = new FakeSource(new(48000, 1, "x", null)) { Readable = false };

        Assert.Null(new BassDeviceAudioMonitor(source, TimeSpan.FromSeconds(1)).Poll());
    }

    [Fact]
    public void StartStop_RunsAndStopsPollingThread()
    {
        using var monitor = new BassDeviceAudioMonitor(new FakeSource(new(48000, 1, "x", null)), TimeSpan.FromMilliseconds(250));

        monitor.Start();
        Assert.True(monitor.IsRunning);
        monitor.Stop();

        Assert.False(monitor.IsRunning);
    }

    sealed class FakeSource(AudioOutputSnapshot snapshot) : IAudioOutputSnapshotSource
    {
        public AudioOutputSnapshot Snapshot { get; set; } = snapshot;
        public bool Readable { get; init; } = true;

        public bool TryRead(out AudioOutputSnapshot value)
        {
            value = Snapshot;
            return Readable;
        }
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
        Assert.Equal(TimeSpan.FromMilliseconds(250), new LinuxPlatformOptions { AudioDevicePollInterval = TimeSpan.Zero }.AudioDevicePollInterval);
    }

    [Fact]
    public void VideoPipelineServices_UseConfiguredRefreshRate() =>
        Assert.Equal(1.0 / 144, new LinuxVideoPipelinePlatformServices(new LinuxPlatformOptions { DisplayRefreshRateHz = 144 }).GetPresentationRefreshIntervalSeconds(), precision: 12);

    [Fact]
    public void AddPlatformServices_RegistersEveryPlatformContract()
    {
        var dir = CreateFakeLibraries(["libbass.so", "libbass_fx.so", .. LinuxNativeLibraryCatalog.RequiredFfmpeg]);
        try
        {
            var platform = new LinuxPlatformServices(new LinuxNativeLibraryBootstrapper(new LoadingOperations(), dir, "linux-x64"), LinuxPlatformOptions.Default);
            platform.EnsureNativeLibrariesLoaded([dir]);
            var services = new ServiceCollection();
            platform.AddPlatformServices(services, MediaPlayerConfiguration.Auto);
            using var provider = services.BuildServiceProvider();

            Assert.True(platform.IsAudioAvailable);
            Assert.IsType<LinuxPrecisionWaiterFactory>(provider.GetRequiredService<IPrecisionWaiterFactory>());
            Assert.IsType<LinuxVideoPipelinePlatformServices>(provider.GetRequiredService<IVideoPipelinePlatformServices>());
            Assert.Same(SoftwareOnlyVideoHardwareAccelerationServices.Instance, provider.GetRequiredService<IVideoHardwareAccelerationServices>());
            Assert.IsType<BassDeviceAudioMonitor>(provider.GetRequiredService<IAudioDeviceMonitor>());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void WithoutBass_StartsWithoutAudioAndRegistersNoDeviceMonitor()
    {
        var dir = CreateFakeLibraries([.. LinuxNativeLibraryCatalog.RequiredFfmpeg]);
        try
        {
            var platform = new LinuxPlatformServices(new LinuxNativeLibraryBootstrapper(new LoadingOperations(), dir, "linux-x64"), LinuxPlatformOptions.Default);

            var provider = PlayerServiceProvider.Create(MediaPlayerConfiguration.Auto, [dir], platform);

            Assert.False(platform.IsAudioAvailable);
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

            Assert.Contains("libbass.so", exception.Message, StringComparison.Ordinal);
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

        public bool TryLoadBassPlugin(string path) => false;

        public bool TryActivateFfmpeg(string directory) => false;

        public void InstallBassResolver(Func<string, string?> resolvePath)
        {
        }
    }

    /// <summary>Pretends every library loads, so no native code runs.</summary>
    sealed class LoadingOperations : ILinuxNativeOperations
    {
        public bool TryLoad(string path, out IntPtr handle)
        {
            handle = new IntPtr(1);
            return true;
        }

        public bool TryLoadBassPlugin(string path) => true;

        public bool TryActivateFfmpeg(string directory) => true;

        public void InstallBassResolver(Func<string, string?> resolvePath)
        {
        }
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
