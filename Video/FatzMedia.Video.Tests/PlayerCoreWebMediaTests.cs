using System.Collections.Concurrent;
using FatzMedia.Video.Core.Abstractions;
using FatzMedia.Video.Core.Configuration;

namespace FatzMedia.Video.Tests;

/// <summary>
/// Stage 060A slice 4: YouTube addresses passed to <c>Load</c> play through the attached web player; every
/// other source takes the engine path as before; and no web call waits for the web view.
/// </summary>
public sealed class PlayerCoreWebMediaTests
{
    const string Video = "https://youtu.be/dQw4w9WgXcQ";

    static (PlayerCore Player, TestMediaEngine Engine) CreatePlayer(MediaPlayerConfiguration? configuration = null)
    {
        var engine = new TestMediaEngine();
        return (new PlayerCore(new PlayerCoreTests.TestPlatformFactory(engine), configuration ?? MediaPlayerConfiguration.Auto, []), engine);
    }

    [Fact]
    public async Task WithoutAWebPlayer_AYouTubeAddressRaisesAnErrorAndTheEngineIsUntouched()
    {
        var (player, engine) = CreatePlayer();
        using var owner = player;
        var errors = new ConcurrentQueue<MediaErrorEventArgs>();
        player.MediaError += (_, e) => errors.Enqueue(e);

        Assert.False(player.Load(Video));
        await player.FlushControlOperationsAsync();

        Assert.Equal(MediaErrorCode.WebMediaUnavailable, Assert.Single(errors).Code);
        Assert.Empty(engine.OpenedSources);
        Assert.False(player.IsOpen);
    }

    [Theory]
    [InlineData("https://www.youtube.com/playlist?list=PL1", "playlist")]
    [InlineData("https://www.youtube.com/@someone", "channel")]
    [InlineData("https://www.youtube.com/results?search_query=x", "search")]
    public async Task AnUnsupportedYouTubeAddress_ReturnsFalseWithTheReason_AndNeverReachesTheEngineOrANetworkOpen(string address, string word)
    {
        var (player, engine) = CreatePlayer();
        using var owner = player;
        var web = new FakeWebPlayer();
        player.AttachWebMediaPlayer(web);
        var errors = new ConcurrentQueue<MediaErrorEventArgs>();
        player.MediaError += (_, e) => errors.Enqueue(e);

        Assert.False(player.Load(address));
        Assert.False(await player.LoadAsync(address));

        Assert.Equal(2, errors.Count);
        Assert.All(errors, e =>
        {
            Assert.Equal(MediaErrorCode.UnsupportedFormat, e.Code);
            Assert.Contains(word, e.Message, StringComparison.OrdinalIgnoreCase);
        });
        Assert.Empty(engine.OpenedSources);
        Assert.Equal(0, web.LoadCount);
    }

    [Fact]
    public async Task SyncLoad_StartsTheWebItemWithoutWaitingForTheWebView()
    {
        // The web view needs the UI thread that a synchronous Load blocks: the call must return while the web
        // player's load is still pending, and the item becomes ready later.
        var (player, _) = CreatePlayer();
        using var owner = player;
        var web = new FakeWebPlayer { LoadGate = new TaskCompletionSource() };
        player.AttachWebMediaPlayer(web);
        var loaded = new TaskCompletionSource<MediaLoadedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        player.MediaLoaded += (_, e) => loaded.TrySetResult(e);

        var returned = Task.Run(() => player.Load(Video));
        Assert.True(await returned.WaitAsync(TestEnvironment.EventuallyTimeout));
        Assert.False(loaded.Task.IsCompleted);
        Assert.True(player.IsOpen);

        web.Duration = TimeSpan.FromSeconds(212);
        web.LoadGate.SetResult();
        var args = await loaded.Task.WaitAsync(TestEnvironment.EventuallyTimeout);

        Assert.Equal(TimeSpan.FromSeconds(212), args.Duration);
        Assert.Equal(MediaInputSourceKind.WebMedia, args.Source.Kind);
        Assert.Equal(TimeSpan.FromSeconds(212), player.Duration);
        Assert.True(player.IsWebMedia);
    }

