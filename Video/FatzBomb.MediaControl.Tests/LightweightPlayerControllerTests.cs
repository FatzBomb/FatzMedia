using System.Collections.Concurrent;
using FatzBomb.MediaControl.Controllers;
using FatzBomb.MediaControl.Core.Abstractions;
using FatzBomb.MediaControl.Core.Configuration;

namespace FatzBomb.MediaControl.Tests;

/// <summary>
/// Stage 046A slice 2: the karaoke and background controls' rules and player helper, moved into
/// Core from the WPF adapter (<see cref="LightweightPlayerPolicy"/>, <see cref="LightweightPlayerController"/>).
/// </summary>
public sealed class LightweightPlayerControllerTests
{
    [Theory]
    [InlineData(MediaContent.Cdg, "song.cdg", true)]
    [InlineData(MediaContent.Audio | MediaContent.Cdg, "song.mp3", true)]
    [InlineData(MediaContent.Audio | MediaContent.Cdg | MediaContent.Archive, "song.zip", true)]
    [InlineData(MediaContent.Audio | MediaContent.Subtitle, "song.kar", true)]
    [InlineData(MediaContent.Audio | MediaContent.Subtitle, "song.MID", true)]
    [InlineData(MediaContent.Audio | MediaContent.Subtitle, "song.mp3", false)]
    [InlineData(MediaContent.Audio, "song.kar", false)]
    [InlineData(MediaContent.Audio, "song.mp3", false)]
    [InlineData(MediaContent.Video, "clip.mp4", false)]
    [InlineData(MediaContent.Video | MediaContent.Cdg, "clip.mp4", false)]
    public void KaraokePolicy_AcceptsCdgAndMidiLyricsOnly(MediaContent content, string path, bool expected) =>
        Assert.Equal(expected, LightweightPlayerPolicy.AcceptsLoadedMedia(LightweightPlayerKind.Karaoke, content, path));

    [Theory]
    [InlineData(MediaContent.Image, true)]
    [InlineData(MediaContent.Video, true)]
    [InlineData(MediaContent.Video | MediaContent.Audio, true)]
    [InlineData(MediaContent.Video | MediaContent.Subtitle, false)]
    [InlineData(MediaContent.Image | MediaContent.Cdg, false)]
    [InlineData(MediaContent.Audio, false)]
    [InlineData(MediaContent.None, false)]
    public void BackgroundPolicy_AcceptsPicturesWithoutCdgOrLyrics(MediaContent content, bool expected) =>
        Assert.Equal(expected, LightweightPlayerPolicy.AcceptsLoadedMedia(LightweightPlayerKind.Background, content, "any.file"));

    [Fact]
    public void Policy_CreatesBackgroundPlayersInBackgroundMode_AndStartsOnlyVideo()
    {
        Assert.Equal(MediaPlayerMode.Background, LightweightPlayerPolicy.CreateConfiguration(LightweightPlayerKind.Background).Mode);
        Assert.Equal(MediaPlayerMode.Full, LightweightPlayerPolicy.CreateConfiguration(LightweightPlayerKind.Karaoke).Mode);
        Assert.Equal(VideoDecodingMethod.Hardware, LightweightPlayerPolicy.CreateConfiguration(LightweightPlayerKind.Background).VideoDecoding);

        Assert.True(LightweightPlayerPolicy.PlaysAfterLoad(LightweightPlayerKind.Karaoke, MediaContent.Cdg));
        Assert.True(LightweightPlayerPolicy.PlaysAfterLoad(LightweightPlayerKind.Background, MediaContent.Video));
        Assert.False(LightweightPlayerPolicy.PlaysAfterLoad(LightweightPlayerKind.Background, MediaContent.Image));

        Assert.True(LightweightPlayerPolicy.IsMidiKaraokeFile(@"C:\songs\a.rmi"));
        Assert.False(LightweightPlayerPolicy.IsMidiKaraokeFile("a.mp3"));
        Assert.False(LightweightPlayerPolicy.IsMidiKaraokeFile(null));
    }

