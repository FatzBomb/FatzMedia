using FatzBomb.MediaControl.Controllers;
using FatzBomb.MediaControl.Core.Abstractions;
using FatzBomb.MediaControl.Core.Configuration;
using FatzBomb.MediaControl.Core.Files;
using FatzBomb.MediaControl.Pipeline;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.Concurrent;

namespace FatzBomb.MediaControl.Tests;

/// <summary>Stage 043A slice 1: player modes at creation.</summary>
public sealed class PlayerModeTests
{
    [Fact]
    public void Configuration_DefaultsToFullMode()
    {
        Assert.Equal(MediaPlayerMode.Full, MediaPlayerConfiguration.Auto.Mode);
        Assert.Equal(MediaPlayerMode.Full, new MediaPlayerConfiguration(VideoDecodingMethod.Software).Mode);
        Assert.Equal(MediaPlayerMode.Full, default(MediaPlayerConfiguration).Mode);
        Assert.Equal(MediaPlayerMode.Audio, (MediaPlayerConfiguration.Auto with { Mode = MediaPlayerMode.Audio }).Mode);
    }

    [Theory]
    [InlineData(FileType.Audio)]
    [InlineData(FileType.AudioArchive)]
    [InlineData(FileType.AudioCdg)]
    [InlineData(FileType.AudioCdgArchive)]
    [InlineData(FileType.Cdg)]
    [InlineData(FileType.CdgArchive)]
    [InlineData(FileType.Video)]
    [InlineData(FileType.VideoSubtitle)]
    [InlineData(FileType.VideoArchive)]
    [InlineData(FileType.Image)]
    [InlineData(FileType.Unsupported)]
    public void FullMode_OpensEveryFileKindNormally(FileType type) =>
        Assert.Equal(MediaModeRoute.Normal, MediaPlayerModePolicy.RouteFile(MediaPlayerMode.Full, type));

    [Theory]
    [InlineData(FileType.Video, "Normal")]
    [InlineData(FileType.VideoSubtitle, "Normal")]
    [InlineData(FileType.VideoArchive, "Normal")]
    [InlineData(FileType.Image, "Normal")]
    [InlineData(FileType.Audio, "Reject")]
    [InlineData(FileType.AudioArchive, "Reject")]
    [InlineData(FileType.AudioCdg, "Reject")]
    [InlineData(FileType.AudioCdgArchive, "Reject")]
    [InlineData(FileType.Cdg, "Reject")]
    [InlineData(FileType.CdgArchive, "Reject")]
    [InlineData(FileType.Unsupported, "Normal")]
    public void BackgroundMode_AcceptsPicturesOnly(FileType type, string expected) =>
        Assert.Equal(expected, MediaPlayerModePolicy.RouteFile(MediaPlayerMode.Background, type).ToString());

    [Theory]
    [InlineData(FileType.Audio, "Normal")]
    [InlineData(FileType.AudioArchive, "Normal")]
    [InlineData(FileType.AudioCdg, "AudioOnly")]
    [InlineData(FileType.AudioCdgArchive, "AudioOnly")]
    [InlineData(FileType.Video, "AudioOnly")]
    [InlineData(FileType.VideoSubtitle, "AudioOnly")]
    [InlineData(FileType.VideoArchive, "AudioOnly")]
    [InlineData(FileType.Image, "Reject")]
    [InlineData(FileType.Cdg, "Reject")]
    [InlineData(FileType.CdgArchive, "Reject")]
    [InlineData(FileType.Unsupported, "Normal")]
    public void AudioMode_AcceptsAnythingWithAudio(FileType type, string expected) =>
        Assert.Equal(expected, MediaPlayerModePolicy.RouteFile(MediaPlayerMode.Audio, type).ToString());

