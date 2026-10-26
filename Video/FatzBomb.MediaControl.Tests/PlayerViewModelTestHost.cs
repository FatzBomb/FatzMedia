using System.Collections.Concurrent;
using FatzBomb.MediaControl.Core.Configuration;
using FatzBomb.MediaControl.Player.Shared;
using FatzBomb.MediaControl.Player.Shared.Hosting;
using FatzBomb.MediaControl.Player.Shared.ViewModels;

namespace FatzBomb.MediaControl.Tests;

/// <summary>
/// A test UI thread: posted work waits in a queue until <see cref="RunPending"/> runs it on the
/// test's thread, and timers tick only when told to.
/// </summary>
internal sealed class TestPlayerDispatcher : IPlayerDispatcher
{
    private readonly ConcurrentQueue<Action> _pending = new();

    public List<TestPlayerTimer> Timers { get; } = [];

    public bool CheckAccess() => false;

    public void Post(Action action) => _pending.Enqueue(action);

    public IPlayerTimer CreateTimer(TimeSpan interval, Action tick)
    {
        var timer = new TestPlayerTimer(interval, tick);
        Timers.Add(timer);
        return timer;
    }

    public int RunPending()
    {
        var count = 0;
        while (_pending.TryDequeue(out Action? action))
        {
            action();
            count++;
        }

        return count;
    }

    /// <summary>Runs posted work until <paramref name="condition"/> holds (or fails the test after the timeout).</summary>
    public async Task UntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TestEnvironment.EventuallyTimeout;
        while (true)
        {
            _ = RunPending();
            if (condition())
                return;
            if (DateTime.UtcNow > deadline)
                Assert.Fail("The condition did not hold in time.");
            await Task.Delay(10);
        }
    }
}

internal sealed class TestPlayerTimer(TimeSpan interval, Action tick) : IPlayerTimer
{
    public TimeSpan Interval { get; } = interval;

    public bool IsRunning { get; private set; }

    public bool IsDisposed { get; private set; }

    public void Start() => IsRunning = true;

    public void Stop() => IsRunning = false;

    public void Dispose() => IsDisposed = true;

    public void Tick() => tick();
}

internal sealed class TestPlayerClipboard : IPlayerClipboard
{
    public string? Text { get; private set; }

    public Task SetTextAsync(string text)
    {
        Text = text;
        return Task.CompletedTask;
    }
}

internal sealed class TestPlayerFilePicker : IPlayerFilePicker
{
    public string? Result { get; set; }

    public List<(string Title, string Filter)> Requests { get; } = [];

    public Task<string?> PickFileAsync(string title, string filter)
    {
        Requests.Add((title, filter));
        return Task.FromResult(Result);
    }
}

/// <summary>An engine factory whose creation waits for <paramref name="gate"/> (a slow native load).</summary>
internal sealed class GatedEngineFactory(Task gate) : IPlayerEngineFactory
{
    private readonly TestPlayerEngineFactory _inner = new();

    public TestMediaEngine Engine => _inner.Engine;

    public IReadOnlyList<string> NativeLibraryPaths => _inner.NativeLibraryPaths;

    public NativeDependencyReport? NativeDependenciesAfterFailure => null;

    public Task<PlayerRendererInfo> ResolveRendererAsync() => _inner.ResolveRendererAsync();

    public async Task<MediaControlPlayer> CreatePlayerAsync(PlayerRendererInfo renderer, MediaPlayerConfiguration configuration)
    {
        await gate.ConfigureAwait(false);
        return await _inner.CreatePlayerAsync(renderer, configuration).ConfigureAwait(false);
    }
}

/// <summary>Creates real <see cref="MediaControlPlayer"/>s over <see cref="TestMediaEngine"/>s.</summary>
internal sealed class TestPlayerEngineFactory : IPlayerEngineFactory
{
    public TestPlayerEngineFactory()
    {
        Platform = new PlayerModeSwitchTests.SwitchTestPlatformFactory
        {
            BeforeCreateServiceProvider = _ =>
            {
                if (Interlocked.Exchange(ref _failNextServiceProvider, 0) == 1)
                    throw new InvalidOperationException("Test: the engine graph could not be built.");
            },
        };
    }

