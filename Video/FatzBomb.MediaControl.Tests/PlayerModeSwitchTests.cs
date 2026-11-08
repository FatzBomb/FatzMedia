using FatzBomb.MediaControl.Controllers;
using FatzBomb.MediaControl.Core.Abstractions;
using FatzBomb.MediaControl.Core.Configuration;
using System.Collections.Concurrent;

namespace FatzBomb.MediaControl.Tests;

/// <summary>Stage 043A slice 2: switching modes on the fly with <c>SetModeAsync</c>.</summary>
public sealed class PlayerModeSwitchTests
{
    [Fact]
    public async Task SameMode_IsANoOp()
    {
        var factory = new SwitchTestPlatformFactory();
        using var player = new MediaControlPlayer(factory, MediaPlayerConfiguration.Auto, []);
        var modeChanges = 0;
        player.ModeChanged += (_, _) => Interlocked.Increment(ref modeChanges);
        Assert.True(player.Load("song.mp3"));
        Assert.True(player.Play());

        Assert.True(await player.SetModeAsync(MediaPlayerMode.Full));
        await player.FlushControlOperationsAsync();

        Assert.Equal(MediaPlayerMode.Full, player.Mode);
        TestMediaEngine engine = Assert.Single(factory.Engines);
        Assert.Equal(0, engine.DisposeCount);
        Assert.Equal(1, engine.PlayCount);
        Assert.Equal(MediaPlayerState.Playing, player.State);
        Assert.Equal(0, modeChanges);
    }

    [Theory]
    [InlineData(MediaPlayerState.Playing)]
    [InlineData(MediaPlayerState.Paused)]
    [InlineData(MediaPlayerState.Stopped)]
    public async Task Switch_RebuildsTheGraphAndReopensAtPositionAndTransportState(MediaPlayerState transport)
    {
        var factory = new SwitchTestPlatformFactory();
        using var player = new MediaControlPlayer(factory, MediaPlayerConfiguration.Auto, []);
        MediaInputSource background = MediaInputSource.FromColor(0xFF336699);
        var loaded = new ConcurrentQueue<string?>();
        player.MediaLoaded += (_, e) => loaded.Enqueue(e.Source.FilePath);
        player.ResetPitchOnMediaLoad = true;
        player.ResetTempoOnMediaLoad = true;
        Assert.True(player.Load(MediaInputSource.FromFile("song.mp3"), background));
        player.Effects.Volume = 0.5;
        player.Effects.Pitch = 3;
        player.Effects.Tempo = 110;
        player.Equalizer = AudioEqualizerSettings.CreateGraphic10Band(3);
        player.AudioSyncOffset = TimeSpan.FromMilliseconds(200);
        Assert.True(player.Seek(42));
        if (transport == MediaPlayerState.Playing)
            Assert.True(player.Play());
        else if (transport == MediaPlayerState.Paused)
            Assert.True(player.Play() && player.Pause());

        Assert.True(await player.SetModeAsync(MediaPlayerMode.Audio));

        Assert.Equal(MediaPlayerMode.Audio, player.Mode);
        Assert.Equal([MediaPlayerMode.Full, MediaPlayerMode.Audio], factory.Modes);
        Assert.Equal(1, factory.Engines[0].DisposeCount);
        TestMediaEngine engine = factory.Engines[1];
        Assert.True(engine.IsOpen);
        Assert.Equal(["song.mp3", "song.mp3"], loaded.Select(Path.GetFileName));
        Assert.Same(background, engine.Background?.Source);
        Assert.Equal(42, engine.Position);
        Assert.Equal(transport, player.State);
        Assert.Equal(transport == MediaPlayerState.Playing ? 1 : 0, engine.PlayCount);
        // Carried over, and the reopen is the same item: no load reset of pitch or tempo.
        Assert.Equal(0.5, engine.TestAudio.Volume);
        Assert.Equal(3, engine.TestAudio.Pitch);
        Assert.Equal(10, engine.TestAudio.CurrentTempo);
        Assert.True(engine.TestAudio.Equalizer.IsEffective);
        Assert.Equal(0.2, engine.AudioVideoOffsetSeconds, 6);
        Assert.True(player.ResetPitchOnMediaLoad);
        Assert.True(player.ResetTempoOnMediaLoad);
    }