    [Theory]
    [InlineData(MediaPlayerMode.Full, MediaInputSourceKind.FilePath, false, true)]
    [InlineData(MediaPlayerMode.Full, MediaInputSourceKind.Bgra32Image, false, true)]
    [InlineData(MediaPlayerMode.Full, MediaInputSourceKind.NetworkUri, false, true)]
    [InlineData(MediaPlayerMode.Background, MediaInputSourceKind.FilePath, false, true)]
    [InlineData(MediaPlayerMode.Background, MediaInputSourceKind.Bgra32Image, false, true)]
    [InlineData(MediaPlayerMode.Background, MediaInputSourceKind.NetworkUri, false, false)]
    [InlineData(MediaPlayerMode.Background, MediaInputSourceKind.NetworkUri, true, true)]
    [InlineData(MediaPlayerMode.Audio, MediaInputSourceKind.FilePath, false, true)]
    [InlineData(MediaPlayerMode.Audio, MediaInputSourceKind.Bgra32Image, false, false)]
    [InlineData(MediaPlayerMode.Audio, MediaInputSourceKind.NetworkUri, false, true)]
    [InlineData(MediaPlayerMode.Audio, MediaInputSourceKind.NetworkUri, true, true)]
    public void AcceptsSource_FollowsTheModeTable(MediaPlayerMode mode, MediaInputSourceKind kind, bool networkIsVideo, bool expected) =>
        Assert.Equal(expected, MediaPlayerModePolicy.AcceptsSource(mode, kind, networkIsVideo));

    [Theory]
    [InlineData(MediaPlayerMode.Full, false, "Normal")]
    [InlineData(MediaPlayerMode.Full, true, "Normal")]
    [InlineData(MediaPlayerMode.Background, false, "Reject")]
    [InlineData(MediaPlayerMode.Background, true, "Normal")]
    [InlineData(MediaPlayerMode.Audio, false, "Normal")]
    [InlineData(MediaPlayerMode.Audio, true, "AudioOnly")]
    public void RouteNetwork_FollowsTheModeTable(MediaPlayerMode mode, bool isVideo, string expected) =>
        Assert.Equal(expected, MediaPlayerModePolicy.RouteNetwork(mode, isVideo).ToString());

    [Theory]
    [InlineData(MediaPlayerMode.Full, false, true)]
    [InlineData(MediaPlayerMode.Full, true, false)]
    [InlineData(MediaPlayerMode.Background, true, false)]
    [InlineData(MediaPlayerMode.Audio, false, true)]
    [InlineData(MediaPlayerMode.Audio, true, true)]
    public void NetworkNeedsAudioEngine_OnlyForNetworkAudioAndSoundtracks(MediaPlayerMode mode, bool isVideo, bool expected) =>
        Assert.Equal(expected, MediaPlayerModePolicy.NetworkNeedsAudioEngine(MediaPlayerModePolicy.RouteNetwork(mode, isVideo), isVideo));

    [Theory]
    [InlineData(MediaPlayerMode.Full, true, true, true, false)]
    [InlineData(MediaPlayerMode.Background, true, false, true, true)]
    [InlineData(MediaPlayerMode.Audio, false, true, false, false)]
    public void ModeGraph_CreatesOnlyWhatTheModeNeeds(MediaPlayerMode mode, bool presentation, bool audioOutput, bool backgrounds, bool forcesLoop)
    {
        Assert.Equal(presentation, MediaPlayerModePolicy.UsesPresentation(mode));
        Assert.Equal(audioOutput, MediaPlayerModePolicy.UsesAudioOutput(mode));
        Assert.Equal(backgrounds, MediaPlayerModePolicy.UsesBackgrounds(mode));
        Assert.Equal(forcesLoop, MediaPlayerModePolicy.ForcesLoop(mode));
    }

    [Fact]
    public void RejectionMessage_NamesModeKindAndSource() =>
        Assert.Equal(
            "Background mode does not accept audio files; song.mp3 was not loaded.",
            MediaPlayerModePolicy.RejectionMessage(MediaPlayerMode.Background, MediaPlayerModePolicy.Describe(FileType.Audio), "song.mp3"));

    [Fact]
    public void FullMode_CreatesPlatformCoordinatorAndStartsAudioMonitor()
    {
        var factory = new ModeTestPlatformFactory(new TestMediaEngine());
        using var player = new MediaControlPlayer(factory, MediaPlayerConfiguration.Auto, []);

        Assert.Equal(MediaPlayerMode.Full, player.Mode);
        Assert.Equal(1, factory.CreatePresentationCoordinatorCount);
        Assert.True(factory.Monitor.Started);
        Assert.False(player.LoopMedia);
        Assert.False(player.TransparentBackground);
    }

