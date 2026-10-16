using FatzBomb.MediaControl.Controllers;
using FatzBomb.MediaControl.Core.Abstractions;
using FatzBomb.MediaControl.Core.Configuration;

namespace FatzBomb.MediaControl.Tests;

public sealed class PlayerFadeTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ShortFade = TimeSpan.FromMilliseconds(40);
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "fbm-fade-" + Guid.NewGuid().ToString("N"));

    public PlayerFadeTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { }
    }

    [Fact]
    public async Task FadeOut_RampsFadeStageFromFullToMuted()
    {
        var engine = new TestMediaEngine();
        using MediaControlPlayer player = CreatePlayer(engine);

        Assert.True(await player.FadeOutAsync(ShortFade).WaitAsync(Timeout));

        Assert.Equal([(1d, TimeSpan.Zero), (0d, ShortFade)], engine.TestAudio.FadeCalls);
        Assert.Equal(1, engine.TestAudio.Volume);
    }

    [Fact]
    public async Task FadeIn_StartsAtAudibleStartGain()
    {
        var engine = new TestMediaEngine();
        using MediaControlPlayer player = CreatePlayer(engine);
        AudioFadeEnvelope envelope = AudioFadeEnvelope.CreateIncoming([new(0.5, 0)]);

        Assert.True(await player.FadeInAsync(ShortFade, envelope).WaitAsync(Timeout));

        Assert.Equal([(0d, TimeSpan.Zero), (1d, ShortFade / 2)], engine.TestAudio.FadeCalls);
    }

    [Fact]
    public async Task Fade_WithoutFadeStage_ReturnsFalse()
    {
        var engine = new TestMediaEngine();
        engine.TestAudio.FadeAvailable = false;
        using MediaControlPlayer player = CreatePlayer(engine);

        Assert.False(await player.FadeOutAsync(ShortFade).WaitAsync(Timeout));
        Assert.Empty(engine.TestAudio.FadeCalls);
    }

    [Fact]
    public async Task Fade_ZeroDuration_ReturnsFalse()
    {
        var engine = new TestMediaEngine();
        using MediaControlPlayer player = CreatePlayer(engine);

        Assert.False(await player.FadeOutAsync(TimeSpan.Zero).WaitAsync(Timeout));
    }

    [Fact]
    public async Task FadedTransition_FadesOutPrimesIncomingAndFadesIn()
    {
        var engine = new TestMediaEngine();
        using MediaControlPlayer player = CreatePlayer(engine);
        player.PreparedNextTransitionFade = new AudioCrossfadeOptions { Duration = ShortFade };
        Assert.True(player.Load("current.mp3"));
        Assert.True(player.Play());
        Assert.True(await player.PrepareNextAsync(CreateFile("next.mp3")).WaitAsync(Timeout));

        Assert.True(await player.TransitionToPreparedNextAsync().WaitAsync(Timeout));

        Assert.Equal(
            [
                (1d, TimeSpan.Zero), (0d, ShortFade), // outgoing
                (0d, TimeSpan.Zero),                  // primed before Play
                (0d, TimeSpan.Zero), (1d, ShortFade), // incoming
            ],
            engine.TestAudio.FadeCalls);
        Assert.Equal(MediaPlayerState.Playing, engine.State);
        Assert.Equal(PreparedNextState.Empty, player.PreparedNext.State);
    }

    [Fact]
    public async Task FadedTransition_IgnoresPlayOnLoadAndStartsOnce()
    {
        var engine = new TestMediaEngine();
        using MediaControlPlayer player = CreatePlayer(engine);
        player.PlayOnLoad = true;
        player.PreparedNextTransitionFade = new AudioCrossfadeOptions { Duration = ShortFade };
        Assert.True(await player.PrepareNextAsync(CreateFile("next.mp3")).WaitAsync(Timeout));

        Assert.True(await player.TransitionToPreparedNextAsync().WaitAsync(Timeout));
        await Task.Delay(100);

        Assert.Equal(1, engine.PlayCount);
    }

    [Fact]
    public async Task AutomaticFadedTransition_AppliesOnlyIncomingFade()
    {
        var engine = new TestMediaEngine();
        using MediaControlPlayer player = CreatePlayer(engine);
        player.PreparedNextTransitionFade = new AudioCrossfadeOptions { Duration = ShortFade };
        player.AutoTransitionToPreparedNext = true;
        Assert.True(player.Load("current.mp3"));
        Assert.True(player.Play());
        Assert.True(await player.PrepareNextAsync(CreateFile("next.mp3")).WaitAsync(Timeout));
        var transitioned = new TaskCompletionSource<PreparedNextTransitionedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        player.PreparedNextTransitioned += (_, e) => transitioned.TrySetResult(e);

        engine.CompletePlayback();

        Assert.True((await transitioned.Task.WaitAsync(Timeout)).IsAutomatic);
        await WaitForAsync(() => engine.TestAudio.FadeCalls.Count >= 3);
        Assert.Equal([(0d, TimeSpan.Zero), (0d, TimeSpan.Zero), (1d, ShortFade)], engine.TestAudio.FadeCalls);
    }

    [Fact]
    public async Task AutomaticFadedTransition_MutedLeadDelaysLoadAndHostStopCancels()
    {
        var engine = new TestMediaEngine();
        using MediaControlPlayer player = CreatePlayer(engine);
        player.PreparedNextTransitionFade = new AudioCrossfadeOptions
        {
            Duration = TimeSpan.FromMilliseconds(400),
            Incoming = AudioFadeEnvelope.CreateIncoming([new(0.5, 0)]),
        };
        player.AutoTransitionToPreparedNext = true;
        Assert.True(player.Load("current.mp3"));
        Assert.True(player.Play());
        Assert.True(await player.PrepareNextAsync(CreateFile("next.mp3")).WaitAsync(Timeout));
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        player.MediaEnded += (_, _) => ended.TrySetResult();

        engine.CompletePlayback();
        await ended.Task.WaitAsync(Timeout);
        await player.FlushControlOperationsAsync().WaitAsync(Timeout);
        await player.FlushControlOperationsAsync().WaitAsync(Timeout);
        Assert.True(await player.PlayAsync().WaitAsync(Timeout)); // host intervenes during the 200 ms muted lead
        await Task.Delay(500);
        await player.FlushControlOperationsAsync().WaitAsync(Timeout);

        Assert.Equal(PreparedNextState.Ready, player.PreparedNext.State);
        Assert.Equal(2, engine.PlayCount);
    }

    [Fact]
    public async Task ManualFadedTransition_SlotClearedDuringFade_RestoresOutgoingGain()
    {
        var engine = new TestMediaEngine();
        using MediaControlPlayer player = await StartFadedTransitionAsync(engine);
        Task<bool> transition = player.TransitionToPreparedNextAsync();
        await WaitForAsync(() => engine.TestAudio.FadeCalls.Count >= 2);

        Assert.True(player.ClearPreparedNext());

        Assert.False(await transition.WaitAsync(Timeout));
        Assert.Equal((1d, TimeSpan.FromMilliseconds(250)), engine.TestAudio.FadeCalls.Last());
        Assert.Equal(1, engine.PlayCount);
    }

    [Fact]
    public async Task ManualFadedTransition_PauseDuringFade_AbandonsAndRestoresGain()
    {
        var engine = new TestMediaEngine();
        using MediaControlPlayer player = await StartFadedTransitionAsync(engine);
        Task<bool> transition = player.TransitionToPreparedNextAsync();
        await WaitForAsync(() => engine.TestAudio.FadeCalls.Count >= 2);

        Assert.True(await player.PauseAsync().WaitAsync(Timeout));

        Assert.False(await transition.WaitAsync(Timeout));
        Assert.Equal(PreparedNextState.Ready, player.PreparedNext.State);
        Assert.Equal(1, engine.PlayCount);
        Assert.Equal((1d, TimeSpan.FromMilliseconds(250)), engine.TestAudio.FadeCalls.Last());
    }

    [Fact]
    public async Task ManualFadedTransition_SlotReplacedDuringFade_DoesNotLoadTheNewItem()
    {
        var engine = new TestMediaEngine();
        using MediaControlPlayer player = await StartFadedTransitionAsync(engine);
        Task<bool> transition = player.TransitionToPreparedNextAsync();
        await WaitForAsync(() => engine.TestAudio.FadeCalls.Count >= 2);
        var replacement = CreateFile("replacement.mp3");

        Assert.True(await player.PrepareNextAsync(replacement).WaitAsync(Timeout));

        Assert.False(await transition.WaitAsync(Timeout));
        Assert.Equal(PreparedNextState.Ready, player.PreparedNext.State);
        Assert.Equal(replacement, player.PreparedNext.Source!.FilePath);
        Assert.Equal(1, engine.PlayCount);
    }

    [Fact]
    public async Task ManualFadedTransition_CancelFadeDuringOutgoing_AbandonsTransition()
    {
        var engine = new TestMediaEngine();
        using MediaControlPlayer player = await StartFadedTransitionAsync(engine);
        Task<bool> transition = player.TransitionToPreparedNextAsync();
        await WaitForAsync(() => engine.TestAudio.FadeCalls.Count >= 2);

        player.CancelFade();

        Assert.False(await transition.WaitAsync(Timeout));
        Assert.Equal(PreparedNextState.Ready, player.PreparedNext.State);
        Assert.Equal(1, engine.PlayCount);
        Assert.Equal(2, engine.TestAudio.FadeCalls.Count);
    }

    [Fact]
    public void CrossfadeOptions_NullAssignmentFallsBackToDisabled()
    {
        using MediaControlPlayer player = CreatePlayer(new TestMediaEngine());

        player.PreparedNextTransitionFade = null!;

        Assert.Same(AudioCrossfadeOptions.Disabled, player.PreparedNextTransitionFade);
    }

    /// <summary>Plays current media with a prepared next item and a fade long enough to interrupt.</summary>
    private async Task<MediaControlPlayer> StartFadedTransitionAsync(TestMediaEngine engine)
    {
        MediaControlPlayer player = CreatePlayer(engine);
        player.PreparedNextTransitionFade = new AudioCrossfadeOptions { Duration = TimeSpan.FromSeconds(5) };
        Assert.True(player.Load("current.mp3"));
        Assert.True(player.Play());
        Assert.True(await player.PrepareNextAsync(CreateFile("next.mp3")).WaitAsync(Timeout));
        return player;
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(10);
    }

    private string CreateFile(string name)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllBytes(path, [1, 2, 3]);
        return path;
    }

    private static MediaControlPlayer CreatePlayer(TestMediaEngine engine) =>
        new(new FadeTestPlatformFactory(engine), MediaPlayerConfiguration.Auto, []);

    private sealed class FadeTestPlatformFactory(TestMediaEngine engine) : IMediaControlPlatformFactory
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