    [Fact]
    public async Task ExplicitBackgroundPause_HoldsAcrossAModeSwitch_UntilResumed()
    {
        var factory = new SwitchTestPlatformFactory();
        using var player = new MediaControlPlayer(factory, MediaPlayerConfiguration.Auto, []);
        Assert.True(player.Load("song.mp3"));
        Assert.True(await player.PauseBackgroundAsync());
        Assert.True(player.IsBackgroundPaused);
        Assert.True(factory.Engines[0].IsBackgroundPaused);

        Assert.True(await player.SetModeAsync(MediaPlayerMode.Background));

        // The rebuilt engine gets the host's pause again; Play does not end it.
        Assert.True(factory.Engines[1].IsBackgroundPaused);
        Assert.True(await player.PlayAsync());
        Assert.True(factory.Engines[1].IsBackgroundPaused);
        Assert.True(await player.ResumeBackgroundAsync());
        Assert.False(player.IsBackgroundPaused);
        Assert.False(factory.Engines[1].IsBackgroundPaused);
    }

    [Fact]
    public async Task UnacceptedSource_SwitchesAndClosesWithAWarning()
    {
        // Background mode does not accept audio files: its engine refuses to open them.
        var factory = new SwitchTestPlatformFactory
        {
            ConfigureEngine = (mode, engine) => engine.OpenSucceeds = mode != MediaPlayerMode.Background,
        };
        using var player = new MediaControlPlayer(factory, MediaPlayerConfiguration.Auto, []);
        Assert.True(player.Load("song.mp3"));
        Assert.True(player.Play());
        await player.FlushControlOperationsAsync();
        var closed = new ConcurrentQueue<string?>();
        var states = new ConcurrentQueue<MediaPlayerState>();
        var warnings = new ConcurrentQueue<MediaDiagnosticEventArgs>();
        var modeChanges = new ConcurrentQueue<MediaValueChangedEventArgs<MediaPlayerMode>>();
        player.MediaClosed += (_, e) => closed.Enqueue(e.Source.FilePath);
        player.StateChanged += (_, e) => states.Enqueue(e.CurrentState);
        player.MediaDiagnostic += (_, e) =>
        {
            if (e.Category == MediaPlayerModePolicy.DiagnosticCategory && e.Level == MediaDiagnosticLevel.Warning)
                warnings.Enqueue(e);
        };
        player.ModeChanged += (_, e) => modeChanges.Enqueue(e);

        Assert.True(await player.SetModeAsync(MediaPlayerMode.Background));
        await player.FlushControlOperationsAsync();

        Assert.Equal(MediaPlayerMode.Background, player.Mode);
        Assert.False(player.IsOpen);
        Assert.Equal(MediaPlayerState.Null, player.State);
        Assert.Equal(["song.mp3"], closed);
        Assert.Equal([MediaPlayerState.Null], states);
        Assert.Contains(warnings, e => e.Message.Contains("song.mp3", StringComparison.Ordinal));
        Assert.Single(modeChanges);
    }