    [Fact]
    public void AudioMode_UsesNoPresentationCoordinatorAndKeepsSurfaces()
    {
        var factory = new ModeTestPlatformFactory(new TestMediaEngine());
        using var player = new MediaControlPlayer(factory, new MediaPlayerConfiguration { Mode = MediaPlayerMode.Audio }, []);
        var surfaceId = $"audio-mode-{Guid.NewGuid():N}";
        var surface = new MediaPresentationSurfaceDescriptor(
            surfaceId,
            MediaPresentationSurfaceRole.Primary,
            MediaNativeSurfaceDescriptor.Win32Hwnd((nint)0x1234),
            640,
            360);

        // Slice 2: the surface is kept (for a switch to Full or Background), not rendered to.
        List<MediaDiagnosticInfo> diagnostics = CaptureModeDiagnostics(() => Assert.True(player.AttachPresentationSurface(surface)));

        Assert.Equal(MediaPlayerMode.Audio, player.Mode);
        Assert.Equal(0, factory.CreatePresentationCoordinatorCount);
        Assert.True(factory.Monitor.Started);
        MediaDiagnosticInfo notice = Assert.Single(diagnostics, info => info.Message.Contains(surfaceId, StringComparison.Ordinal));
        Assert.Equal(MediaDiagnosticLevel.Info, notice.Level);
        Assert.True(player.ResizePresentationSurface(surfaceId, 800, 450));
        Assert.True(player.DetachPresentationSurface(surfaceId));
        Assert.False(player.ResizePresentationSurface(surfaceId, 640, 360));
    }

    [Fact]
    public void BackgroundMode_StartsNoAudioMonitorAndForcesLoop()
    {
        var engine = new TestMediaEngine();
        var factory = new ModeTestPlatformFactory(engine);
        using var player = new MediaControlPlayer(factory, new MediaPlayerConfiguration { Mode = MediaPlayerMode.Background }, []);

        Assert.Equal(MediaPlayerMode.Background, player.Mode);
        Assert.Equal(1, factory.CreatePresentationCoordinatorCount);
        Assert.False(factory.Monitor.Resolved);
        Assert.False(factory.Monitor.Started);
        Assert.True(player.LoopMedia);
        Assert.True(engine.LoopPlayback);

        List<MediaDiagnosticInfo> diagnostics = CaptureModeDiagnostics(() => player.LoopMedia = false);

        Assert.True(player.LoopMedia);
        Assert.True(engine.LoopPlayback);
        Assert.Contains(diagnostics, info => info.Level == MediaDiagnosticLevel.Warning && info.Message.Contains("always loops", StringComparison.Ordinal));
    }

    [Fact]
    public void BackgroundMode_ServiceProviderNeverInitializesAudioOutput()
    {
        IServiceProvider services = MediaControlServiceProvider.Create(
            new MediaPlayerConfiguration { Mode = MediaPlayerMode.Background },
            [],
            new EmptyPlatformServices());

        IAudioEngine audio = services.GetRequiredService<IAudioEngine>();

        SilentAudioEngine silent = Assert.IsType<SilentAudioEngine>(audio);
        Assert.False(silent.Init());
        Assert.False(silent.Open("song.mp3"));
        Assert.False(silent.Play());
        silent.SetVolume(0.5);
        silent.SetPitch(3);
        silent.SetTempo(10);
        silent.Normalize = true;
        Assert.Equal(0, silent.Position);
        Assert.Same(UnavailableMediaFileAnalyzer.Instance, services.GetRequiredService<IMediaFileAnalyzer>());
    }

    [Fact]
    public void FromColor_FillsASmallStraightAlphaBgraImage()
    {
        MediaInputSource source = MediaInputSource.FromColor(0x80FF4020);

        Assert.Equal(MediaInputSourceKind.Bgra32Image, source.Kind);
        Assert.True(source.IsBgraImage);
        Assert.Equal(MediaInputSource.SolidColorSize, source.Width);
        Assert.Equal(MediaInputSource.SolidColorSize, source.Height);
        Assert.Equal(source.Width * 4, source.Stride);
        Assert.Equal("Solid colour #80FF4020", source.DisplayName);
        byte[] pixels = source.BgraPixels!;
        Assert.Equal(source.Stride * source.Height, pixels.Length);
        for (var i = 0; i < pixels.Length; i += 4)
            Assert.Equal(new byte[] { 0x20, 0x40, 0xFF, 0x80 }, pixels[i..(i + 4)]);

        Assert.Equal("Backdrop", MediaInputSource.FromColor(0xFF000000, "Backdrop").DisplayName);
    }

