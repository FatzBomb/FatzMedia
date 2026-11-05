using FatzBomb.MediaControl.Player.Shared.Hosting;
using FatzBomb.MediaControl.Player.Shared.Presentation;

namespace FatzBomb.MediaControl.Tests;

/// <summary>ENG-039 R1: the players of a test-player window (rebuild, full screen, keys, stats, shutdown).</summary>
/// <remarks>
/// Each test starts with <see cref="Inline"/>, so a fake player's gate continues the controller
/// inline on the test thread (as the host's UI thread would, one step at a time).
/// </remarks>
public sealed class PlayerSetControllerTests
{
    private static readonly ProcessSample Sample = new(12, 0.5, 40, 120);

    [Fact]
    public void Start_BuildsTheRequestedPlayersAndInitializesThemInOrder()
    {
        Inline();
        var host = new FakeHost { RequestedPlayerCount = 3 };
        PlayerSetController<FakeSlot> controller = host.CreateController();

        Task build = controller.StartAsync();

        Assert.True(build.IsCompletedSuccessfully);
        Assert.Equal(["P1", "P2", "P3"], controller.Slots.Select(static slot => slot.Name));
        Assert.All(controller.Slots, static slot => Assert.True(slot.Initialized));
        Assert.Equal(
            ["create P1", "attach P1", "create P2", "attach P2", "create P3", "attach P3", "layout P1,P2,P3", "init P1", "init P2", "init P3"],
            host.Log);
    }

    [Fact]
    public void RendererDeviceLost_AsksTheWindowToFallBackOncePerBuild()
    {
        // ENG-050: every player shares the window's renderer, so the first loss asks once; the
        // rebuild that follows may ask again.
        Inline();
        var host = new FakeHost { RequestedPlayerCount = 2 };
        PlayerSetController<FakeSlot> controller = host.CreateController();
        Assert.True(controller.StartAsync().IsCompletedSuccessfully);

        controller.Slots[0].RaiseRendererDeviceLost();
        controller.Slots[1].RaiseRendererDeviceLost();
        Assert.Equal(1, host.FallBackCount);

        Assert.True(controller.RebuildAsync().IsCompletedSuccessfully);
        controller.Slots[0].RaiseRendererDeviceLost();
        Assert.Equal(2, host.FallBackCount);
    }

    [Fact]
    public void Rebuild_BeforeStart_DoesNothing()
    {
        Inline();
        var host = new FakeHost();
        PlayerSetController<FakeSlot> controller = host.CreateController();

        Assert.True(controller.RebuildAsync().IsCompletedSuccessfully);
        Assert.Empty(host.Created);
    }

    [Fact]
    public void Rebuild_RequestsDuringARebuildCoalesceIntoOneMoreBuild()
    {
        Inline();
        var host = new FakeHost();
        var gate = new TaskCompletionSource();
        host.InitGates.Enqueue(gate.Task);
        PlayerSetController<FakeSlot> controller = host.CreateController();

        Task first = controller.StartAsync();
        Task second = controller.RebuildAsync();
        Task third = controller.RebuildAsync();
        host.RequestedPlayerCount = 2;
        Task fourth = controller.RebuildAsync();

        Assert.Same(first, second);
        Assert.Same(first, third);
        Assert.Same(first, fourth);
        Assert.False(first.IsCompleted);

        gate.SetResult();

        Assert.True(first.IsCompletedSuccessfully);
        // The first build, then one more with the latest count: not one per request.
        Assert.Equal(3, host.Created.Count);
        Assert.Equal(["P1", "P2"], controller.Slots.Select(static slot => slot.Name));
        Assert.True(host.Created[0].Disposed);
        Assert.NotSame(first, controller.RebuildAsync());
    }

    [Fact]
    public void Rebuild_DetachesAndDisposesTheOldPlayersBeforeCreatingTheNew()
    {
        Inline();
        var host = new FakeHost { RequestedPlayerCount = 2 };
        PlayerSetController<FakeSlot> controller = host.CreateController();
        _ = controller.StartAsync();
        host.Log.Clear();
        host.RequestedPlayerCount = 1;

        Assert.True(controller.RebuildAsync().IsCompletedSuccessfully);

        Assert.Equal(
            ["detach P1", "detach P2", "dispose P1", "dispose P2", "create P1", "attach P1", "layout P1", "init P1"],
            host.Log);
        Assert.Same(host.Created[2], Assert.Single(controller.Slots));
    }

