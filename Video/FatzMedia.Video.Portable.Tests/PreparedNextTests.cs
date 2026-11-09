using FatzMedia.Video.Controllers;
using FatzMedia.Video.Core.Abstractions;
using FatzMedia.Video.Core.Configuration;
using FatzMedia.Video.Core.Prepared;
using System.Collections.Concurrent;

namespace FatzMedia.Video.Tests;

public sealed class PreparedNextSlotTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "fbm-prepared-" + Guid.NewGuid().ToString("N"));

    public PreparedNextSlotTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { }
    }

    [Fact]
    public void Begin_MovesEmptyToAnalyzing()
    {
        using var slot = new PreparedNextSlot(_ => "id");
        var source = MediaInputSource.FromFile("next.mp3");

        _ = slot.Begin(source, CancellationToken.None, out PreparedNextChange change);

        Assert.Equal(PreparedNextState.Empty, change.Previous.State);
        Assert.Equal(PreparedNextState.Analyzing, change.Current.State);
        Assert.Same(source, slot.Snapshot.Source);
    }

    [Fact]
    public void TryComplete_CurrentGeneration_BecomesReadyWithAnalysis()
    {
        using var slot = new PreparedNextSlot(_ => "id");
        PreparedNextTicket ticket = slot.Begin(MediaInputSource.FromFile("next.mp3"), CancellationToken.None, out _);
        MediaAnalysisResult analysis = CreateAnalysis();

        Assert.True(slot.TryComplete(ticket.Generation, analysis, out PreparedNextChange change));

        Assert.Equal(PreparedNextState.Ready, change.Current.State);
        Assert.Same(analysis, slot.Snapshot.Analysis);
        Assert.True(slot.IsReady());
        Assert.True(slot.IsReady(ticket.Generation));
    }

    [Fact]
    public void Begin_ReplacesPreviousCandidateAndCancelsItsAnalysis()
    {
        using var slot = new PreparedNextSlot(_ => "id");
        PreparedNextTicket first = slot.Begin(MediaInputSource.FromFile("a.mp3"), CancellationToken.None, out _);
        PreparedNextTicket second = slot.Begin(MediaInputSource.FromFile("b.mp3"), CancellationToken.None, out _);

        Assert.True(first.Token.IsCancellationRequested);
        Assert.False(second.Token.IsCancellationRequested);
        Assert.False(slot.TryComplete(first.Generation, null, out _));
        Assert.Equal("b.mp3", slot.Snapshot.Source!.FilePath);
        Assert.Equal(PreparedNextState.Analyzing, slot.Snapshot.State);
    }

    [Fact]
    public void Clear_CancelsAnalysisAndRejectsLateCompletion()
    {
        using var slot = new PreparedNextSlot(_ => "id");
        PreparedNextTicket ticket = slot.Begin(MediaInputSource.FromFile("a.mp3"), CancellationToken.None, out _);

        Assert.True(slot.Clear(out PreparedNextChange change));

        Assert.Equal(PreparedNextState.Empty, change.Current.State);
        Assert.True(ticket.Token.IsCancellationRequested);
        Assert.False(slot.TryComplete(ticket.Generation, null, out _));
        Assert.Equal(PreparedNextInfo.Empty, slot.Snapshot);
        Assert.False(slot.Clear(out _));
    }

    [Fact]
    public void Begin_LinksCallerCancellation()
    {
        using var slot = new PreparedNextSlot(_ => "id");
        using var cts = new CancellationTokenSource();
        PreparedNextTicket ticket = slot.Begin(MediaInputSource.FromFile("a.mp3"), cts.Token, out _);

        cts.Cancel();

        Assert.True(ticket.Token.IsCancellationRequested);
        Assert.True(slot.TryFail(ticket.Generation, out PreparedNextChange change));
        Assert.Equal(PreparedNextState.Empty, change.Current.State);
    }

    [Fact]
    public void Transition_ClaimsReadyThenEmpties()
    {
        using var slot = new PreparedNextSlot(_ => "id");
        PreparedNextTicket ticket = slot.Begin(MediaInputSource.FromFile("a.mp3"), CancellationToken.None, out _);
        _ = slot.TryComplete(ticket.Generation, null, out _);

        Assert.True(slot.TryBeginTransition(null, out PreparedNextTransition? transition, out PreparedNextChange? claimed));
        Assert.Equal(PreparedNextState.Transitioning, claimed!.Value.Current.State);
        Assert.False(slot.TryBeginTransition(null, out _, out _));

        Assert.True(slot.TryCompleteTransition(transition!.Generation, out PreparedNextChange done));
        Assert.Equal(PreparedNextState.Empty, done.Current.State);
    }

    [Fact]
    public void Transition_WhileAnalyzing_IsRejected()
    {
        using var slot = new PreparedNextSlot(_ => "id");
        _ = slot.Begin(MediaInputSource.FromFile("a.mp3"), CancellationToken.None, out _);

        Assert.False(slot.TryBeginTransition(null, out PreparedNextTransition? transition, out PreparedNextChange? change));
        Assert.Null(transition);
        Assert.Null(change);
    }

    [Fact]
    public void Transition_ExpectedGenerationMismatch_IsRejected()
    {
        using var slot = new PreparedNextSlot(_ => "id");
        PreparedNextTicket ticket = slot.Begin(MediaInputSource.FromFile("a.mp3"), CancellationToken.None, out _);
        _ = slot.TryComplete(ticket.Generation, null, out _);

        Assert.False(slot.TryBeginTransition(ticket.Generation + 1, out _, out _));
        Assert.True(slot.IsReady());
    }

    [Fact]
    public void Transition_ChangedIdentity_BecomesStale()
    {
        var identity = "v1";
        using var slot = new PreparedNextSlot(_ => identity);
        PreparedNextTicket ticket = slot.Begin(MediaInputSource.FromFile("a.mp3"), CancellationToken.None, out _);
        _ = slot.TryComplete(ticket.Generation, null, out _);
        identity = "v2";

        Assert.False(slot.TryBeginTransition(null, out PreparedNextTransition? transition, out PreparedNextChange? change));

        Assert.Null(transition);
        Assert.Equal(PreparedNextState.Stale, change!.Value.Current.State);
        Assert.Equal(PreparedNextState.Stale, slot.Snapshot.State);
        Assert.True(slot.Clear(out _));
    }

    [Fact]
    public void ReplacementDuringTransition_SurvivesTransitionCompletion()
    {
        using var slot = new PreparedNextSlot(_ => "id");
        PreparedNextTicket ticket = slot.Begin(MediaInputSource.FromFile("a.mp3"), CancellationToken.None, out _);
        _ = slot.TryComplete(ticket.Generation, null, out _);
        _ = slot.TryBeginTransition(null, out PreparedNextTransition? transition, out _);

        _ = slot.Begin(MediaInputSource.FromFile("b.mp3"), CancellationToken.None, out _);

        Assert.False(slot.TryCompleteTransition(transition!.Generation, out _));
        Assert.Equal("b.mp3", slot.Snapshot.Source!.FilePath);
        Assert.Equal(PreparedNextState.Analyzing, slot.Snapshot.State);
    }

    [Fact]
    public void Dispose_CancelsAndRejectsFurtherWork()
    {
        var slot = new PreparedNextSlot(_ => "id");
        PreparedNextTicket ticket = slot.Begin(MediaInputSource.FromFile("a.mp3"), CancellationToken.None, out _);

        slot.Dispose();

        Assert.True(ticket.Token.IsCancellationRequested);
        Assert.False(slot.TryComplete(ticket.Generation, null, out _));
        Assert.Throws<ObjectDisposedException>(() => slot.Begin(MediaInputSource.FromFile("b.mp3"), CancellationToken.None, out _));
    }

    [Fact]
    public void CreateIdentity_TracksFileLengthChanges()
    {
        var path = Path.Combine(_directory, "song.mp3");
        File.WriteAllBytes(path, [1, 2, 3]);
        var source = MediaInputSource.FromFile(path);
        var before = PreparedNextSlot.CreateIdentity(source);

        File.WriteAllBytes(path, [1, 2, 3, 4]);

        Assert.NotNull(before);
        Assert.NotEqual(before, PreparedNextSlot.CreateIdentity(source));
    }

    [Fact]
    public void CreateIdentity_NonFileSource_IsNull() =>
        Assert.Null(PreparedNextSlot.CreateIdentity(MediaInputSource.FromBgra32Image(new byte[4], 1, 1, 4)));

    internal static MediaAnalysisResult CreateAnalysis() =>
        new(10, 1, 9, 8, [new SilenceRegion(0, 1.1), new SilenceRegion(8.9, 10)], 1.5);
}