    [Fact]
    public async Task SyncControls_OnAWebItem_IssueTheCommandAndReturnWithoutWaiting()
    {
        var (player, engine) = CreatePlayer();
        using var owner = player;
        var web = new FakeWebPlayer { CommandGate = new TaskCompletionSource() };
        player.AttachWebMediaPlayer(web);
        Assert.True(player.Load(Video));
        await player.FlushControlOperationsAsync();

        // Every web command stays pending: the sync calls must still return.
        var calls = Task.Run(() => (player.Play(), player.Pause(), player.Seek(30), player.Stop()));
        var result = await calls.WaitAsync(TestEnvironment.EventuallyTimeout);

        Assert.Equal((true, true, true, true), result);
        Assert.Equal(["play", "pause", "seek:30", "pause", "seek:0"], [.. web.Commands.ToArray().Where(c => c != "volume:1")]);
        Assert.Equal(0, engine.PlayCount);
        web.CommandGate.SetResult();
    }

    [Fact]
    public async Task LoadAsync_CompletesWhenTheWebItemIsReady_AndAutoplaysOnlyWithPlayOnLoad()
    {
        var (player, _) = CreatePlayer();
        using var owner = player;
        var web = new FakeWebPlayer { Duration = TimeSpan.FromSeconds(90) };
        player.AttachWebMediaPlayer(web);

        Assert.True(await player.LoadAsync(Video));
        Assert.False(web.LastAutoplay);

        player.PlayOnLoad = true;
        Assert.True(await player.LoadAsync("https://www.youtube.com/watch?v=aaaaaaaaaaa"));
        Assert.True(web.LastAutoplay);
        Assert.Equal("aaaaaaaaaaa", web.Current!.ItemId);
    }

    [Fact]
    public async Task WebStateEvents_MapToPlayerStateAndEvents()
    {
        var (player, _) = CreatePlayer();
        using var owner = player;
        var web = new FakeWebPlayer();
        player.AttachWebMediaPlayer(web);
        var seen = new ConcurrentQueue<string>();
        player.MediaStarted += (_, _) => seen.Enqueue("started");
        player.MediaPaused += (_, e) => seen.Enqueue($"paused@{e.Position.TotalSeconds}");
        player.MediaEnded += (_, _) => seen.Enqueue("ended");
        player.StateChanged += (_, e) => seen.Enqueue($"state:{e.CurrentState}");
        player.PositionChanged += (_, e) => seen.Enqueue($"pos:{e.Position.TotalSeconds}");
        Assert.True(await player.LoadAsync(Video));
        await player.FlushControlOperationsAsync();
        while (seen.TryDequeue(out _)) { }

        web.RaiseState(WebMediaState.Playing, 0);
        Assert.Equal(MediaPlayerState.Playing, player.State);
        web.Position = TimeSpan.FromSeconds(5);
        web.RaisePosition(TimeSpan.FromSeconds(5));
        web.RaiseState(WebMediaState.Paused, 5);
        Assert.Equal(MediaPlayerState.Paused, player.State);
        web.RaiseState(WebMediaState.Playing, 5);
        web.RaiseState(WebMediaState.Ended, 90);
        await player.FlushControlOperationsAsync();

        Assert.Equal(MediaPlayerState.Stopped, player.State);
        Assert.Equal(
            ["state:Playing", "started", "pos:5", "state:Paused", "paused@5", "state:Playing", "started", "state:Stopped", "ended"],
            [.. seen]);
        Assert.Equal(TimeSpan.FromSeconds(5), player.Position);
    }

    [Fact]
    public async Task AnEndedWebItem_LoopsWhenLoopMediaIsOn()
    {
        var (player, _) = CreatePlayer();
        using var owner = player;
        var web = new FakeWebPlayer();
        player.AttachWebMediaPlayer(web);
        Assert.True(await player.LoadAsync(Video));
        player.LoopMedia = true;
        var looped = 0;
        player.MediaLooped += (_, _) => Interlocked.Increment(ref looped);
        web.Commands.Clear();

        web.RaiseState(WebMediaState.Ended, 90);
        await player.FlushControlOperationsAsync();

        Assert.Equal(1, Volatile.Read(ref looped));
        Assert.Equal(["seek:0", "play"], web.Commands.ToArray());
    }

