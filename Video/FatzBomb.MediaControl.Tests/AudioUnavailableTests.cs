using FatzBomb.MediaControl.Controllers;
using FatzBomb.MediaControl.Core.Abstractions;
using FatzBomb.MediaControl.Core.Configuration;
using FatzBomb.MediaControl.Core.Files;
using FatzBomb.MediaControl.Core.Threading;
using FatzBomb.MediaControl.Pipeline;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.Concurrent;

namespace FatzBomb.MediaControl.Tests;

/// <summary>ENG-033: a player whose platform could not load BASS still starts.</summary>
[Collection("File type registry")]
public sealed class AudioUnavailableTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("fbm-no-audio-").FullName;
    private readonly List<(HashSet<string> Set, string Extension)> _addedTypes = [];

    public void Dispose()
    {
        foreach ((HashSet<string> set, var extension) in _addedTypes)
            _ = set.Remove(extension);
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Theory]
    [InlineData("Normal", FileType.Audio, true)]
    [InlineData("Normal", FileType.AudioArchive, true)]
    [InlineData("Normal", FileType.AudioCdg, true)]
    [InlineData("Normal", FileType.AudioCdgArchive, true)]
    [InlineData("Normal", FileType.Video, false)]
    [InlineData("Normal", FileType.VideoSubtitle, false)]
    [InlineData("Normal", FileType.VideoArchive, false)]
    [InlineData("Normal", FileType.Image, false)]
    [InlineData("Normal", FileType.Cdg, false)]
    [InlineData("Normal", FileType.Unsupported, false)]
    [InlineData("AudioOnly", FileType.Video, true)]
    [InlineData("AudioOnly", FileType.AudioCdg, true)]
    [InlineData("Reject", FileType.Audio, false)]
    public void NeedsAudioEngine_OnlyForAudioAndSoundtracks(string route, FileType type, bool expected) =>
        Assert.Equal(expected, MediaPlayerModePolicy.NeedsAudioEngine(Enum.Parse<MediaModeRoute>(route), type));

    [Theory]
    [InlineData(MediaPlayerMode.Full, true, true)]
    [InlineData(MediaPlayerMode.Full, false, false)]
    [InlineData(MediaPlayerMode.Background, true, false)]
    [InlineData(MediaPlayerMode.Background, false, false)]
    public void VideoAudio_NeedsAudioOutputAndBass(MediaPlayerMode mode, bool audioAvailable, bool expected) =>
        Assert.Equal(expected, VideoPipelineFactory.UsesVideoAudio(mode, audioAvailable));

    [Theory]
    [InlineData(MediaPlayerMode.Full)]
    [InlineData(MediaPlayerMode.Audio)]
    public void ServiceProvider_WithoutBass_UsesSilentEngineAndWarns(MediaPlayerMode mode)
    {
        IServiceProvider? services = null;
        List<MediaDiagnosticInfo> diagnostics = Capture(() => services = Create(mode));

        _ = Assert.IsType<SilentAudioEngine>(services!.GetRequiredService<IAudioEngine>());
        Assert.Same(UnavailableMediaFileAnalyzer.Instance, services!.GetRequiredService<IMediaFileAnalyzer>());
        Assert.NotNull(services!.GetRequiredService<IMediaEngine>());
        MediaDiagnosticInfo warning = Assert.Single(diagnostics, static info => info.Message.StartsWith(MediaPlayerModePolicy.AudioUnavailableMessage, StringComparison.Ordinal));
        Assert.Equal(MediaDiagnosticLevel.Warning, warning.Level);
    }

    [Fact]
    public void ServiceProvider_BackgroundWithoutBass_DoesNotWarn()
    {
        List<MediaDiagnosticInfo> diagnostics = Capture(() => Create(MediaPlayerMode.Background));

        Assert.DoesNotContain(diagnostics, static info => info.Message.Contains(MediaPlayerModePolicy.AudioUnavailableMessage, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(MediaPlayerMode.Full, "song.mp3")]
    [InlineData(MediaPlayerMode.Audio, "song.mp3")]
    [InlineData(MediaPlayerMode.Audio, "clip.mp4")]
    public void Open_MediaThatNeedsAudio_FailsWithErrorDiagnosticAndEvent(MediaPlayerMode mode, string fileName)
    {
        var path = CreateMediaFile(fileName);
        IMediaEngine engine = Create(mode).GetRequiredService<IMediaEngine>();
        var errors = new List<MediaErrorInfo>();
        ((IMediaErrorSource)engine).ErrorOccurred += errors.Add;
        var opened = true;

        List<MediaDiagnosticInfo> diagnostics = Capture(() => opened = engine.Open(path));

        Assert.False(opened);
        Assert.False(engine.IsOpen);
        MediaErrorInfo error = Assert.Single(errors);
        Assert.Equal(MediaErrorCode.NativeLibraryMissing, error.Code);
        Assert.Equal($"{MediaPlayerModePolicy.AudioUnavailableMessage}; {fileName} was not loaded.", error.Message);
        Assert.Contains(diagnostics, info => info.Level == MediaDiagnosticLevel.Error && info.Message == error.Message);
        engine.Dispose();
    }

    [Fact]
    public void Open_NetworkAudioWithoutBass_FailsCleanly()
    {
        IMediaEngine engine = Create(MediaPlayerMode.Full).GetRequiredService<IMediaEngine>();
        var errors = new List<MediaErrorInfo>();
        ((IMediaErrorSource)engine).ErrorOccurred += errors.Add;

        Assert.False(engine.Open(MediaInputSource.FromUri(new Uri("https://example.invalid/stream.mp3"))));

        Assert.Equal(MediaErrorCode.NativeLibraryMissing, Assert.Single(errors).Code);
        engine.Dispose();
    }

    [Fact]
    public void Player_WithoutBass_ConstructsAndLoadReportsTheError()
    {
        var path = CreateMediaFile("song.mp3");
        using var player = new MediaControlPlayer(new NoAudioPlatformFactory(), MediaPlayerConfiguration.Auto, []);
        var errors = new ConcurrentQueue<MediaErrorEventArgs>();
        player.MediaError += (_, args) => errors.Enqueue(args);

        Assert.False(player.Load(path));

        Assert.True(SpinWait.SpinUntil(() => !errors.IsEmpty, TimeSpan.FromSeconds(5)));
        Assert.Contains(errors, static args => args.Message.StartsWith(MediaPlayerModePolicy.AudioUnavailableMessage, StringComparison.Ordinal));
    }

    private static IServiceProvider Create(MediaPlayerMode mode) =>
        MediaControlServiceProvider.Create(new MediaPlayerConfiguration { Mode = mode }, [], new NoAudioPlatformServices());

    private string CreateMediaFile(string fileName)
    {
        var extension = Path.GetExtension(fileName);
        HashSet<string> set = extension == ".mp4" ? FileTypes.Video : FileTypes.Audio;
        if (set.Add(extension))
            _addedTypes.Add((set, extension));

        var path = Path.Combine(_root, fileName);
        File.WriteAllBytes(path, [0, 1, 2, 3]);
        return path;
    }

    private static List<MediaDiagnosticInfo> Capture(Action action)
    {
        var captured = new ConcurrentQueue<MediaDiagnosticInfo>();
        void Record(MediaDiagnosticInfo info) => captured.Enqueue(info);

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

    /// <summary>A platform whose BASS libraries did not load: no audio, no device monitor.</summary>
    private sealed class NoAudioPlatformServices : IMediaControlPlatformServices
    {
        public bool IsAudioAvailable => false;

        public void EnsureNativeLibrariesLoaded(IReadOnlyList<string> nativeLibraryPaths)
        {
        }

        public void AddPlatformServices(IServiceCollection services, MediaPlayerConfiguration configuration)
        {
            _ = services.AddSingleton<IPrecisionWaiterFactory>(new SystemPrecisionWaiterFactory());
            _ = services.AddSingleton<IVideoPipelinePlatformServices>(new FixedRefreshServices());
            _ = services.AddSingleton<IVideoHardwareAccelerationServices>(SoftwareOnlyVideoHardwareAccelerationServices.Instance);
        }
    }

    private sealed class FixedRefreshServices : IVideoPipelinePlatformServices
    {
        public double GetPresentationRefreshIntervalSeconds() => 1.0 / 60;
    }

    private sealed class NoAudioPlatformFactory : IMediaControlPlatformFactory
    {
        private readonly NullPresentationCoordinator _coordinator = new();

        public string LastNativeLoadMessage => string.Empty;

        public VideoDecodeCapabilities GetVideoDecodeCapabilities() => VideoDecodeCapabilities.Create([], []);

        public IMediaPresentationCoordinator CreatePresentationCoordinator(IMediaEngine mediaEngine, MediaPlayerConfiguration configuration) => _coordinator;

        public IServiceProvider CreateServiceProvider(MediaPlayerConfiguration configuration, IReadOnlyList<string> nativeLibraryPaths) =>
            MediaControlServiceProvider.Create(configuration, nativeLibraryPaths, new NoAudioPlatformServices());
    }
}