    [Fact]
    public void Rebuild_AnOldPlayerStillDisposingWhenAnotherRequestComes_RunsOneMoreBuild()
    {
        Inline();
        var host = new FakeHost();
        PlayerSetController<FakeSlot> controller = host.CreateController();
        _ = controller.StartAsync();
        var disposing = new TaskCompletionSource();
        host.Created[0].DisposeGate = disposing.Task;

        Task rebuild = controller.RebuildAsync();
        Assert.Same(rebuild, controller.RebuildAsync());
        disposing.SetResult();

        Assert.True(rebuild.IsCompletedSuccessfully);
        Assert.Equal(3, host.Created.Count);
        Assert.True(host.Created[1].Disposed);
        Assert.False(host.Created[1].Initialized);
        Assert.Same(host.Created[2], Assert.Single(controller.Slots));
        Assert.True(host.Created[2].Initialized);
    }

    [Fact]
    public void Rebuild_TheFirstBuildIsAtStartup_EvenWhenARequestSupersedesIt()
    {
        Inline();
        var host = new FakeHost { RequestedPlayerCount = 2 };
        var gate = new TaskCompletionSource();
        host.InitGates.Enqueue(gate.Task);
        PlayerSetController<FakeSlot> controller = host.CreateController();

        _ = controller.StartAsync();
        Task rebuild = controller.RebuildAsync();
        gate.SetResult();

        Assert.True(rebuild.IsCompletedSuccessfully);
        Assert.Equal([true, true, false, false], host.Created.Select(static slot => slot.AtStartup));
        // The superseded build's second player was never started.
        Assert.False(host.Created[1].Initialized);
        Assert.True(host.Created[1].Disposed);
    }

    [Fact]
    public void Rebuild_AFailureIsReportedAndTheNextRebuildReplacesThePlayers()
    {
        Inline();
        var host = new FakeHost { RequestedPlayerCount = 2, FailCreatingIndex = 2 };
        PlayerSetController<FakeSlot> controller = host.CreateController();

        Task build = controller.StartAsync();

        Assert.True(build.IsCompletedSuccessfully);
        Assert.Equal("Rebuilding the players failed", Assert.Single(host.Failures).What);
        FakeSlot partial = Assert.Single(controller.Slots);

        host.FailCreatingIndex = null;
        Assert.True(controller.RebuildAsync().IsCompletedSuccessfully);

        Assert.True(partial.Disposed);
        Assert.Equal(2, controller.Slots.Count);
        Assert.All(controller.Slots, static slot => Assert.True(slot.Initialized));
    }

    [Fact]
    public void Rebuild_AFailedInitializationIsReportedAndDoesNotFaultTheRebuild()
    {
        Inline();
        var host = new FakeHost();
        host.InitGates.Enqueue(Task.FromException(new InvalidOperationException("no engine")));
        PlayerSetController<FakeSlot> controller = host.CreateController();

        Task build = controller.StartAsync();

        Assert.True(build.IsCompletedSuccessfully);
        Assert.IsType<InvalidOperationException>(Assert.Single(host.Failures).Exception);
    }

    [Fact]
    public void Rebuild_APlayerThatFailsToDispose_IsReportedAndTheOthersAreStillReplaced()
    {
        Inline();
        var host = new FakeHost { RequestedPlayerCount = 2 };
        PlayerSetController<FakeSlot> controller = host.CreateController();
        _ = controller.StartAsync();
        host.Created[0].DisposeFailure = new InvalidOperationException("stuck");

        Assert.True(controller.RebuildAsync().IsCompletedSuccessfully);

        Assert.Equal("Closing a player failed", Assert.Single(host.Failures).What);
        Assert.True(host.Created[1].Disposed);
        Assert.Equal(4, host.Created.Count);
        Assert.Equal(2, controller.Slots.Count);
    }