    [Fact]
    public async Task LeavingBackground_RestoresTheHostLoopValue()
    {
        var factory = new SwitchTestPlatformFactory();
        using var player = new MediaControlPlayer(factory, MediaPlayerConfiguration.Auto, []);
        var loopChanges = new ConcurrentQueue<(bool Previous, bool Current)>();
        player.LoopMediaChanged += (_, e) => loopChanges.Enqueue((e.PreviousValue, e.CurrentValue));
        Assert.False(player.LoopMedia);

        Assert.True(await player.SetModeAsync(MediaPlayerMode.Background));
        Assert.True(player.LoopMedia);
        Assert.True(factory.Engines[^1].LoopPlayback);

        Assert.True(await player.SetModeAsync(MediaPlayerMode.Full));
        Assert.False(player.LoopMedia);
        Assert.False(factory.Engines[^1].LoopPlayback);
        Assert.Equal([(false, true), (true, false)], loopChanges);

        // A value the host sets while Background forces looping is its own, and is kept on leaving.
        Assert.True(await player.SetModeAsync(MediaPlayerMode.Background));
        player.LoopMedia = true;
        Assert.True(await player.SetModeAsync(MediaPlayerMode.Audio));
        Assert.True(player.LoopMedia);
        Assert.True(factory.Engines[^1].LoopPlayback);
    }

    [Fact]
    public async Task Surfaces_AreKeptInAudioModeAndReattachedOnReturn()
    {
        var factory = new SwitchTestPlatformFactory();
        using var player = new MediaControlPlayer(factory, MediaPlayerConfiguration.Auto, []);
        MediaPresentationSurfaceDescriptor primary = Surface("primary", 640, 360);
        MediaPresentationSurfaceDescriptor gone = Surface("gone", 320, 240);
        Assert.True(player.AttachPresentationSurface(primary));
        Assert.True(player.AttachPresentationSurface(gone));
        RecordingCoordinator fullCoordinator = Assert.Single(factory.Coordinators);

        Assert.True(await player.SetModeAsync(MediaPlayerMode.Audio));

        // Renderer resources freed; the host can still resize, add and remove surfaces.
        Assert.True(fullCoordinator.Disposed);
        Assert.True(player.ResizePresentationSurface("primary", 1280, 720));
        Assert.True(player.AttachPresentationSurface(Surface("added", 800, 600)));
        Assert.True(player.DetachPresentationSurface("gone"));

        Assert.True(await player.SetModeAsync(MediaPlayerMode.Full));

        RecordingCoordinator returned = factory.Coordinators[^1];
        Assert.NotSame(fullCoordinator, returned);
        Assert.Equal(["added", "primary"], returned.Surfaces.Keys.Order(StringComparer.Ordinal));
        Assert.Equal((1280, 720), (returned.Surfaces["primary"].PixelWidth, returned.Surfaces["primary"].PixelHeight));
        Assert.True(player.ResizePresentationSurface("primary", 1920, 1080));
        Assert.Equal(1920, returned.Surfaces["primary"].PixelWidth);
        Assert.False(player.ResizePresentationSurface("gone", 100, 100));
    }

    [Fact]
    public async Task ModeChanged_IsRaisedOnceAfterTheSwitch()
    {
        var factory = new SwitchTestPlatformFactory();
        using var player = new MediaControlPlayer(factory, MediaPlayerConfiguration.Auto, []);
        var changes = new ConcurrentQueue<(MediaPlayerMode Previous, MediaPlayerMode Current, MediaPlayerMode Seen)>();
        player.ModeChanged += (_, e) => changes.Enqueue((e.PreviousValue, e.CurrentValue, player.Mode));

        Assert.True(await player.SetModeAsync(MediaPlayerMode.Audio));
        Assert.True(await player.SetModeAsync(MediaPlayerMode.Audio));
        await player.FlushControlOperationsAsync();

        Assert.Equal([(MediaPlayerMode.Full, MediaPlayerMode.Audio, MediaPlayerMode.Audio)], changes);
        Assert.Equal(2, factory.Engines.Count);
    }