    [Fact]
    public async Task AWebError_RaisesMediaErrorAndEndsTheItem()
    {
        var (player, _) = CreatePlayer();
        using var owner = player;
        var web = new FakeWebPlayer();
        player.AttachWebMediaPlayer(web);
        var errors = new ConcurrentQueue<MediaErrorEventArgs>();
        var closed = 0;
        player.MediaError += (_, e) => errors.Enqueue(e);
        player.MediaClosed += (_, _) => Interlocked.Increment(ref closed);
        Assert.True(await player.LoadAsync(Video));

        web.RaiseError(WebMediaErrorKind.EmbeddingNotAllowed, "not allowed", 150);
        await player.FlushControlOperationsAsync();

        Assert.Equal(MediaErrorCode.OpenFailed, Assert.Single(errors).Code);
        Assert.Equal(1, Volatile.Read(ref closed));
        Assert.False(player.IsOpen);
        Assert.False(player.IsWebMedia);
    }

    [Fact]
    public async Task TheVolumeTheHostSets_AppliesToTheWebItem()
    {
        var (player, _) = CreatePlayer();
        using var owner = player;
        var web = new FakeWebPlayer();
        player.AttachWebMediaPlayer(web);
        Assert.True(await player.LoadAsync(Video));
        web.Commands.Clear();

        player.Effects.Volume = 0.25;

        Assert.Contains("volume:0.25", web.Commands.ToArray());
    }

    [Fact]
    public async Task BackgroundAndAudioModes_RejectAWebItem()
    {
        foreach (var mode in new[] { MediaPlayerMode.Background, MediaPlayerMode.Audio })
        {
            var (player, _) = CreatePlayer(MediaPlayerConfiguration.Auto with { Mode = mode });
            using var owner = player;
            var web = new FakeWebPlayer();
            player.AttachWebMediaPlayer(web);
            var errors = new ConcurrentQueue<MediaErrorEventArgs>();
            player.MediaError += (_, e) => errors.Enqueue(e);

            Assert.False(await player.LoadAsync(Video));

            Assert.Equal(MediaErrorCode.UnsupportedFormat, Assert.Single(errors).Code);
            Assert.Equal(0, web.LoadCount);
        }
    }

    // ---- normal playback is unchanged ---------------------------------------------------------------------------

    [Fact]
    public async Task FilePaths_StillGoToTheEngine_EvenWithAWebPlayerAttached()
    {
        var (player, engine) = CreatePlayer();
        using var owner = player;
        var web = new FakeWebPlayer();
        player.AttachWebMediaPlayer(web);

        Assert.True(player.Load("song.mp3"));
        Assert.True(await player.LoadAsync("other.mp3"));

        Assert.Equal(["song.mp3", "other.mp3"], [.. engine.OpenedSources.Select(s => s.FilePath ?? string.Empty)]);
        Assert.All(engine.OpenedSources, s => Assert.Equal(MediaInputSourceKind.FilePath, s.Kind));
        Assert.Equal(0, web.LoadCount);
        Assert.False(player.IsWebMedia);
    }

    [Theory]
    [InlineData("https://example.com/clip.mp4")]
    [InlineData("http://radio.example.com/live")]
    [InlineData("https://notyoutube.com/watch?v=dQw4w9WgXcQ")]
    public async Task OtherUrlStrings_AreTreatedExactlyAsBefore(string address)
    {
        var (player, engine) = CreatePlayer();
        using var owner = player;
        player.AttachWebMediaPlayer(new FakeWebPlayer());

        _ = player.Load(address);
        await player.FlushControlOperationsAsync();

        var opened = Assert.Single(engine.OpenedSources);
        Assert.Equal(MediaInputSourceKind.FilePath, opened.Kind);
        Assert.Equal(address, opened.FilePath);
    }

    [Fact]
    public async Task ExplicitNetworkSources_AreUnchanged()
    {
        var (player, engine) = CreatePlayer();
        using var owner = player;
        player.AttachWebMediaPlayer(new FakeWebPlayer());

        Assert.True(player.Load(MediaInputSource.FromUri(new Uri("https://example.com/clip.mp4"))));
        await player.FlushControlOperationsAsync();

        Assert.Equal(MediaInputSourceKind.NetworkUri, Assert.Single(engine.OpenedSources).Kind);
    }

    [Fact]
    public async Task LoadingAFileAfterAWebItem_EndsTheWebItemAndPlaysTheFileInTheEngine()
    {
        var (player, engine) = CreatePlayer();
        using var owner = player;
        var web = new FakeWebPlayer();
        player.AttachWebMediaPlayer(web);
        Assert.True(await player.LoadAsync(Video));
        var closed = 0;
        player.MediaClosed += (_, _) => Interlocked.Increment(ref closed);

        Assert.True(player.Load("song.mp3"));
        Assert.True(player.Play());
        await player.FlushControlOperationsAsync();

        Assert.False(player.IsWebMedia);
        Assert.True(Volatile.Read(ref closed) >= 1);
        Assert.Contains("pause", web.Commands.ToArray());
        Assert.Equal(1, engine.PlayCount);
        Assert.Equal(MediaPlayerState.Playing, player.State);
    }