    private int _failNextServiceProvider;

    public PlayerModeSwitchTests.SwitchTestPlatformFactory Platform { get; }

    /// <summary>Makes the next engine graph build fail once (a mode switch that cannot build its mode).</summary>
    public bool FailNextServiceProvider
    {
        get => Volatile.Read(ref _failNextServiceProvider) == 1;
        set => Volatile.Write(ref _failNextServiceProvider, value ? 1 : 0);
    }

    public PlayerRendererInfo Renderer { get; init; } = new(
        "Test renderer",
        VideoDecodingMethod.Software,
        RequiresFrameSinkSurface: true,
        [(MediaDiagnosticLevel.Info, "Test renderer selected.")]);

    public Exception? CreateFailure { get; init; }

    public NativeDependencyReport? FailureReport { get; init; }

    public MediaPlayerConfiguration? Configuration { get; private set; }

    public MediaControlPlayer? Created { get; private set; }

    public TestMediaEngine Engine => Platform.Engines[^1];

    public IReadOnlyList<string> NativeLibraryPaths { get; } = [];

    public NativeDependencyReport? NativeDependenciesAfterFailure => FailureReport;

    public Task<PlayerRendererInfo> ResolveRendererAsync() => Task.FromResult(Renderer);

    public Task<MediaControlPlayer> CreatePlayerAsync(PlayerRendererInfo renderer, MediaPlayerConfiguration configuration)
    {
        Configuration = configuration;
        if (CreateFailure is { } failure)
            return Task.FromException<MediaControlPlayer>(failure);

        return Task.Run(() => Created = new MediaControlPlayer(Platform, configuration, NativeLibraryPaths));
    }
}

/// <summary>One view model over the test doubles.</summary>
internal sealed class PlayerViewModelHarness : IAsyncDisposable
{
    public PlayerViewModelHarness(PlayerStartupOptions? options = null, TestPlayerEngineFactory? engine = null, bool withShell = true)
        : this(options, (IPlayerEngineFactory?)engine, withShell)
    {
    }

    public PlayerViewModelHarness(PlayerStartupOptions? options, IPlayerEngineFactory? engine, bool withShell = true)
    {
        IPlayerEngineFactory factory = engine ?? new TestPlayerEngineFactory();
        Engine = factory as TestPlayerEngineFactory ?? new TestPlayerEngineFactory();
        ViewModel = new PlayerViewModel(
            options ?? new PlayerStartupOptions(),
            new PlayerHost(Dispatcher, factory, withShell ? Clipboard : null, withShell ? FilePicker : null));
    }

    public TestPlayerDispatcher Dispatcher { get; } = new();

    public TestPlayerClipboard Clipboard { get; } = new();

    public TestPlayerFilePicker FilePicker { get; } = new();

    public TestPlayerEngineFactory Engine { get; }

    public PlayerViewModel ViewModel { get; }

    public MediaControlPlayer Player => ViewModel.Player ?? throw new InvalidOperationException("The player was not created.");

    public TestPlayerTimer PositionTimer => Assert.Single(Dispatcher.Timers);

    /// <summary>
    /// Creates and starts a view model. Posted work is not run: unowned diagnostics of other tests
    /// reach every player and would overwrite the status line (tests run posted work themselves).
    /// </summary>
    public static async Task<PlayerViewModelHarness> StartAsync(PlayerStartupOptions? options = null, TestPlayerEngineFactory? engine = null)
    {
        var harness = new PlayerViewModelHarness(options, engine);
        await harness.ViewModel.StartAsync();
        return harness;
    }

    /// <summary>Loads <paramref name="path"/> and waits until the view model shows its duration.</summary>
    public async Task LoadAsync(string path)
    {
        await ViewModel.LoadMediaAsync(path);
        Assert.True(Player.IsOpen);
        await Dispatcher.UntilAsync(() => ViewModel.DurationSeconds > 0 && ViewModel.PlaybackState != Core.Abstractions.MediaPlayerState.Null);
    }

    public async ValueTask DisposeAsync() => await ViewModel.DisposeAsync();
}