    [Fact]
    public async Task CallsMadeDuringASwitch_RunAfterIt()
    {
        using var releaseBuild = new ManualResetEventSlim();
        var buildEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new SwitchTestPlatformFactory
        {
            BeforeCreateServiceProvider = count =>
            {
                if (count == 2)
                {
                    buildEntered.TrySetResult();
                    releaseBuild.Wait();
                }
            },
        };
        using var player = new MediaControlPlayer(factory, MediaPlayerConfiguration.Auto, []);
        Assert.True(player.Load("song.mp3"));
        Assert.True(player.Play());

        Task<bool> switching = player.SetModeAsync(MediaPlayerMode.Audio);
        await buildEntered.Task.WaitAsync(TestEnvironment.EventuallyTimeout);
        Task<bool> pause = player.PauseAsync();

        await Task.Delay(50);
        Assert.False(pause.IsCompleted);

        releaseBuild.Set();
        Assert.True(await switching.WaitAsync(TestEnvironment.EventuallyTimeout));
        Assert.True(await pause.WaitAsync(TestEnvironment.EventuallyTimeout));
        Assert.Equal(MediaPlayerState.Paused, factory.Engines[1].State);
        Assert.Equal(MediaPlayerState.Paused, player.State);
    }

    [Fact]
    public async Task DisposeDuringASwitch_WaitsForItAndDisposesTheNewGraph()
    {
        using var releaseBuild = new ManualResetEventSlim();
        var buildEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new SwitchTestPlatformFactory
        {
            BeforeCreateServiceProvider = count =>
            {
                if (count == 2)
                {
                    buildEntered.TrySetResult();
                    releaseBuild.Wait();
                }
            },
        };
        var player = new MediaControlPlayer(factory, MediaPlayerConfiguration.Auto, []);
        Assert.True(player.Load("song.mp3"));

        Task<bool> switching = player.SetModeAsync(MediaPlayerMode.Background);
        await buildEntered.Task.WaitAsync(TestEnvironment.EventuallyTimeout);
        Task dispose = Task.Run(player.Dispose);

        await Task.Delay(50);
        Assert.False(dispose.IsCompleted);

        releaseBuild.Set();
        Assert.True(await switching.WaitAsync(TestEnvironment.EventuallyTimeout));
        await dispose.WaitAsync(TestEnvironment.EventuallyTimeout);
        Assert.Equal(1, factory.Engines[0].DisposeCount);
        Assert.Equal(1, factory.Engines[1].DisposeCount);
        // Refused synchronously, like every call on a disposed player.
        _ = Assert.Throws<ObjectDisposedException>(() => { _ = player.SetModeAsync(MediaPlayerMode.Full); });
    }

    [Fact]
    public async Task FailedBuild_RebuildsThePreviousModeAndReopens()
    {
        var factory = new SwitchTestPlatformFactory
        {
            BeforeCreateServiceProvider = count =>
            {
                if (count == 2)
                    throw new InvalidOperationException("No audio device.");
            },
        };
        using var player = new MediaControlPlayer(factory, MediaPlayerConfiguration.Auto, []);
        var modeChanges = 0;
        player.ModeChanged += (_, _) => Interlocked.Increment(ref modeChanges);
        Assert.True(player.Load("song.mp3"));
        Assert.True(player.Seek(12));
        Assert.True(player.Play());

        Assert.False(await player.SetModeAsync(MediaPlayerMode.Audio));
        await player.FlushControlOperationsAsync();

        Assert.Equal(MediaPlayerMode.Full, player.Mode);
        TestMediaEngine engine = factory.Engines[^1];
        Assert.Equal(2, factory.Engines.Count);
        Assert.True(engine.IsOpen);
        Assert.Equal(12, engine.Position);
        Assert.Equal(MediaPlayerState.Playing, player.State);
        Assert.Equal(0, modeChanges);
    }