    [Fact]
    public async Task Shutdown_DuringAPlayersInitialization_WaitsForTheRebuildAndDisposesEveryPlayer()
    {
        Inline();
        // The B6 regression: closing while a toolbar rebuild starts its players.
        var host = new FakeHost { RequestedPlayerCount = 2 };
        var gate = new TaskCompletionSource();
        PlayerSetController<FakeSlot> controller = host.CreateController();
        _ = controller.StartAsync();
        host.InitGates.Enqueue(gate.Task);
        _ = controller.RebuildAsync();

        Task<bool> shutdown = controller.ShutdownAsync();
        Task<bool> secondClose = controller.ShutdownAsync();

        Assert.False(shutdown.IsCompleted);
        Assert.True(secondClose.IsCompletedSuccessfully);
        Assert.False(await secondClose);
        Assert.True(controller.IsShuttingDown);
        Assert.False(controller.IsShutDown);

        gate.SetResult();

        Assert.True(await shutdown);
        Assert.True(controller.IsShutDown);
        Assert.Equal(4, host.Created.Count);
        Assert.All(host.Created, static slot => Assert.True(slot.Disposed));
        // The rebuild stopped at the shutdown: the second new player was never started.
        Assert.False(host.Created[3].Initialized);
        Assert.Empty(controller.Slots);
        Assert.True(await controller.ShutdownAsync());
    }

    [Fact]
    public async Task Shutdown_WhileTheOldPlayersAreDisposing_BuildsNothingNew()
    {
        Inline();
        var host = new FakeHost();
        PlayerSetController<FakeSlot> controller = host.CreateController();
        _ = controller.StartAsync();
        var disposing = new TaskCompletionSource();
        host.Created[0].DisposeGate = disposing.Task;
        _ = controller.RebuildAsync();

        Task<bool> shutdown = controller.ShutdownAsync();
        disposing.SetResult();

        Assert.True(await shutdown);
        FakeSlot only = Assert.Single(host.Created);
        Assert.True(only.Disposed);
        Assert.True(controller.RebuildAsync().IsCompletedSuccessfully);
        Assert.Single(host.Created);
    }

    [Fact]
    public async Task Shutdown_APlayerThatFailsToDispose_StillDisposesTheOthersAndLetsTheWindowClose()
    {
        Inline();
        var host = new FakeHost { RequestedPlayerCount = 3 };
        PlayerSetController<FakeSlot> controller = host.CreateController();
        _ = controller.StartAsync();
        host.Created[1].DisposeFailure = new InvalidOperationException("stuck");

        Assert.True(await controller.ShutdownAsync());

        Assert.Equal("Closing a player failed", Assert.Single(host.Failures).What);
        Assert.True(host.Created[0].Disposed);
        Assert.True(host.Created[2].Disposed);
        Assert.True(controller.IsShutDown);
    }

    [Fact]
    public async Task Shutdown_LeavesFullScreenFirst()
    {
        Inline();
        var host = new FakeHost { RequestedPlayerCount = 2 };
        PlayerSetController<FakeSlot> controller = host.CreateController();
        _ = controller.StartAsync();
        controller.EnterFullScreen(controller.Slots[1]);
        host.Log.Clear();

        Assert.True(await controller.ShutdownAsync());

        Assert.Equal(["fullscreen P2 False", "window False", "layout P1,P2", "dispose P1", "dispose P2"], host.Log);
    }

    [Fact]
    public void FullScreen_EntersLayoutWindowPlayerAndLeavesInReverse()
    {
        Inline();
        var host = new FakeHost { RequestedPlayerCount = 2 };
        PlayerSetController<FakeSlot> controller = host.CreateController();
        _ = controller.StartAsync();
        host.Log.Clear();

        controller.Slots[1].RequestFullScreen();

        Assert.Same(controller.Slots[1], controller.FullScreenSlot);
        Assert.Equal(["layout P1,P2 full P2", "window True", "fullscreen P2 True"], host.Log);

        host.Log.Clear();
        controller.Slots[1].RequestFullScreen();

        Assert.Null(controller.FullScreenSlot);
        Assert.Equal(["fullscreen P2 False", "window False", "layout P1,P2"], host.Log);
    }

    [Fact]
    public void FullScreen_AnotherPlayerLeavesTheFirstOnesFullScreenFirst()
    {
        Inline();
        var host = new FakeHost { RequestedPlayerCount = 2 };
        PlayerSetController<FakeSlot> controller = host.CreateController();
        _ = controller.StartAsync();
        controller.Slots[0].RequestFullScreen();
        host.Log.Clear();

        controller.Slots[1].RequestFullScreen();

        Assert.Same(controller.Slots[1], controller.FullScreenSlot);
        Assert.Equal(
            ["fullscreen P1 False", "window False", "layout P1,P2", "layout P1,P2 full P2", "window True", "fullscreen P2 True"],
            host.Log);
    }

