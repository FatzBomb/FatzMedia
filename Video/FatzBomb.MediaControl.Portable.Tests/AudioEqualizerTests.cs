using FatzBomb.MediaControl.Controllers;
using FatzBomb.MediaControl.Core.Abstractions;
using FatzBomb.MediaControl.Core.Audio.Filters;
using FatzBomb.MediaControl.Core.Configuration;

namespace FatzBomb.MediaControl.Tests;

public sealed class AudioEqualizerSettingsTests
{
    [Fact]
    public void Default_IsDisabledAndNotEffective()
    {
        Assert.False(AudioEqualizerSettings.Disabled.Enabled);
        Assert.Empty(AudioEqualizerSettings.Disabled.Bands);
        Assert.False(AudioEqualizerSettings.Disabled.IsEffective);
    }

    [Theory]
    [InlineData(5, 30, 0.01, 20, 15, 0.1)]
    [InlineData(50000, -40, 9, 20000, -15, 4)]
    [InlineData(double.NaN, double.NaN, double.NaN, 1000, 0, 1)]
    public void Band_ClampsToSafeRanges(double center, double gain, double bandwidth, double expectedCenter, double expectedGain, double expectedBandwidth)
    {
        var band = new AudioEqualizerBand(center, gain, bandwidth);

        Assert.Equal((expectedCenter, expectedGain, expectedBandwidth), (band.CenterHz, band.GainDb, band.BandwidthOctaves));
    }

    [Fact]
    public void Graphic10Band_UsesIsoCentresAndDefaultsMissingGains()
    {
        AudioEqualizerSettings settings = AudioEqualizerSettings.CreateGraphic10Band(3, -2);

        Assert.True(settings.Enabled);
        Assert.Equal(AudioEqualizerSettings.Graphic10BandCentersHz, settings.Bands.Select(static b => b.CenterHz));
        Assert.Equal([3d, -2d, 0, 0, 0, 0, 0, 0, 0, 0], settings.Bands.Select(static b => b.GainDb));
        Assert.True(settings.IsEffective);
    }

    [Fact]
    public void AllNeutralOrDisabled_IsNotEffective()
    {
        Assert.False(AudioEqualizerSettings.CreateGraphic10Band().IsEffective);
        Assert.False((AudioEqualizerSettings.CreateGraphic10Band(6) with { Enabled = false }).IsEffective);
    }

    [Fact]
    public void Bands_AreCappedAndCopied()
    {
        var bands = Enumerable.Range(0, 20).Select(static i => new AudioEqualizerBand(100 + i, 1)).ToArray();

        var settings = new AudioEqualizerSettings { Enabled = true, Bands = bands };
        bands[0] = new AudioEqualizerBand(999, 9);

        Assert.Equal(AudioEqualizerSettings.MaximumBands, settings.Bands.Count);
        Assert.Equal(100, settings.Bands[0].CenterHz);
    }

    [Fact]
    public void Equality_ComparesBandValues()
    {
        Assert.Equal(AudioEqualizerSettings.CreateGraphic10Band(1, 2), AudioEqualizerSettings.CreateGraphic10Band(1, 2));
        Assert.Equal(AudioEqualizerSettings.CreateGraphic10Band(1, 2).GetHashCode(), AudioEqualizerSettings.CreateGraphic10Band(1, 2).GetHashCode());
        Assert.NotEqual(AudioEqualizerSettings.CreateGraphic10Band(1, 2), AudioEqualizerSettings.CreateGraphic10Band(1, 3));
    }
}

public sealed class EqualizerUpdatePlannerTests
{
    [Fact]
    public void NotEffective_RemovesOnlyWhenAttached()
    {
        Assert.Equal(EqualizerUpdateAction.None, EqualizerUpdatePlanner.Plan(0, false, AudioEqualizerSettings.Disabled));
        Assert.Equal(EqualizerUpdateAction.Remove, EqualizerUpdatePlanner.Plan(10, true, AudioEqualizerSettings.CreateGraphic10Band()));
    }

    [Fact]
    public void Effective_CreatesUpdatesOrRebuilds()
    {
        AudioEqualizerSettings ten = AudioEqualizerSettings.CreateGraphic10Band(4);

        Assert.Equal(EqualizerUpdateAction.Create, EqualizerUpdatePlanner.Plan(0, false, ten));
        Assert.Equal(EqualizerUpdateAction.Update, EqualizerUpdatePlanner.Plan(10, true, ten));
        Assert.Equal(EqualizerUpdateAction.Rebuild, EqualizerUpdatePlanner.Plan(3, true, ten));
    }