    [Fact]
    public async Task KaraokeLoad_AcceptsCdg_ConfiguresThePlayerAndStartsIt()
    {
        var engine = new TestMediaEngine { Content = MediaContent.Audio | MediaContent.Cdg };
        var factory = new LightweightTestPlatformFactory(engine);
        using var controller = new LightweightPlayerController(LightweightPlayerKind.Karaoke, "Test.Karaoke");
        var changes = 0;
        controller.PlayerChanged += (_, _) => changes++;

        Assert.True(await controller.LoadAsync("song.mp3", factory));

        MediaControlPlayer player = Assert.IsType<MediaControlPlayer>(controller.Player);
        Assert.Same(player, controller.Session);
        Assert.Equal(1, changes);
        Assert.Equal(MediaPlayerMode.Full, player.Mode);
        Assert.False(player.LoopMedia);
        Assert.True(player.ShowMidiLyrics);
        Assert.True(player.PlayOnLoad);
        Assert.True(controller.IsMediaOpen);
        Assert.Equal(1, engine.PlayCount);
        Assert.Equal(1, factory.CreatedPlayers);
    }

    [Fact]
    public async Task KaraokeLoad_ClosesVideoAfterInspection()
    {
        var engine = new TestMediaEngine { Content = MediaContent.Video | MediaContent.Audio };
        using var controller = new LightweightPlayerController(LightweightPlayerKind.Karaoke, "Test.Karaoke");

        Assert.False(await controller.LoadAsync("clip.mp4", new LightweightTestPlatformFactory(engine)));

        await WaitUntilAsync(() => !engine.IsOpen);
        Assert.Equal(0, engine.PlayCount);
        Assert.NotNull(controller.Player);
    }

    [Fact]
    public async Task BackgroundLoad_UsesBackgroundMode_LoopsAndShowsAnImageWithoutPlaying()
    {
        var engine = new TestMediaEngine { Content = MediaContent.Image };
        var factory = new LightweightTestPlatformFactory(engine);
        using var controller = new LightweightPlayerController(LightweightPlayerKind.Background, "Test.Background");

        Assert.True(await controller.LoadAsync("picture.png", factory));

        MediaControlPlayer player = controller.Player!;
        Assert.Equal(MediaPlayerMode.Background, player.Mode);
        Assert.True(player.LoopMedia);
        Assert.Equal(VideoDisplayMode.Zoom, player.VideoDisplayMode);
        Assert.Equal(0, engine.PlayCount);
        Assert.Equal(MediaPlayerMode.Background, factory.LastConfiguration?.Mode);
    }

    [Fact]
    public async Task BackgroundLoad_RejectsAudio_InBackgroundMode()
    {
        var engine = new TestMediaEngine { Content = MediaContent.Audio };
        using var controller = new LightweightPlayerController(LightweightPlayerKind.Background, "Test.Background");

        Assert.False(await controller.LoadAsync("song.mp3", new LightweightTestPlatformFactory(engine)));
        Assert.False(engine.IsOpen);
    }

    [Fact]
    public async Task AutoPlayOff_LoadsWithoutStarting()
    {
        var engine = new TestMediaEngine { Content = MediaContent.Video };
        using var controller = new LightweightPlayerController(LightweightPlayerKind.Background, "Test.Background") { AutoPlay = false };

        Assert.True(await controller.LoadAsync("clip.mp4", new LightweightTestPlatformFactory(engine)));
        Assert.Equal(0, engine.PlayCount);
        Assert.False(controller.Player!.PlayOnLoad);
    }