    [Fact]
    public void FullScreen_ARebuildLeavesItAndOldPlayersNoLongerAsk()
    {
        Inline();
        var host = new FakeHost();
        PlayerSetController<FakeSlot> controller = host.CreateController();
        _ = controller.StartAsync();
        FakeSlot old = controller.Slots[0];
        old.RequestFullScreen();

        _ = controller.RebuildAsync();
        old.RequestFullScreen();

        Assert.Null(controller.FullScreenSlot);
        Assert.False(old.IsFullScreen);
        Assert.Equal(1, host.WindowFullScreenCount);
    }

    [Fact]
    public void F11_WithoutPlayersIsNotUsed()
    {
        Inline();
        var host = new FakeHost();
        PlayerSetController<FakeSlot> controller = host.CreateController();

        Assert.False(controller.HandleFullScreenKey());
    }

    [Fact]
    public void F11_ShowsTheFocusedPlayerElsePlayerOne_AndLeavesFullScreen()
    {
        Inline();
        var host = new FakeHost { RequestedPlayerCount = 3 };
        PlayerSetController<FakeSlot> controller = host.CreateController();
        _ = controller.StartAsync();

        Assert.True(controller.HandleFullScreenKey());
        Assert.Same(controller.Slots[0], controller.FullScreenSlot);
        Assert.True(controller.HandleFullScreenKey());
        Assert.Null(controller.FullScreenSlot);

        controller.Slots[2].IsKeyboardFocusWithin = true;
        Assert.True(controller.HandleFullScreenKey());
        Assert.Same(controller.Slots[2], controller.FullScreenSlot);
    }

    [Fact]
    public void Escape_InFullScreen_ClosesThatPlayersFlyoutThenLeavesFullScreen()
    {
        Inline();
        var host = new FakeHost { RequestedPlayerCount = 2 };
        PlayerSetController<FakeSlot> controller = host.CreateController();
        _ = controller.StartAsync();
        controller.Slots[0].HasOpenFlyout = true;
        controller.Slots[1].HasOpenFlyout = true;
        controller.EnterFullScreen(controller.Slots[1]);

        Assert.True(controller.HandleEscapeKey());
        Assert.False(controller.Slots[1].HasOpenFlyout);
        Assert.True(controller.Slots[0].HasOpenFlyout);
        Assert.Same(controller.Slots[1], controller.FullScreenSlot);

        Assert.True(controller.HandleEscapeKey());
        Assert.Null(controller.FullScreenSlot);
        Assert.True(controller.Slots[0].HasOpenFlyout);
    }

    [Fact]
    public void Escape_InTheWindow_ClosesTheFocusedPlayersFlyoutElseAnyOpenOne()
    {
        Inline();
        var host = new FakeHost { RequestedPlayerCount = 3 };
        PlayerSetController<FakeSlot> controller = host.CreateController();
        _ = controller.StartAsync();
        controller.Slots[0].HasOpenFlyout = true;
        controller.Slots[2].HasOpenFlyout = true;
        controller.Slots[2].IsKeyboardFocusWithin = true;

        Assert.True(controller.HandleEscapeKey());
        Assert.False(controller.Slots[2].HasOpenFlyout);
        Assert.True(controller.Slots[0].HasOpenFlyout);

        Assert.True(controller.HandleEscapeKey());
        Assert.False(controller.Slots[0].HasOpenFlyout);

        Assert.False(controller.HandleEscapeKey());
        Assert.Equal(0, host.WindowFullScreenCount);
    }

    [Fact]
    public async Task Stats_TickFeedsEveryPlayerOneReading_UntilShutdown()
    {
        Inline();
        var host = new FakeHost { RequestedPlayerCount = 2 };
        var dispatcher = new TestPlayerDispatcher();
        var samples = 0;
        var controller = new PlayerSetController<FakeSlot>(host, dispatcher, () => { samples++; return Sample; }, TimeSpan.FromMilliseconds(250));
        TestPlayerTimer timer = Assert.Single(dispatcher.Timers);
        Assert.Equal(TimeSpan.FromMilliseconds(250), timer.Interval);
        Assert.False(timer.IsRunning);

        _ = controller.StartAsync();
        Assert.True(timer.IsRunning);
        timer.Tick();

        Assert.Equal(1, samples);
        Assert.All(controller.Slots, static slot => Assert.Equal([Sample], slot.Statuses));

        FakeSlot[] slots = [.. controller.Slots];
        Assert.True(await controller.ShutdownAsync());
        timer.Tick();

        Assert.False(timer.IsRunning);
        Assert.True(timer.IsDisposed);
        Assert.Equal(1, samples);
        Assert.All(slots, static slot => Assert.Single(slot.Statuses));
    }

