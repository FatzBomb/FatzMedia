using FatzMedia.Video.Core.Abstractions;
using FatzMedia.Video.Core.Audio;
using FatzMedia.Video.Core.Configuration;
using FatzMedia.Video.Pipeline;
using FatzMedia.Video.Sample.Player.Shared;
using Microsoft.Extensions.DependencyInjection;

namespace FatzMedia.Video.Tests;

/// <summary>
/// The audio engine comes from <see cref="MediaPlayerConfiguration.AudioEngineFactory"/> (null: the built-in
/// FatzAudio engine); its device events are the player's device monitor.
/// </summary>
public sealed class AudioEngineSelectionTests
{
    [Fact]
    public void Configuration_DefaultsToTheBuiltInEngine()
    {
        Assert.Null(new MediaPlayerConfiguration().AudioEngineFactory);
        Assert.Null(MediaPlayerConfiguration.Auto.AudioEngineFactory);
    }

    [Fact]
    public void ServiceProvider_WithAFactory_UsesItsEngine_AndForwardsItsDeviceChanges()
    {
        var engine = new DeviceReportingEngine();
        var services = Create(new MediaPlayerConfiguration { AudioEngineFactory = new TestFactory(() => engine) });

        Assert.Same(engine, services.GetRequiredService<IAudioEngine>());
        Assert.Empty(services.GetRequiredService<AudioEngineStartup>().Problem);
        var monitor = services.GetRequiredService<IAudioDeviceMonitor>();
        _ = Assert.IsType<AudioEngineDeviceMonitor>(monitor);
        List<AudioDeviceChangeKind> kinds = [];
        monitor.DeviceChanged += (_, info) => kinds.Add(info.Kind);

        monitor.Start();
        Assert.True(monitor.IsRunning);
        engine.Raise(AudioDeviceChangeKind.DeviceRemoved);
        monitor.Dispose();
        engine.Raise(AudioDeviceChangeKind.DeviceAdded);

        Assert.Equal([AudioDeviceChangeKind.DeviceRemoved], kinds);
        Assert.False(monitor.IsRunning);
    }

    [Fact]
    public void ServiceProvider_WhenTheFactoryFails_WarnsAndHasNoAudio()
    {
        List<MediaDiagnosticInfo> diagnostics = [];
        void Capture(MediaDiagnosticInfo info) => diagnostics.Add(info);
        DiagnosticRouter.DiagnosticOccurred += Capture;
        IServiceProvider services;
        try
        {
            services = Create(new MediaPlayerConfiguration { AudioEngineFactory = new TestFactory(() => throw new DllNotFoundException("no fatzaudio")) });
        }
        finally
        {
            DiagnosticRouter.DiagnosticOccurred -= Capture;
        }

        _ = Assert.IsType<SilentAudioEngine>(services.GetRequiredService<IAudioEngine>());
        Assert.Null(services.GetService<IAudioDeviceMonitor>());
        Assert.StartsWith("Audio engine Test could not start (no fatzaudio).", services.GetRequiredService<AudioEngineStartup>().Problem, StringComparison.Ordinal);
        Assert.Contains(diagnostics, info => info.Level == MediaDiagnosticLevel.Warning
            && info.Message.StartsWith("Audio engine Test could not start (no fatzaudio).", StringComparison.Ordinal));
    }
    [Theory]
    [InlineData(new string[0], null)]
    [InlineData(new[] { "--audio-device=USB Audio" }, "USB Audio")]
    [InlineData(new[] { "--audio-device=\"Speakers (Realtek)\"" }, "Speakers (Realtek)")]
    public void PlayerOption_ParsesTheAudioDevice(string[] args, string? expected) =>
        Assert.Equal(expected, PlayerStartupOptions.Parse(args).AudioDevice);

    [Theory]
    [InlineData(new string[0], false)]
    [InlineData(new[] { "--video-audio=fatz" }, true)]
    [InlineData(new[] { "--video-audio=ffmpeg" }, false)]
    public void PlayerOption_ParsesWhoDecodesSoundtracks(string[] args, bool engineDecodes) =>
        Assert.Equal(engineDecodes, PlayerStartupOptions.Parse(args).AudioEngineDecodesSoundtracks);

    [Theory]
    [InlineData(new string[0], false, 0, false)]
    [InlineData(new[] { "--cdg-scale" }, true, PlayerStartupOptions.DefaultCdgSmoothness, false)]
    [InlineData(new[] { "--cdg-scale=6", "--cdg-effects" }, true, 6, true)]
    [InlineData(new[] { "--cdg-scale=42" }, true, 10, false)]
    public void PlayerOption_ParsesTheCdgScaling(string[] args, bool enabled, int smoothness, bool effects)
    {
        var scaling = PlayerStartupOptions.Parse(args).CdgScaling;
        Assert.Equal((enabled, smoothness, effects), (scaling.Enabled, scaling.Smoothness, scaling.Effects));
    }

    static IServiceProvider Create(MediaPlayerConfiguration configuration) =>
        PlayerServiceProvider.Create(configuration, [], new TestPlatformServices());

    sealed class TestFactory(Func<IAudioEngine> create) : IAudioEngineFactory
    {
        public string Name => "Test";

        public IAudioEngine Create(MediaPlayerConfiguration configuration) => create();
    }

    sealed class DeviceReportingEngine : IAudioEngine, IAudioDeviceChangeSource
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

    /// <summary>A platform that loads nothing and registers nothing.</summary>
    sealed class TestPlatformServices : IPlatformServices
    {
        public void EnsureNativeLibrariesLoaded(IReadOnlyList<string> nativeLibraryPaths)
        {
        }

        public void AddPlatformServices(IServiceCollection services, MediaPlayerConfiguration configuration)
        {
        }
    }
}