    [Fact]
    public async Task MissingPlatformFactory_WarnsAndCreatesNothing()
    {
        const string category = "Test.NoFactory";
        var warnings = new ConcurrentQueue<MediaDiagnosticInfo>();
        void Record(MediaDiagnosticInfo info)
        {
            if (info.Category == category)
                warnings.Enqueue(info);
        }

        DiagnosticRouter.DiagnosticOccurred += Record;
        try
        {
            using var controller = new LightweightPlayerController(LightweightPlayerKind.Karaoke, category);
            Assert.False(await controller.LoadAsync("song.cdg", null));
            Assert.Null(controller.Player);
            Assert.False(controller.HasPlayerOrPendingCreation);
        }
        finally
        {
            DiagnosticRouter.DiagnosticOccurred -= Record;
        }

        MediaDiagnosticInfo warning = Assert.Single(warnings);
        Assert.Equal(MediaDiagnosticLevel.Warning, warning.Level);
    }

    [Fact]
    public async Task FailedCreation_RaisesAnErrorAndAllowsAnotherAttempt()
    {
        const string category = "Test.FailedCreation";
        var errors = new ConcurrentQueue<MediaDiagnosticInfo>();
        void Record(MediaDiagnosticInfo info)
        {
            if (info.Category == category && info.Level == MediaDiagnosticLevel.Error)
                errors.Enqueue(info);
        }

        DiagnosticRouter.DiagnosticOccurred += Record;
        try
        {
            var factory = new LightweightTestPlatformFactory(new TestMediaEngine()) { Fail = true };
            using var controller = new LightweightPlayerController(LightweightPlayerKind.Karaoke, category);
            Assert.Null(await controller.EnsurePlayerAsync(factory));
            Assert.False(controller.HasPlayerOrPendingCreation);

            factory.Fail = false;
            Assert.NotNull(await controller.EnsurePlayerAsync(factory));
        }
        finally
        {
            DiagnosticRouter.DiagnosticOccurred -= Record;
        }

        Assert.Single(errors);
    }

    [Fact]
    public async Task ConcurrentRequests_ShareOneCreation()
    {
        var factory = new LightweightTestPlatformFactory(new TestMediaEngine()) { Gate = new ManualResetEventSlim() };
        using var controller = new LightweightPlayerController(LightweightPlayerKind.Background, "Test.Concurrent");
        var changes = 0;
        controller.PlayerChanged += (_, _) => Interlocked.Increment(ref changes);

        Task<MediaControlPlayer?> first = controller.EnsurePlayerAsync(factory);
        Task<MediaControlPlayer?> second = controller.EnsurePlayerAsync(factory);
        Assert.True(controller.HasPlayerOrPendingCreation);
        factory.Gate.Set();

        MediaControlPlayer? a = await first;
        MediaControlPlayer? b = await second;
        Assert.NotNull(a);
        Assert.Same(a, b);
        Assert.Equal(1, factory.CreatedPlayers);
        Assert.Equal(1, Volatile.Read(ref changes));
    }

    [Fact]
    public async Task ReleaseDuringCreation_DisposesThatPlayer_AndTheNextLoadCreatesAnother()
    {
        var engine = new TestMediaEngine { Content = MediaContent.Image };
        var factory = new LightweightTestPlatformFactory(engine, new TestMediaEngine { Content = MediaContent.Image }) { Gate = new ManualResetEventSlim() };
        using var controller = new LightweightPlayerController(LightweightPlayerKind.Background, "Test.Release");
        var changes = 0;
        controller.PlayerChanged += (_, _) => Interlocked.Increment(ref changes);

        Task<bool> abandoned = controller.LoadAsync("picture.png", factory);
        controller.ReleasePlayer();
        Assert.False(controller.HasPlayerOrPendingCreation);
        factory.Gate.Set();

        Assert.False(await abandoned);
        await WaitUntilAsync(() => engine.DisposeCount > 0);
        Assert.Null(controller.Player);
        Assert.Equal(0, Volatile.Read(ref changes));

        Assert.True(await controller.LoadAsync("picture.png", factory));
        Assert.Equal(2, factory.CreatedPlayers);
        Assert.Equal(1, Volatile.Read(ref changes));
    }