    [Fact]
    public void Stats_DefaultIntervalIsOneSecond()
    {
        Inline();
        var dispatcher = new TestPlayerDispatcher();
        _ = new PlayerSetController<FakeSlot>(new FakeHost(), dispatcher, () => Sample);

        Assert.Equal(TimeSpan.FromSeconds(1), Assert.Single(dispatcher.Timers).Interval);
    }

    /// <summary>Clears the test thread's synchronization context: awaits in the controller then continue inline.</summary>
    private static void Inline() => SynchronizationContext.SetSynchronizationContext(null);

    private sealed class FakeHost : IPlayerSetHost<FakeSlot>
    {
        public int RequestedPlayerCount { get; set; } = 1;

        public int? FailCreatingIndex { get; set; }

        public List<FakeSlot> Created { get; } = [];

        public List<string> Log { get; } = [];

        public List<(string What, Exception Exception)> Failures { get; } = [];

        /// <summary>The initializations of the next created players, in order; empty means they complete at once.</summary>
        public Queue<Task> InitGates { get; } = new();

        public int WindowFullScreenCount { get; private set; }

        public PlayerSetController<FakeSlot> CreateController() => new(this, new TestPlayerDispatcher(), static () => Sample);

        public FakeSlot CreateSlot(int index, bool atStartup)
        {
            Log.Add($"create P{index}");
            if (index == FailCreatingIndex)
                throw new InvalidOperationException("no surface");

            var slot = new FakeSlot(this, index, atStartup, InitGates.TryDequeue(out Task? gate) ? gate : Task.CompletedTask);
            Created.Add(slot);
            return slot;
        }

        public void Attach(FakeSlot slot) => Log.Add($"attach {slot.Name}");

        public void Detach(FakeSlot slot) => Log.Add($"detach {slot.Name}");

        public void Layout(IReadOnlyList<FakeSlot> slots, FakeSlot? fullScreen) =>
            Log.Add($"layout {string.Join(",", slots.Select(static slot => slot.Name))}{(fullScreen is null ? string.Empty : $" full {fullScreen.Name}")}");

        public void SetWindowFullScreen(bool fullScreen)
        {
            if (fullScreen)
                WindowFullScreenCount++;
            Log.Add($"window {fullScreen}");
        }

        public void ReportFailure(string what, Exception exception) => Failures.Add((what, exception));

        public int FallBackCount { get; private set; }

        public void FallBackRenderer()
        {
            FallBackCount++;
            Log.Add("fall back");
        }
    }

    private sealed class FakeSlot(FakeHost host, int index, bool atStartup, Task initGate) : IPlayerSlot
    {
        public event EventHandler? FullScreenToggleRequested;

        public event EventHandler? RendererDeviceLost;

        public void RaiseRendererDeviceLost() => RendererDeviceLost?.Invoke(this, EventArgs.Empty);

        public string Name { get; } = $"P{index}";

        public bool AtStartup { get; } = atStartup;

        public bool IsKeyboardFocusWithin { get; set; }

        public bool HasOpenFlyout { get; set; }

        public bool Initialized { get; private set; }

        public bool Disposed { get; private set; }

        public bool IsFullScreen { get; private set; }

        public Task DisposeGate { get; set; } = Task.CompletedTask;

        public Exception? DisposeFailure { get; set; }

        public List<ProcessSample> Statuses { get; } = [];

        public async Task InitializeAsync()
        {
            host.Log.Add($"init {Name}");
            await initGate;
            Initialized = true;
        }

        public async Task DisposeAsync()
        {
            host.Log.Add($"dispose {Name}");
            Disposed = true;
            await DisposeGate;
            if (DisposeFailure is { } failure)
                throw failure;
        }

        public void UpdateStatus(ProcessSample process) => Statuses.Add(process);

        public void SetFullScreen(bool fullScreen)
        {
            IsFullScreen = fullScreen;
            host.Log.Add($"fullscreen {Name} {fullScreen}");
        }

        public bool CloseFlyout()
        {
            if (!HasOpenFlyout)
                return false;

            HasOpenFlyout = false;
            return true;
        }

        public void RequestFullScreen() => FullScreenToggleRequested?.Invoke(this, EventArgs.Empty);
    }
}