    [Fact]
    public async Task PreparedNext_SurvivesTheSwitch()
    {
        var factory = new SwitchTestPlatformFactory();
        using var player = new MediaControlPlayer(factory, MediaPlayerConfiguration.Auto, []);
        MediaInputSource next = MediaInputSource.FromColor(0xFF000000);
        Assert.True(await player.PrepareNextAsync(next));
        await player.FlushControlOperationsAsync();
        var slotChanges = 0;
        player.PreparedNextChanged += (_, _) => Interlocked.Increment(ref slotChanges);

        Assert.True(await player.SetModeAsync(MediaPlayerMode.Background));
        await player.FlushControlOperationsAsync();

        Assert.Equal(PreparedNextState.Ready, player.PreparedNext.State);
        Assert.Same(next, player.PreparedNext.Source);
        Assert.Equal(0, slotChanges);
        Assert.True(await player.TransitionToPreparedNextAsync());
        Assert.True(factory.Engines[^1].IsOpen);
    }

    internal static MediaPresentationSurfaceDescriptor Surface(string id, int width, int height) =>
        new(id, MediaPresentationSurfaceRole.Primary, MediaNativeSurfaceDescriptor.Win32Hwnd((nint)0x1234), width, height);

    /// <summary>A new engine and coordinator for every graph, so each rebuild can be observed.</summary>
    internal sealed class SwitchTestPlatformFactory : IMediaControlPlatformFactory
    {
        private int _createServiceProviderCount;

        public List<TestMediaEngine> Engines { get; } = [];

        public List<MediaPlayerMode> Modes { get; } = [];

        public List<RecordingCoordinator> Coordinators { get; } = [];

        public Action<int>? BeforeCreateServiceProvider { get; init; }

        public Action<MediaPlayerMode, TestMediaEngine>? ConfigureEngine { get; init; }

        public string LastNativeLoadMessage => string.Empty;

        public VideoDecodeCapabilities GetVideoDecodeCapabilities() => VideoDecodeCapabilities.Create([], []);

        public IMediaPresentationCoordinator CreatePresentationCoordinator(IMediaEngine mediaEngine, MediaPlayerConfiguration configuration)
        {
            var coordinator = new RecordingCoordinator();
            Coordinators.Add(coordinator);
            return coordinator;
        }

        public IServiceProvider CreateServiceProvider(MediaPlayerConfiguration configuration, IReadOnlyList<string> nativeLibraryPaths)
        {
            BeforeCreateServiceProvider?.Invoke(Interlocked.Increment(ref _createServiceProviderCount));
            var engine = new TestMediaEngine();
            ConfigureEngine?.Invoke(configuration.Mode, engine);
            Engines.Add(engine);
            Modes.Add(configuration.Mode);
            return new TestServiceProvider(engine);
        }
    }

    /// <summary>A coordinator that accepts surfaces and records them.</summary>
    internal sealed class RecordingCoordinator : IMediaPresentationCoordinator
    {
        public event EventHandler? FramePresented { add { } remove { } }

        public ConcurrentDictionary<string, MediaPresentationSurfaceDescriptor> Surfaces { get; } = new();

        public bool Disposed { get; private set; }

        public VideoDisplayMode VideoDisplayMode { get; set; }

        public BaseVisualTransform BaseVisualTransform { get; set; }

        public bool AttachPresentationSurface(MediaPresentationSurfaceDescriptor surface)
        {
            Surfaces[surface.Id] = surface;
            return !Disposed;
        }

        public bool ResizePresentationSurface(string surfaceId, int pixelWidth, int pixelHeight)
        {
            if (!Surfaces.TryGetValue(surfaceId, out MediaPresentationSurfaceDescriptor? surface))
                return false;

            Surfaces[surfaceId] = surface.WithSize(pixelWidth, pixelHeight);
            return true;
        }

        public bool SetPresentationSurfaceLiveResize(string surfaceId, bool isResizing) => Surfaces.ContainsKey(surfaceId);

        public bool DetachPresentationSurface(string surfaceId) => Surfaces.TryRemove(surfaceId, out _);

        public void RefreshCurrentPosition() { }

        public void StartRendering() { }

        public void StopRendering() { }

        public void RefreshAttachments() { }

        public void Dispose() => Disposed = true;
    }
}
