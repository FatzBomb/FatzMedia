using FatzMedia.Video.Core.Abstractions;
using FatzMedia.Video.Core.Audio;
using FatzMedia.Video.Core.Configuration;
using FatzMedia.Video.Sample.Player.Shared;
using Microsoft.Extensions.DependencyInjection;

namespace FatzMedia.Video.Tests;

/// <summary>
/// 037A slice 2: the audio engine is chosen by <see cref="MediaPlayerConfiguration.AudioEngineFactory"/>
/// (BASS by default) and, in the test players, by <c>--audio=</c>.
/// </summary>
public sealed class AudioEngineSelectionTests
{
    [Fact]
    public void Configuration_DefaultsToBass()
    {
        Assert.Null(new MediaPlayerConfiguration().AudioEngineFactory);
        Assert.Null(MediaPlayerConfiguration.Auto.AudioEngineFactory);
    }

    [Fact]
    public void ServiceProvider_WithoutAFactory_UsesTheBassEngineAndThePlatformMonitor()
    {
        IServiceProvider services = Create(new MediaPlayerConfiguration(), out TestDeviceMonitor platformMonitor);

        _ = Assert.IsType<AudioEngine>(services.GetRequiredService<IAudioEngine>());
        Assert.Same(platformMonitor, services.GetRequiredService<IAudioDeviceMonitor>());
    }

    [Fact]
    public void ServiceProvider_WithAFactory_UsesItsEngine_AndForwardsDeviceChangesOfBoth()
    {
        var engine = new DeviceReportingEngine();
        IServiceProvider services = Create(new MediaPlayerConfiguration { AudioEngineFactory = new TestFactory(() => engine) }, out TestDeviceMonitor platformMonitor);

        Assert.Same(engine, services.GetRequiredService<IAudioEngine>());
        IAudioDeviceMonitor monitor = services.GetRequiredService<IAudioDeviceMonitor>();
        _ = Assert.IsType<AudioEngineDeviceMonitor>(monitor);
        List<AudioDeviceChangeKind> kinds = [];
        monitor.DeviceChanged += (_, info) => kinds.Add(info.Kind);

        monitor.Start();
        Assert.True(monitor.IsRunning);
        Assert.True(platformMonitor.IsRunning);
        engine.Raise(AudioDeviceChangeKind.DeviceRemoved);
        platformMonitor.Raise(AudioDeviceChangeKind.DefaultDeviceChanged);
        monitor.Dispose();
        engine.Raise(AudioDeviceChangeKind.DeviceAdded);

        Assert.Equal([AudioDeviceChangeKind.DeviceRemoved, AudioDeviceChangeKind.DefaultDeviceChanged], kinds);
        Assert.True(platformMonitor.Disposed);
    }

