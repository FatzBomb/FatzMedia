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
        _ = Assert.IsType<UnavailableCdgEngine>(services.GetRequiredService<ICdgEngine>());
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
    [InlineData(new string[0], CdgScale.None, PlayerStartupOptions.DefaultCdgStyle, false)]
    [InlineData(new[] { "--cdg-scale" }, PlayerStartupOptions.DefaultCdgScale, PlayerStartupOptions.DefaultCdgStyle, false)]
    [InlineData(new[] { "--cdg-scale=low", "--cdg-style=pixelart", "--cdg-effects" }, CdgScale.Low, CdgScaleStyle.PixelArt, true)]
    [InlineData(new[] { "--cdg-scale=Med", "--cdg-style=aa" }, CdgScale.Medium, CdgScaleStyle.AntiAliased, false)]
    [InlineData(new[] { "--cdg-scale=6", "--cdg-style=curved" }, CdgScale.High, CdgScaleStyle.Curved, false)]
    [InlineData(new[] { "--cdg-scale=none", "--cdg-style=pixel" }, CdgScale.None, CdgScaleStyle.Pixel, false)]
    [InlineData(new[] { "--cdg-scale=0", "--cdg-style=traced" }, CdgScale.None, CdgScaleStyle.Traced, false)]
    [InlineData(new[] { "--cdg-scale=high", "--cdg-style=clean" }, CdgScale.High, CdgScaleStyle.Clean, false)]
    [InlineData(new[] { "--cdg-enhance=low" }, CdgScale.Low, PlayerStartupOptions.DefaultCdgStyle, false)]
    [InlineData(new[] { "--cdg-enhance" }, PlayerStartupOptions.DefaultCdgScale, PlayerStartupOptions.DefaultCdgStyle, false)]
    [InlineData(new[] { "--cdg-enhance=off" }, CdgScale.None, PlayerStartupOptions.DefaultCdgStyle, false)]
    public void PlayerOption_ParsesTheCdgScaling(string[] args, CdgScale scale, CdgScaleStyle style, bool effects)
    {
        var scaling = PlayerStartupOptions.Parse(args).CdgScaling;
        Assert.Equal((scale, style, effects), (scaling.Scale, scaling.Style, scaling.HasEffects));
        if (effects)
            Assert.Equal(scaling, scaling.WithDefaultEffects());
    }

    [Fact]
    public void CdgScalingOptions_ClampTheirValues()
    {
        var options = new CdgScalingOptions { Scale = (CdgScale)9, Style = (CdgScaleStyle)9, OutlineSize = 12, ShadowSize = -1, BevelStrength = 4, BevelSize = 11 };
        Assert.Equal((CdgScale.None, CdgScaleStyle.Pixel), (options.Scale, options.Style));
        Assert.False(options.Enabled);
        Assert.Equal((10, 0, 0, 4, 10), (options.OutlineSize, options.ShadowSize, options.ShadowDistance, options.BevelStrength, options.BevelSize));
        Assert.True(options.HasEffects);
        Assert.False(new CdgScalingOptions { BevelSize = 1 }.HasEffects);
        Assert.False(new CdgScalingOptions { BevelStrength = 5 }.HasEffects);
    }

    [Theory]
    [InlineData(CdgScale.Low, 1080, 432)]
    [InlineData(CdgScale.Low, 2160, 432)]
    [InlineData(CdgScale.Medium, 720, 432)]
    [InlineData(CdgScale.Medium, 1080, 432)]
    [InlineData(CdgScale.Medium, 1440, 648)]
    [InlineData(CdgScale.Medium, 2160, 1080)]
    [InlineData(CdgScale.High, 720, 648)]
    [InlineData(CdgScale.High, 1080, 1080)]
    [InlineData(CdgScale.High, 1440, 1296)]
    [InlineData(CdgScale.High, 2160, 2160)]
    [InlineData(CdgScale.High, 0, 1080)]
    [InlineData(CdgScale.High, 100, 216)]
    [InlineData(CdgScale.High, 100_000, 4320)]
    public void CdgScale_FollowsTheScreenHeight(CdgScale scale, int display, int height)
    {
        var size = FatzMedia.Video.Audio.FatzAudioMappings.ToCdgRenderSize(scale, display);
        Assert.Equal(height, size.Height);
        Assert.Equal((int)Math.Round(height * 16 / 9.0), size.Width);
    }

    [Fact]
    public void CdgLook_MapsToFatzAudio()
    {
        var options = new CdgScalingOptions { Scale = CdgScale.High, Style = CdgScaleStyle.PixelArt, OutlineSize = 3, ShadowSize = 2, ShadowDistance = 4, BevelStrength = 5, BevelSize = 2 };
        var presentation = FatzMedia.Video.Audio.FatzAudioMappings.ToCdgPresentation(options);
        Assert.True(presentation.PixelArt);
        Assert.Equal(10, FatzMedia.Video.Audio.FatzAudioMappings.ToCdgPresentation(options with { Style = CdgScaleStyle.Curved }).Smoothness);
        Assert.Equal(10, FatzMedia.Video.Audio.FatzAudioMappings.ToCdgPresentation(options with { Style = CdgScaleStyle.Clean }).Smoothness);
        Assert.True(FatzMedia.Video.Audio.FatzAudioMappings.ToCdgPresentation(options with { Style = CdgScaleStyle.AntiAliased }).EdgeBlend);
        var effects = FatzMedia.Video.Audio.FatzAudioMappings.ToCdgEffects(options)!;
        Assert.Equal(1f, effects.Outline!.Size);       // 1.5 CD+G pixels, FatzAudio's unit
        Assert.Equal((0.5f, 1f), (effects.Shadow!.Size, effects.Shadow.Distance));
        Assert.Equal((0.5f, 1f), (effects.Bevel!.Strength, effects.Bevel.Size));
        var most = options with { OutlineSize = 10, ShadowSize = 10, ShadowDistance = 10, BevelSize = 10 };
        effects = FatzMedia.Video.Audio.FatzAudioMappings.ToCdgEffects(most)!;
        Assert.All([effects.Outline!.Size, effects.Shadow!.Size, effects.Shadow.Distance, effects.Bevel!.Size], size => Assert.InRange(size, 0f, FatzMedia.Audio.SurfaceEffects.MaxSize));
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
        public bool Init(int sampleRate = 44100) => true;
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