public sealed class PreparedNextPlayerTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "fbm-prepared-player-" + Guid.NewGuid().ToString("N"));

    public PreparedNextPlayerTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { }
    }

    [Fact]
    public async Task PrepareNext_AnalyzesWithoutTouchingCurrentMedia()
    {
        var engine = new TestMediaEngine();
        var analyzer = new FakeAnalyzer();
        using var player = CreatePlayer(engine, analyzer);
        Assert.True(player.Load("current.mp3"));
        Assert.True(player.Play());
        var next = CreateFile("next.mp3");

        Assert.True(await player.PrepareNextAsync(next).WaitAsync(Timeout));

        Assert.Equal(PreparedNextState.Ready, player.PreparedNext.State);
        Assert.Same(analyzer.Result, player.PreparedNext.Analysis);
        Assert.Equal([next], analyzer.Requests);
        Assert.Equal(MediaPlayerState.Playing, engine.State);
        Assert.Equal(1, engine.PlayCount);
    }

    [Theory]
    [InlineData("karaoke.zip")]
    [InlineData("lyrics.cdg")]
    [InlineData("cover.png")]
    public async Task PrepareNext_NonAudioFile_IsReadyWithoutRunningAnalysis(string name)
    {
        var analyzer = new FakeAnalyzer();
        using var player = CreatePlayer(new TestMediaEngine(), analyzer);

        Assert.True(await player.PrepareNextAsync(CreateFile(name)).WaitAsync(Timeout));

        Assert.Equal(PreparedNextState.Ready, player.PreparedNext.State);
        Assert.Null(player.PreparedNext.Analysis);
        Assert.Empty(analyzer.Requests);
    }

    [Fact]
    public async Task PrepareNext_MissingFile_ReturnsFalseAndStaysEmpty()
    {
        using var player = CreatePlayer(new TestMediaEngine(), new FakeAnalyzer());

        Assert.False(await player.PrepareNextAsync(Path.Combine(_directory, "missing.mp3")).WaitAsync(Timeout));
        Assert.Equal(PreparedNextState.Empty, player.PreparedNext.State);
    }

    [Fact]
    public async Task PrepareNext_ReplacingCandidate_CancelsFirstAndKeepsDepthOne()
    {
        var analyzer = new FakeAnalyzer { Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var player = CreatePlayer(new TestMediaEngine(), analyzer);
        var first = CreateFile("first.mp3");
        var second = CreateFile("second.mp3");

        Task<bool> firstPrepare = player.PrepareNextAsync(first);
        await analyzer.Started.Task.WaitAsync(Timeout);
        analyzer.Gate = null;
        Task<bool> secondPrepare = player.PrepareNextAsync(second);

        Assert.False(await firstPrepare.WaitAsync(Timeout));
        Assert.True(await secondPrepare.WaitAsync(Timeout));
        Assert.Equal(second, player.PreparedNext.Source!.FilePath);
        Assert.Equal(PreparedNextState.Ready, player.PreparedNext.State);
    }

    [Fact]
    public async Task ClearPreparedNext_DuringAnalysis_CancelsAndEmpties()
    {
        var analyzer = new FakeAnalyzer { Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var player = CreatePlayer(new TestMediaEngine(), analyzer);
        Task<bool> prepare = player.PrepareNextAsync(CreateFile("next.mp3"));
        await analyzer.Started.Task.WaitAsync(Timeout);

        Assert.True(player.ClearPreparedNext());

        Assert.False(await prepare.WaitAsync(Timeout));
        Assert.Equal(PreparedNextState.Empty, player.PreparedNext.State);
        Assert.False(player.ClearPreparedNext());
    }

    [Fact]
    public async Task AnalysisFailure_StillPreparesWithoutAnalysis()
    {
        var analyzer = new FakeAnalyzer { Failure = new InvalidOperationException("bad decode") };
        using var player = CreatePlayer(new TestMediaEngine(), analyzer);

        Assert.True(await player.PrepareNextAsync(CreateFile("next.mp3")).WaitAsync(Timeout));

        Assert.Equal(PreparedNextState.Ready, player.PreparedNext.State);
        Assert.Null(player.PreparedNext.Analysis);
    }

    [Fact]
    public async Task TransitionNow_LoadsAndPlaysPreparedItem()
    {
        var engine = new TestMediaEngine();
        using var player = CreatePlayer(engine, new FakeAnalyzer());
        var next = CreateFile("next.mp3");
        Assert.True(await player.PrepareNextAsync(next).WaitAsync(Timeout));
        var transitioned = new TaskCompletionSource<PreparedNextTransitionedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        player.PreparedNextTransitioned += (_, e) => transitioned.TrySetResult(e);

        Assert.True(await player.TransitionToPreparedNextAsync().WaitAsync(Timeout));

        PreparedNextTransitionedEventArgs args = await transitioned.Task.WaitAsync(Timeout);
        Assert.False(args.IsAutomatic);
        Assert.Equal(next, args.Source.FilePath);
        Assert.Equal(MediaPlayerState.Playing, engine.State);
        Assert.Equal(PreparedNextState.Empty, player.PreparedNext.State);
    }

    [Fact]
    public async Task Scrub_ReleaseSeekAtTheDragPosition_CompletesWithoutMoving()
    {
        // Stage 035A facade: the release seek of a drag at the last drag position is ignored.
        var engine = new TestMediaEngine();
        using var player = CreatePlayer(engine, new FakeAnalyzer());
        Assert.True(player.Load(CreateFile("first.mp3")));
        Assert.True(player.Play());
        Assert.True(player.BeginScrub());
        Assert.True(player.Controls.Seek(30));
        engine.Position = 32;
        var completed = new TaskCompletionSource<MediaSeekCompletedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        player.SeekCompleted += (_, e) => completed.TrySetResult(e);

        Assert.True(player.Seek(30));
        Assert.True(player.EndScrub());

        Assert.Equal(32, engine.Position);
        _ = await completed.Task.WaitAsync(Timeout);
    }

    [Fact]
    public async Task Scrub_LoadingMidDrag_EndsTheScrub()
    {
        var engine = new TestMediaEngine();
        using var player = CreatePlayer(engine, new FakeAnalyzer());
        Assert.True(player.Load(CreateFile("first.mp3")));
        Assert.True(player.BeginScrub());
        Assert.True(player.Controls.Seek(30));

        Assert.True(await player.LoadAsync(CreateFile("second.mp3")).WaitAsync(Timeout));

        Assert.False(player.Controls.IsScrubbing);
        Assert.True(player.Seek(30));
        Assert.Equal(30, engine.Position);
    }

    [Fact]
    public async Task TransitionNow_EmptySlot_ReturnsFalse()
    {
        var engine = new TestMediaEngine();
        using var player = CreatePlayer(engine, new FakeAnalyzer());

        Assert.False(await player.TransitionToPreparedNextAsync().WaitAsync(Timeout));
        Assert.False(engine.IsOpen);
    }

    [Fact]
    public async Task TransitionNow_LoadFailure_EmptiesSlot()
    {
        var engine = new TestMediaEngine { OpenSucceeds = false };
        using var player = CreatePlayer(engine, new FakeAnalyzer());
        Assert.True(await player.PrepareNextAsync(CreateFile("next.mp3")).WaitAsync(Timeout));

        Assert.False(await player.TransitionToPreparedNextAsync().WaitAsync(Timeout));
        Assert.Equal(PreparedNextState.Empty, player.PreparedNext.State);
    }

    [Fact]
    public async Task TransitionNow_ChangedFile_BecomesStaleWithoutLoading()
    {
        var engine = new TestMediaEngine();
        using var player = CreatePlayer(engine, new FakeAnalyzer());
        var next = CreateFile("next.mp3");
        Assert.True(await player.PrepareNextAsync(next).WaitAsync(Timeout));
        File.WriteAllBytes(next, [9, 9, 9, 9, 9, 9]);

        Assert.False(await player.TransitionToPreparedNextAsync().WaitAsync(Timeout));

        Assert.Equal(PreparedNextState.Stale, player.PreparedNext.State);
        Assert.False(engine.IsOpen);
    }

    [Fact]
    public async Task NaturalEnd_WithAutoTransition_StartsPreparedItemAfterMediaEnded()
    {
        var engine = new TestMediaEngine();
        using var player = CreatePlayer(engine, new FakeAnalyzer());
        Assert.True(player.Load("current.mp3"));
        Assert.True(player.Play());
        Assert.True(await player.PrepareNextAsync(CreateFile("next.mp3")).WaitAsync(Timeout));
        player.AutoTransitionToPreparedNext = true;
        var order = new ConcurrentQueue<string>();
        var transitioned = new TaskCompletionSource<PreparedNextTransitionedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        player.MediaEnded += (_, _) => order.Enqueue("ended");
        player.MediaLoaded += (_, _) => order.Enqueue("loaded");
        player.PreparedNextTransitioned += (_, e) =>
        {
            order.Enqueue("transitioned");
            transitioned.TrySetResult(e);
        };

        engine.CompletePlayback();

        PreparedNextTransitionedEventArgs args = await transitioned.Task.WaitAsync(Timeout);
        Assert.True(args.IsAutomatic);
        Assert.Equal(["ended", "loaded", "transitioned"], order);
        Assert.Equal(2, engine.PlayCount);
        Assert.Equal(MediaPlayerState.Playing, engine.State);
    }

    [Fact]
    public async Task NaturalEnd_AutoTransitionDisabled_LeavesSlotReady()
    {
        var engine = new TestMediaEngine();
        using var player = CreatePlayer(engine, new FakeAnalyzer());
        Assert.True(player.Load("current.mp3"));
        Assert.True(player.Play());
        Assert.True(await player.PrepareNextAsync(CreateFile("next.mp3")).WaitAsync(Timeout));
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        player.MediaEnded += (_, _) => ended.TrySetResult();

        engine.CompletePlayback();
        await ended.Task.WaitAsync(Timeout);
        await DrainQueueAsync(player);

        Assert.Equal(PreparedNextState.Ready, player.PreparedNext.State);
        Assert.Equal(1, engine.PlayCount);
    }

    [Fact]
    public async Task NaturalEnd_HostStopInMediaEnded_CancelsAutoTransition()
    {
        var engine = new TestMediaEngine();
        using var player = CreatePlayer(engine, new FakeAnalyzer());
        Assert.True(player.Load("current.mp3"));
        Assert.True(player.Play());
        Assert.True(await player.PrepareNextAsync(CreateFile("next.mp3")).WaitAsync(Timeout));
        player.AutoTransitionToPreparedNext = true;
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        player.MediaEnded += (_, _) =>
        {
            _ = player.Stop();
            ended.TrySetResult();
        };

        engine.RaiseMediaEnded();
        await ended.Task.WaitAsync(Timeout);
        await DrainQueueAsync(player);

        Assert.Equal(PreparedNextState.Ready, player.PreparedNext.State);
        Assert.Equal(1, engine.PlayCount);
    }

    [Fact]
    public async Task NaturalEnd_LoopMediaEnabled_DoesNotAutoTransition()
    {
        var engine = new TestMediaEngine { HandlesLoopInternally = true };
        using var player = CreatePlayer(engine, new FakeAnalyzer());
        Assert.True(player.Load("current.mp3"));
        Assert.True(player.Play());
        Assert.True(await player.PrepareNextAsync(CreateFile("next.mp3")).WaitAsync(Timeout));
        player.AutoTransitionToPreparedNext = true;
        player.LoopMedia = true;
        var looped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        player.MediaLooped += (_, _) => looped.TrySetResult();

        engine.RaiseMediaEnded();
        await looped.Task.WaitAsync(Timeout);
        await DrainQueueAsync(player);

        Assert.Equal(PreparedNextState.Ready, player.PreparedNext.State);
    }

    [Fact]
    public async Task PreparedNextChanged_ReportsLifecycleInOrder()
    {
        using var player = CreatePlayer(new TestMediaEngine(), new FakeAnalyzer());
        var states = new ConcurrentQueue<PreparedNextState>();
        var emptied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        player.PreparedNextChanged += (_, e) =>
        {
            states.Enqueue(e.Current.State);
            if (e.Current.State == PreparedNextState.Empty)
                emptied.TrySetResult();
        };

        Assert.True(await player.PrepareNextAsync(CreateFile("next.mp3")).WaitAsync(Timeout));
        Assert.True(await player.TransitionToPreparedNextAsync().WaitAsync(Timeout));
        await emptied.Task.WaitAsync(Timeout);

        Assert.Equal(
            [PreparedNextState.Analyzing, PreparedNextState.Ready, PreparedNextState.Transitioning, PreparedNextState.Empty],
            states);
    }

    [Fact]
    public async Task AnalyzeAsync_DoesNotPrepare()
    {
        var analyzer = new FakeAnalyzer();
        using var player = CreatePlayer(new TestMediaEngine(), analyzer);

        MediaAnalysisResult? result = await player.AnalyzeAsync(CreateFile("next.mp3")).WaitAsync(Timeout);

        Assert.Same(analyzer.Result, result);
        Assert.Equal(PreparedNextState.Empty, player.PreparedNext.State);
    }

    [Fact]
    public async Task AnalyzeAsync_NonFileSource_ReturnsNull()
    {
        var analyzer = new FakeAnalyzer();
        using var player = CreatePlayer(new TestMediaEngine(), analyzer);

        Assert.Null(await player.AnalyzeAsync(MediaInputSource.FromBgra32Image(new byte[4], 1, 1, 4)));
        Assert.Empty(analyzer.Requests);
    }

    [Fact]
    public async Task Reset_ClearsPreparedItem()
    {
        var factory = new PreparedTestPlatformFactory(new FakeAnalyzer(), new TestMediaEngine(), new TestMediaEngine());
        using var player = new PlayerCore(factory, MediaPlayerConfiguration.Auto, []);
        Assert.True(await player.PrepareNextAsync(CreateFile("next.mp3")).WaitAsync(Timeout));

        Assert.True(player.Reset(MediaPlayerConfiguration.Auto, []));

        Assert.Equal(PreparedNextState.Empty, player.PreparedNext.State);
    }

    private static async Task DrainQueueAsync(PlayerCore player)
    {
        // The auto transition is scheduled two queue hops after media end; each flush queues
        // behind everything queued so far.
        for (var i = 0; i < 4; i++)
            await player.FlushControlOperationsAsync().WaitAsync(Timeout);
    }

    private string CreateFile(string name)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllBytes(path, [1, 2, 3]);
        return path;
    }

    private static PlayerCore CreatePlayer(TestMediaEngine engine, FakeAnalyzer analyzer) =>
        new(new PreparedTestPlatformFactory(analyzer, engine), MediaPlayerConfiguration.Auto, []);

    private sealed class FakeAnalyzer : IMediaFileAnalyzer
    {
        public MediaAnalysisResult Result { get; } = PreparedNextSlotTests.CreateAnalysis();
        public ConcurrentQueue<string> Requests { get; } = new();
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource? Gate { get; set; }
        public Exception? Failure { get; init; }

        public async Task<MediaAnalysisResult?> AnalyzeAsync(string filePath, CancellationToken cancellationToken)
        {
            Requests.Enqueue(filePath);
            TaskCompletionSource? gate = Gate;
            _ = Started.TrySetResult();
            if (gate is not null)
                await gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            return Failure is null ? Result : throw Failure;
        }
    }

    private sealed class PreparedTestPlatformFactory(FakeAnalyzer analyzer, params TestMediaEngine[] engines) : IPlatformFactory
    {
        private readonly Queue<TestMediaEngine> _engines = new(engines);

        public string LastNativeLoadMessage => string.Empty;

        public VideoDecodeCapabilities GetVideoDecodeCapabilities() => VideoDecodeCapabilities.Create([], []);

        public IMediaPresentationCoordinator CreatePresentationCoordinator(
            IMediaEngine mediaEngine,
            MediaPlayerConfiguration configuration) => new NullPresentationCoordinator();

        public IServiceProvider CreateServiceProvider(
            MediaPlayerConfiguration configuration,
            IReadOnlyList<string> nativeLibraryPaths) => new ServiceProvider(_engines.Dequeue(), analyzer);
    }

    private sealed class ServiceProvider(TestMediaEngine engine, FakeAnalyzer analyzer) : IServiceProvider
    {
        private readonly IArchiveService _archiveService = new NotSupportedArchiveService();

        public object? GetService(Type serviceType) =>
            serviceType == typeof(IMediaEngine) ? engine
            : serviceType == typeof(IArchiveService) ? _archiveService
            : serviceType == typeof(IMediaFileAnalyzer) ? analyzer
            : null;
    }
}