    [Fact]
    public void TransparentBackground_ForcesCdgBackgroundTransparentAndRestoresOpacity()
    {
        var engine = new TestMediaEngine { TestCdg = new TestCdgEngine() };
        using var player = new MediaControlPlayer(new ModeTestPlatformFactory(engine), MediaPlayerConfiguration.Auto, []);
        player.CdgVisuals.BackgroundOpacity = 60;
        Assert.Equal(60, engine.TestCdg.BackgroundOpacity);

        player.TransparentBackground = true;

        Assert.True(player.TransparentBackground);
        Assert.Equal(0, engine.TestCdg.BackgroundOpacity);
        Assert.Equal(60, player.CdgVisuals.BackgroundOpacity);

        player.CdgVisuals.BackgroundOpacity = 80;
        Assert.Equal(0, engine.TestCdg.BackgroundOpacity);

        player.TransparentBackground = false;
        Assert.Equal(80, engine.TestCdg.BackgroundOpacity);
    }

    [Fact]
    public void AutoBackground_DrivesTheEngineSearch()
    {
        var engine = new TestMediaEngine();
        using var player = new MediaControlPlayer(new ModeTestPlatformFactory(engine), MediaPlayerConfiguration.Auto, []);

        player.AutoBackground = false;

        Assert.False(player.AutoBackground);
        Assert.False(engine.BackgroundAutoDetect);

        player.AutoBackground = true;

        Assert.True(player.AutoBackground);
        Assert.True(engine.BackgroundAutoDetect);
    }

    [Theory]
    [InlineData(10.0, 120.0, 0.0, false)]
    [InlineData(119.96, 120.0, 0.0, true)]
    [InlineData(120.0, 120.0, 0.0, true)]
    [InlineData(80.0, 120.0, 0.5, false)]
    [InlineData(80.0, 120.0, 1.0, true)]
    [InlineData(0.0, 0.0, 0.2, false)]
    public void Soundtrack_EndsAtDurationOrWhenAudioRunsOut(double position, double duration, double stalledSeconds, bool expected) =>
        Assert.Equal(expected, SoundtrackAudioEngine.HasReachedEnd(position, duration, stalledSeconds));

    private static List<MediaDiagnosticInfo> CaptureModeDiagnostics(Action action)
    {
        var captured = new ConcurrentQueue<MediaDiagnosticInfo>();
        void Record(MediaDiagnosticInfo info)
        {
            if (info.Category == MediaPlayerModePolicy.DiagnosticCategory)
                captured.Enqueue(info);
        }

        DiagnosticRouter.DiagnosticOccurred += Record;
        try
        {
            action();
        }
        finally
        {
            DiagnosticRouter.DiagnosticOccurred -= Record;
        }

        return [.. captured];
    }

    private sealed class ModeTestPlatformFactory(TestMediaEngine engine) : IMediaControlPlatformFactory
    {
        private readonly NullPresentationCoordinator _coordinator = new();

        public RecordingAudioDeviceMonitor Monitor { get; } = new();

        public int CreatePresentationCoordinatorCount { get; private set; }

        public string LastNativeLoadMessage => string.Empty;

        public VideoDecodeCapabilities GetVideoDecodeCapabilities() => VideoDecodeCapabilities.Create([], []);

        public IMediaPresentationCoordinator CreatePresentationCoordinator(IMediaEngine mediaEngine, MediaPlayerConfiguration configuration)
        {
            CreatePresentationCoordinatorCount++;
            return _coordinator;
        }

        public IServiceProvider CreateServiceProvider(MediaPlayerConfiguration configuration, IReadOnlyList<string> nativeLibraryPaths) =>
            new MonitorServiceProvider(new TestServiceProvider(engine), Monitor);
    }

    private sealed class MonitorServiceProvider(IServiceProvider inner, RecordingAudioDeviceMonitor monitor) : IServiceProvider
    {
        public object? GetService(Type serviceType)
        {
            if (serviceType != typeof(IAudioDeviceMonitor))
                return inner.GetService(serviceType);

            monitor.Resolved = true;
            return monitor;
        }
    }

    private sealed class RecordingAudioDeviceMonitor : IAudioDeviceMonitor
    {
        public bool Resolved { get; set; }

        public bool Started { get; private set; }

        public bool IsRunning => Started;

        public event EventHandler<AudioDeviceChangeInfo>? DeviceChanged { add { } remove { } }

        public void Start() => Started = true;

        public void Stop() => Started = false;

        public void Dispose()
        {
        }
    }

    private sealed class EmptyPlatformServices : IMediaControlPlatformServices
    {
        public void EnsureNativeLibrariesLoaded(IReadOnlyList<string> nativeLibraryPaths)
        {
        }

        public void AddPlatformServices(IServiceCollection services, MediaPlayerConfiguration configuration)
        {
        }
    }
}