    [Fact]
    public async Task NewerLoad_AbandonsTheOlderOne()
    {
        var engine = new TestMediaEngine { Content = MediaContent.Image };
        var factory = new LightweightTestPlatformFactory(engine) { Gate = new ManualResetEventSlim() };
        using var controller = new LightweightPlayerController(LightweightPlayerKind.Background, "Test.Newer");

        Task<bool> older = controller.LoadAsync("first.png", factory);
        Task<bool> newer = controller.LoadAsync("second.png", factory);
        factory.Gate.Set();

        Assert.False(await older);
        Assert.True(await newer);
        Assert.Equal(1, factory.CreatedPlayers);
    }

    [Fact]
    public async Task Release_UnbindsBeforeDisposing_AndDisposeEndsTheController()
    {
        var engine = new TestMediaEngine { Content = MediaContent.Image };
        var controller = new LightweightPlayerController(LightweightPlayerKind.Background, "Test.Dispose");
        Assert.True(await controller.LoadAsync("picture.png", new LightweightTestPlatformFactory(engine)));
        var disposedWhenUnbound = new List<bool>();
        controller.PlayerChanged += (_, _) => disposedWhenUnbound.Add(engine.DisposeCount > 0);

        controller.Dispose();

        Assert.Equal([false], disposedWhenUnbound);
        Assert.True(engine.DisposeCount > 0);
        Assert.Null(controller.Player);
        _ = await Assert.ThrowsAsync<ObjectDisposedException>(() => controller.EnsurePlayerAsync(new LightweightTestPlatformFactory(new TestMediaEngine())));
        Assert.False(controller.TryQueueReload());
    }

    [Fact]
    public void ReloadQueue_CoalescesUntilCleared()
    {
        using var controller = new LightweightPlayerController(LightweightPlayerKind.Karaoke, "Test.Queue");

        Assert.True(controller.TryQueueReload());
        Assert.False(controller.TryQueueReload());
        controller.ClearQueuedReload();
        Assert.True(controller.TryQueueReload());

        var reload = controller.BeginReload();
        Assert.True(controller.IsCurrentReload(reload));
        _ = controller.BeginReload();
        Assert.False(controller.IsCurrentReload(reload));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TestEnvironment.EventuallyTimeout;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Condition not met in time.");
            await Task.Delay(10);
        }
    }

    private sealed class LightweightTestPlatformFactory(params TestMediaEngine[] engines) : IMediaControlPlatformFactory
    {
        private readonly ConcurrentQueue<TestMediaEngine> _engines = new(engines);
        private readonly NullPresentationCoordinator _coordinator = new();
        private TestMediaEngine _last = engines[0];
        private int _createdPlayers;

        public ManualResetEventSlim? Gate { get; init; }

        public bool Fail { get; set; }

        public int CreatedPlayers => Volatile.Read(ref _createdPlayers);

        public MediaPlayerConfiguration? LastConfiguration { get; private set; }

        public string LastNativeLoadMessage => string.Empty;

        public VideoDecodeCapabilities GetVideoDecodeCapabilities() => VideoDecodeCapabilities.Create([], []);

        public IMediaPresentationCoordinator CreatePresentationCoordinator(IMediaEngine mediaEngine, MediaPlayerConfiguration configuration) => _coordinator;

        public IServiceProvider CreateServiceProvider(MediaPlayerConfiguration configuration, IReadOnlyList<string> nativeLibraryPaths)
        {
            Assert.True(Gate?.Wait(TestEnvironment.EventuallyTimeout) ?? true, "The test gate was not opened.");
            if (Fail)
                throw new InvalidOperationException("native libraries missing");

            LastConfiguration = configuration;
            _ = Interlocked.Increment(ref _createdPlayers);
            if (_engines.TryDequeue(out TestMediaEngine? engine))
                _last = engine;
            return new TestServiceProvider(_last);
        }
    }
}