    [Fact]
    public void ServiceProvider_WhenTheFactoryFails_WarnsAndUsesBass()
    {
        List<MediaDiagnosticInfo> diagnostics = [];
        void Capture(MediaDiagnosticInfo info) => diagnostics.Add(info);
        DiagnosticRouter.DiagnosticOccurred += Capture;
        try
        {
            IServiceProvider services = Create(
                new MediaPlayerConfiguration { AudioEngineFactory = new TestFactory(() => throw new DllNotFoundException("no fatzaudio")) },
                out _);
            _ = Assert.IsType<AudioEngine>(services.GetRequiredService<IAudioEngine>());
        }
        finally
        {
            DiagnosticRouter.DiagnosticOccurred -= Capture;
        }

        Assert.Contains(diagnostics, info => info.Level == MediaDiagnosticLevel.Warning
            && info.Message.Contains("Test could not start (no fatzaudio); using BASS.", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(new string[0], PlayerAudioEngine.Bass, null)]
    [InlineData(new[] { "--audio=fatz" }, PlayerAudioEngine.FatzAudio, null)]
    [InlineData(new[] { "--audio=FatzAudio" }, PlayerAudioEngine.FatzAudio, null)]
    [InlineData(new[] { "--audio=fatz", "--audio=bass" }, PlayerAudioEngine.Bass, null)]
    [InlineData(new[] { "--audio=wasapi" }, PlayerAudioEngine.Bass, "wasapi")]
    public void PlayerOption_ParsesTheAudioEngine(string[] args, PlayerAudioEngine expected, string? unknown)
    {
        PlayerStartupOptions options = PlayerStartupOptions.Parse(args);

        Assert.Equal(expected, options.AudioEngine);
        Assert.Equal(unknown, options.UnknownAudioEngine);
        if (unknown is null)
            Assert.Empty(options.DescribeUnknownValues());
        else
            Assert.Contains(options.DescribeUnknownValues(), line => line == "Unknown --audio value 'wasapi' (use bass or fatz); using bass.");
    }

    private static IServiceProvider Create(MediaPlayerConfiguration configuration, out TestDeviceMonitor platformMonitor)
    {
        var platform = new TestPlatformServices();
        IServiceProvider services = PlayerServiceProvider.Create(configuration, [], platform);
        platformMonitor = platform.Monitor;
        return services;
    }

    private sealed class TestFactory(Func<IAudioEngine> create) : IAudioEngineFactory
    {
        public string Name => "Test";

        public IAudioEngine Create(MediaPlayerConfiguration configuration) => create();
    }

    private sealed class DeviceReportingEngine : IAudioEngine, IAudioDeviceChangeSource
    {
#pragma warning disable CS0067 // the engine events are not used here
        public event EventHandler<AudioEngineState>? StateChanged;
        public event EventHandler? Opened;
        public event EventHandler? Closed;
        public event EventHandler<double>? PositionChanged;
        public event EventHandler? PlaybackEnded;
        public event EventHandler<double>? NormalizationGainChanged;
#pragma warning restore CS0067
        public event EventHandler<AudioDeviceChangeInfo>? DeviceChanged;

        public AudioEngineState State => AudioEngineState.Stopped;
        public int PlayHandle => 0;
        public int SourceHandle => 0;
        public bool Normalize { get; set; }
        public double NormalizationGain => 1;
        public bool IsNormalizationReady => true;
        public double Duration => 0;
        public double Position { get; set; }
        public int CurrentTempo => 0;

        public void Raise(AudioDeviceChangeKind kind) => DeviceChanged?.Invoke(this, new AudioDeviceChangeInfo(kind, null, null, null, 0));

        public Task<double> AnalyzePeakQuickAsync(CancellationToken cancellationToken) => Task.FromResult(0.0);
        public Task<double> AnalyzePeakAsync(CancellationToken cancellationToken) => Task.FromResult(0.0);
        public bool Init(int device = -1, int sampleRate = 44100) => true;
        public bool Open(string filePath) => false;
        public bool Open(IArchiveEntryInfo archiveEntry) => false;
        public bool Play() => false;
        public bool Pause() => false;
        public bool Stop() => false;
        public void Close()
        {
        }

        public void SetVolume(double volume01)
        {
        }

        public void SetPitch(int semitones)
        {
        }

        public void SetTempo(int percent)
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class TestDeviceMonitor : IAudioDeviceMonitor
    {
        public event EventHandler<AudioDeviceChangeInfo>? DeviceChanged;

        public bool IsRunning { get; private set; }

        public bool Disposed { get; private set; }

        public void Start() => IsRunning = true;

        public void Stop() => IsRunning = false;

        public void Dispose() => Disposed = true;

        public void Raise(AudioDeviceChangeKind kind) => DeviceChanged?.Invoke(this, new AudioDeviceChangeInfo(kind, null, null, null, 0));
    }

    // Audio is reported available; BASS itself is not loaded, so the BASS engine is created and
    // fails to initialize, as on a machine without the BASS libraries.
    private sealed class TestPlatformServices : IPlatformServices
    {
        public TestDeviceMonitor Monitor { get; } = new();

        public bool IsAudioAvailable => true;

        public void EnsureNativeLibrariesLoaded(IReadOnlyList<string> nativeLibraryPaths)
        {
        }

        public void AddPlatformServices(IServiceCollection services, MediaPlayerConfiguration configuration) =>
            _ = services.AddSingleton<IAudioDeviceMonitor>(_ => Monitor);
    }
}