    [Fact]
    public void Equalizer_RunsBeforeGainAndFadeStages() =>
        Assert.True(AudioFilterStage.EqualizerPriority > AudioFilterStage.GainPriority);
}

public sealed class PlayerEqualizerTests
{
    [Fact]
    public void SettingEqualizer_AppliesToAudioEngineAndRaisesEvent()
    {
        var engine = new TestMediaEngine();
        using var player = new MediaControlPlayer(new EqualizerTestPlatformFactory(engine), MediaPlayerConfiguration.Auto, []);
        Assert.True(player.Load("song.mp3"));
        MediaValueChangedEventArgs<AudioEqualizerSettings>? raised = null;
        player.EqualizerChanged += (_, e) => raised = e;
        AudioEqualizerSettings settings = AudioEqualizerSettings.CreateGraphic10Band(6, 3);

        player.Equalizer = settings;

        Assert.Equal(settings, engine.TestAudio.Equalizer);
        Assert.Same(AudioEqualizerSettings.Disabled, raised!.PreviousValue);
        Assert.Equal(settings, raised.CurrentValue);
    }

    [Fact]
    public void EqualSettings_AreNoOps()
    {
        var engine = new TestMediaEngine();
        using var player = new MediaControlPlayer(new EqualizerTestPlatformFactory(engine), MediaPlayerConfiguration.Auto, []);
        Assert.True(player.Load("song.mp3"));
        player.Equalizer = AudioEqualizerSettings.CreateGraphic10Band(2);
        var applied = engine.TestAudio.EqualizerApplyCount;
        var raised = 0;
        player.EqualizerChanged += (_, _) => raised++;

        player.Equalizer = AudioEqualizerSettings.CreateGraphic10Band(2);

        Assert.Equal(applied, engine.TestAudio.EqualizerApplyCount);
        Assert.Equal(0, raised);
    }

    [Fact]
    public void NullEqualizer_FallsBackToDisabled()
    {
        using var player = new MediaControlPlayer(new EqualizerTestPlatformFactory(new TestMediaEngine()), MediaPlayerConfiguration.Auto, []);
        player.Equalizer = AudioEqualizerSettings.CreateGraphic10Band(2);

        player.Equalizer = null!;

        Assert.Same(AudioEqualizerSettings.Disabled, player.Equalizer);
    }

    private sealed class EqualizerTestPlatformFactory(TestMediaEngine engine) : IMediaControlPlatformFactory
    {
        public string LastNativeLoadMessage => string.Empty;

        public VideoDecodeCapabilities GetVideoDecodeCapabilities() => VideoDecodeCapabilities.Create([], []);

        public IMediaPresentationCoordinator CreatePresentationCoordinator(IMediaEngine mediaEngine, MediaPlayerConfiguration configuration) =>
            new NullCoordinator();

        public IServiceProvider CreateServiceProvider(MediaPlayerConfiguration configuration, IReadOnlyList<string> nativeLibraryPaths) =>
            new Provider(engine);
    }

    private sealed class Provider(TestMediaEngine engine) : IServiceProvider
    {
        private readonly IArchiveService _archives = new NullArchives();

        public object? GetService(Type serviceType) =>
            serviceType == typeof(IMediaEngine) ? engine
            : serviceType == typeof(IArchiveService) ? _archives
            : null;
    }

    private sealed class NullArchives : IArchiveService
    {
        public IArchiveHandle Open(string path) => throw new NotSupportedException();
    }

    private sealed class NullCoordinator : IMediaPresentationCoordinator
    {
        public event EventHandler? FramePresented { add { } remove { } }

        public VideoDisplayMode VideoDisplayMode { get; set; }
        public BaseVisualTransform BaseVisualTransform { get; set; }

        public bool AttachPresentationSurface(MediaPresentationSurfaceDescriptor surface) => false;
        public bool ResizePresentationSurface(string surfaceId, int pixelWidth, int pixelHeight) => false;
        public bool SetPresentationSurfaceLiveResize(string surfaceId, bool isResizing) => false;
        public bool DetachPresentationSurface(string surfaceId) => false;
        public void RefreshCurrentPosition() { }
        public void RunOnRenderThread(Action action) => action();
        public void StartRendering() { }
        public void StopRendering() { }
        public void RefreshAttachments() { }
        public void Dispose() { }
    }
}