    [Fact]
    public async Task WithNoWebItem_ControlsAndStateComeFromTheEngine()
    {
        var (player, engine) = CreatePlayer();
        using var owner = player;
        player.AttachWebMediaPlayer(new FakeWebPlayer());
        engine.Duration = 200;

        Assert.True(player.Load("song.mp3"));
        Assert.True(player.Play());
        Assert.True(player.Seek(10));
        await player.FlushControlOperationsAsync();

        Assert.Equal(1, engine.PlayCount);
        Assert.Equal(TimeSpan.FromSeconds(200), player.Duration);
        Assert.Equal(MediaPlayerState.Playing, player.State);
        Assert.True(player.IsOpen);
    }

    [Fact]
    public async Task Detaching_EndsTheWebItem()
    {
        var (player, _) = CreatePlayer();
        using var owner = player;
        var web = new FakeWebPlayer();
        player.AttachWebMediaPlayer(web);
        Assert.True(await player.LoadAsync(Video));

        player.AttachWebMediaPlayer(null);

        Assert.False(player.IsWebMedia);
        Assert.False(player.IsOpen);
        Assert.Contains("pause", web.Commands.ToArray());
        Assert.False(web.HasSubscribers);
    }

    /// <summary>A web player that records commands and lets a test hold or release its completion.</summary>
    sealed class FakeWebPlayer : IWebMediaPlayer
    {
        public TaskCompletionSource? LoadGate { get; set; }

        public TaskCompletionSource? CommandGate { get; set; }

        public ConcurrentQueue<string> CommandQueue { get; } = new();

        public CommandList Commands => new(CommandQueue);

        public int LoadCount { get; private set; }

        public bool LastAutoplay { get; private set; }

        public bool HasSubscribers => StateChanged is not null || PositionChanged is not null || Error is not null;

        public WebMediaReference? Current { get; private set; }

        public WebMediaState State { get; private set; }

        public TimeSpan Position { get; set; }

        public TimeSpan Duration { get; set; }

        public double Volume => 1;

        public bool IsMuted => false;

        public double Rate => 1;

        public IReadOnlyList<double> AvailableRates { get; } = [1];

        public event EventHandler<WebMediaStateChangedEventArgs>? StateChanged;

        public event EventHandler<TimeSpan>? PositionChanged;

        public event EventHandler<WebMediaErrorEventArgs>? Error;

        public Task LoadAsync(WebMediaReference item, bool autoplay, CancellationToken cancellationToken = default)
        {
            LoadCount++;
            LastAutoplay = autoplay;
            Current = item;
            return LoadGate?.Task ?? Task.CompletedTask;
        }

        public Task PlayAsync() => Record("play");

        public Task PauseAsync() => Record("pause");

        public Task SeekAsync(TimeSpan position) => Record($"seek:{position.TotalSeconds}");

        public Task SetVolumeAsync(double volume) => Record($"volume:{volume}");

        public Task SetMutedAsync(bool muted) => Record($"mute:{muted}");

        public Task SetRateAsync(double rate) => Record($"rate:{rate}");

        public Task SignInAsync(CancellationToken cancellationToken = default) => Record("signin");

        public Task SignOutAsync(CancellationToken cancellationToken = default) => Record("signout");

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public void RaiseState(WebMediaState state, double seconds)
        {
            State = state;
            StateChanged?.Invoke(this, new WebMediaStateChangedEventArgs(state, TimeSpan.FromSeconds(seconds)));
        }

        public void RaisePosition(TimeSpan position) => PositionChanged?.Invoke(this, position);

        public void RaiseError(WebMediaErrorKind kind, string message, int code) =>
            Error?.Invoke(this, new WebMediaErrorEventArgs(kind, message, code));

        Task Record(string command)
        {
            CommandQueue.Enqueue(command);
            return CommandGate?.Task ?? Task.CompletedTask;
        }
    }

    /// <summary>A view over the recorded commands that a test can read or clear.</summary>
    readonly struct CommandList(ConcurrentQueue<string> queue)
    {
        public string[] ToArray() => [.. queue];

        public void Clear()
        {
            while (queue.TryDequeue(out _)) { }
        }
    }
}
