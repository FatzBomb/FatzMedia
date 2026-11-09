using FatzMedia.Video.Core.Abstractions;
using FatzMedia.Video.Core.Configuration;
using FatzMedia.Video.Sample.Player.Shared;
using FatzMedia.Video.Sample.Player.Shared.ViewModels;

namespace FatzMedia.Video.Tests;

/// <summary>Stage 044A: the test players' shared view model, over the engine test doubles.</summary>
public sealed class PlayerViewModelTests
{
    [Fact]
    public async Task Start_CreatesTheEngineWithTheViewModelSettings()
    {
        var options = new PlayerStartupOptions
        {
            ResetPitchOnLoad = true,
            TrimSilence = true,
            NetworkRetries = 2,
            NetworkCaFile = "ca.pem",
            NetworkAllowInvalidCertificates = true,
            PreferCpuHdrToneMapping = true,
            EqualizerGains = [3, -3],
        };
        await using var harness = await PlayerViewModelHarness.StartAsync(options);
        var viewModel = harness.ViewModel;
        var player = harness.Player;

        Assert.Null(viewModel.StartupError);
        Assert.False(viewModel.IsBusy);
        Assert.True(viewModel.CanOpen);
        Assert.True(harness.PositionTimer.IsRunning);
        var configuration = Assert.NotNull(harness.Engine.Configuration);
        Assert.Equal(VideoDecodingMethod.Software, configuration.VideoDecoding);
        Assert.True(configuration.EnableLogging);
        Assert.True(configuration.ResetPitchOnMediaLoad);
        Assert.False(configuration.ResetTempoOnMediaLoad);
        Assert.True(configuration.SilenceTrim.Enabled);
        Assert.Equal(2, configuration.NetworkRetry?.MaxRetries);
        Assert.Equal("ca.pem", configuration.NetworkTls?.CertificateAuthorityFile);
        Assert.True(configuration.NetworkTls?.AllowInvalidCertificates);
        Assert.True(configuration.HdrToneMapping.PreferCpu);
        Assert.False(configuration.HdrToneMapping.Disabled);

        Assert.Equal(1.0, player.Effects.Volume);
        Assert.True(player.Normalize);
        Assert.True(player.PlayOnLoad);
        Assert.False(player.LoopMedia);
        Assert.True(player.ShowMidiLyrics);
        Assert.True(player.ResetPitchOnMediaLoad);
        Assert.Equal(MediaScrubAudio.SilentWhileMoving, player.ScrubAudio);
        Assert.Equal(VideoFrameTiming.SourceCadence, player.VideoFrameTiming);
        Assert.Equal(VideoDisplayMode.Zoom, player.VideoDisplayMode);
        Assert.True(player.Equalizer.IsEffective);
        Assert.True(viewModel.EqualizerEnabled);
        Assert.Equal([3, -3, 0, 0, 0, 0, 0, 0, 0, 0], viewModel.EqualizerBands.Select(static band => band.GainDb));

        Assert.StartsWith("Renderer: Test renderer | Visual transform=IDENTITY | Load resets: Pitch=True, Tempo=False | Silence trim=True", viewModel.StatusText);
        _ = harness.Dispatcher.RunPending();
        Assert.Contains(viewModel.DiagnosticLog, static entry => entry is { Category: "Renderer", Message: "Test renderer selected." });
    }

    [Fact]
    public async Task Start_WhenTheEngineCannotStart_KeepsTheErrorAndDisablesTheControls()
    {
        var engine = new TestPlayerEngineFactory { CreateFailure = new DllNotFoundException("bass.dll") };
        await using var harness = await PlayerViewModelHarness.StartAsync(engine: engine);
        var viewModel = harness.ViewModel;

        Assert.IsType<DllNotFoundException>(viewModel.StartupError);
        Assert.Null(viewModel.Player);
        Assert.Equal("Native DLLs unavailable: bass.dll", viewModel.StatusText);
        Assert.False(viewModel.CanOpen);
        Assert.False(viewModel.PlayCommand.CanExecute(null));
        Assert.False(viewModel.LoadMediaCommand.CanExecute("song.mp3"));
    }

    [Fact]
    public async Task Start_IsStartedOnce()
    {
        await using var harness = new PlayerViewModelHarness();

        var first = harness.ViewModel.StartAsync();
        Assert.Same(first, harness.ViewModel.StartAsync());
        await first;
        Assert.Single(harness.Engine.Platform.Engines);
    }

    [Fact]
    public async Task Start_LoadsTheStartupMedia()
    {
        await using var harness = await PlayerViewModelHarness.StartAsync(new PlayerStartupOptions { MediaPath = "song.mp3" });

        Assert.True(harness.Player.IsOpen);
        Assert.Equal("Loaded: song.mp3", harness.ViewModel.StatusText);
    }

    [Fact]
    public async Task Start_LoadsTheStartupMediaWithoutWaitingForTheSurface()
    {
        // Stage 051A: the fixed 500 ms startup delay is gone; the surface binds when Player is set,
        // and one that attaches later gets the current frame re-presented by Core.
        // Timed from the moment the player exists, so engine creation on a slow runner does not count.
        await using var harness = new PlayerViewModelHarness(new PlayerStartupOptions { MediaPath = "song.mp3" }, null);
        var start = harness.ViewModel.StartAsync();
        await harness.Dispatcher.UntilAsync(() => harness.ViewModel.Player is not null);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        await start;

        Assert.InRange(stopwatch.ElapsedMilliseconds, 0, 400);
        Assert.True(harness.Player.IsOpen);
    }

    [Fact]
    public async Task OpenFile_LoadsThePickedFileWithTheEngineFilter()
    {
        await using var harness = await PlayerViewModelHarness.StartAsync();
        harness.FilePicker.Result = "song.mp3";

        await harness.ViewModel.OpenFileCommand.ExecuteAsync(null);

        (var title, var filter) = Assert.Single(harness.FilePicker.Requests);
        Assert.Equal("Open media file", title);
        Assert.Equal(harness.Player.OpenFileDialogFilter, filter);
        Assert.True(harness.Player.IsOpen);
        Assert.Equal("Loaded: song.mp3", harness.ViewModel.StatusText);
    }

    [Fact]
    public async Task OpenFile_Cancelled_LoadsNothing()
    {
        await using var harness = await PlayerViewModelHarness.StartAsync();

        await harness.ViewModel.OpenFileCommand.ExecuteAsync(null);

        Assert.Single(harness.FilePicker.Requests);
        Assert.False(harness.Player.IsOpen);
    }

    [Fact]
    public async Task Transport_PlaysPausesAndStops()
    {
        await using var harness = await PlayerViewModelHarness.StartAsync();
        harness.ViewModel.AutoPlay = false;
        await harness.ViewModel.LoadMediaAsync("song.mp3");

        await harness.ViewModel.PlayCommand.ExecuteAsync(null);
        Assert.Equal(MediaPlayerState.Playing, harness.Player.State);
        Assert.Equal("Playing", harness.ViewModel.StatusText);

        await harness.ViewModel.PauseCommand.ExecuteAsync(null);
        Assert.Equal(MediaPlayerState.Paused, harness.Player.State);
        Assert.Equal("Paused", harness.ViewModel.StatusText);

        await harness.ViewModel.StopCommand.ExecuteAsync(null);
        Assert.Equal(MediaPlayerState.Stopped, harness.Player.State);
        Assert.Equal("Stopped", harness.ViewModel.StatusText);
        Assert.Equal(1, harness.Engine.Engine.StopCount);
        Assert.Equal("song.mp3", harness.ViewModel.LoadedMediaText);
    }

    [Fact]
    public async Task Load_AFailedLoadKeepsTheLoadedName()
    {
        await using var harness = await PlayerViewModelHarness.StartAsync();
        await harness.LoadAsync("song.mp3");
        harness.Engine.Engine.OpenSucceeds = false;

        await harness.ViewModel.LoadMediaAsync("broken.mp3");

        Assert.Equal("Could not load: broken.mp3", harness.ViewModel.StatusText);
        Assert.Equal("song.mp3", harness.ViewModel.LoadedMediaText);
    }

    [Fact]
    public async Task DisposeAsync_WhileTheEngineStarts_DisposesItWhenItArrives()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var engine = new GatedEngineFactory(release.Task);
        await using var harness = new PlayerViewModelHarness(null, engine);
        var start = harness.ViewModel.StartAsync();

        var disposing = harness.ViewModel.DisposeAsync();
        release.SetResult();
        await disposing;
        await start;

        Assert.Null(harness.ViewModel.Player);
        Assert.Equal(1, engine.Engine.DisposeCount);
    }

    [Fact]
    public async Task TogglePlayPause_FollowsTheLatestState()
    {
        await using var harness = await PlayerViewModelHarness.StartAsync();
        var viewModel = harness.ViewModel;
        viewModel.AutoPlay = false;
        await harness.LoadAsync("song.mp3");
        Assert.Equal(MediaPlayerState.Stopped, viewModel.PlaybackState);

        await viewModel.TogglePlayPauseCommand.ExecuteAsync(null);
        Assert.Equal(MediaPlayerState.Playing, harness.Player.State);
        await harness.Dispatcher.UntilAsync(() => viewModel.IsPlaying);

        await viewModel.TogglePlayPauseCommand.ExecuteAsync(null);
        Assert.Equal(MediaPlayerState.Paused, harness.Player.State);
    }

    [Fact]
    public async Task Mode_StartsInTheRequestedModeAndSwitchesOnTheFly()
    {
        await using var harness = await PlayerViewModelHarness.StartAsync(new PlayerStartupOptions { Mode = MediaPlayerMode.Audio });
        var viewModel = harness.ViewModel;
        Assert.Equal(MediaPlayerMode.Audio, harness.Engine.Configuration?.Mode);
        Assert.Equal(MediaPlayerMode.Audio, viewModel.PlayerMode);
        Assert.Equal(MediaPlayerMode.Audio, harness.Player.Mode);
        Assert.True(viewModel.CanSwitchMode);
        Assert.Equal(Enum.GetValues<MediaPlayerMode>(), viewModel.PlayerModes);

        // The switch can finish inside the setter when the engine answers at once, so record
        // that it was raised rather than reading it afterwards.
        var switchingSeen = false;
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PlayerViewModel.IsSwitchingMode) && viewModel.IsSwitchingMode)
                switchingSeen = true;
        };
        viewModel.PlayerMode = MediaPlayerMode.Background;
        Assert.True(switchingSeen);
        await harness.Dispatcher.UntilAsync(() => !viewModel.IsSwitchingMode);

        Assert.Equal(MediaPlayerMode.Background, harness.Player.Mode);
        Assert.Equal(MediaPlayerMode.Background, viewModel.PlayerMode);
        // Background mode loops; the view model shows it without sending it back.
        Assert.True(viewModel.LoopPlayback);
        Assert.Equal([MediaPlayerMode.Audio, MediaPlayerMode.Background], harness.Engine.Platform.Modes);

        viewModel.PlayerMode = MediaPlayerMode.Full;
        await harness.Dispatcher.UntilAsync(() => !viewModel.IsSwitchingMode && harness.Player.Mode == MediaPlayerMode.Full);
        await harness.Dispatcher.UntilAsync(() => !viewModel.LoopPlayback);
        Assert.False(harness.Player.LoopMedia);
    }

    [Fact]
    public async Task Mode_BackgroundAtStartupShowsTheForcedLoop()
    {
        await using var harness = await PlayerViewModelHarness.StartAsync(new PlayerStartupOptions { Mode = MediaPlayerMode.Background });

        Assert.True(harness.Player.LoopMedia);
        Assert.True(harness.ViewModel.LoopPlayback);
    }

    [Fact]
    public async Task Mode_AFailedSwitchShowsTheModeTheEngineKept()
    {
        var engine = new TestPlayerEngineFactory();
        await using var harness = await PlayerViewModelHarness.StartAsync(engine: engine);
        var viewModel = harness.ViewModel;
        engine.FailNextServiceProvider = true;

        viewModel.PlayerMode = MediaPlayerMode.Audio;
        await harness.Dispatcher.UntilAsync(() => !viewModel.IsSwitchingMode);

        Assert.Equal(MediaPlayerMode.Full, harness.Player.Mode);
        Assert.Equal(MediaPlayerMode.Full, viewModel.PlayerMode);
        Assert.Contains(viewModel.DiagnosticLog, static entry => entry is { Category: "Player", Message: "Could not switch to Audio mode" });
    }

    [Fact]
    public async Task Settings_AreClampedAndForwardedToTheEngine()
    {
        await using var harness = await PlayerViewModelHarness.StartAsync();
        var viewModel = harness.ViewModel;
        var player = harness.Player;

        viewModel.Volume = 1.5;
        viewModel.Pitch = 20;
        viewModel.Tempo = 10;
        viewModel.CdgBackgroundOpacity = 140;
        viewModel.LoopPlayback = true;
        viewModel.SilentDrag = false;
        viewModel.DisplayLockedTiming = true;
        viewModel.VideoDisplayMode = VideoDisplayMode.Stretch;
        viewModel.Normalize = false;
        viewModel.AutoPlay = false;

        Assert.Equal(1.0, viewModel.Volume);
        Assert.Equal(12, viewModel.Pitch);
        Assert.Equal(50, viewModel.Tempo);
        Assert.Equal(100, viewModel.CdgBackgroundOpacity);
        Assert.Equal(12, player.Effects.Pitch);
        Assert.Equal(50, player.Effects.Tempo);
        Assert.True(player.LoopMedia);
        Assert.Equal(MediaScrubAudio.Audible, player.ScrubAudio);
        Assert.Equal(VideoFrameTiming.DisplayLocked, player.VideoFrameTiming);
        Assert.Equal(VideoDisplayMode.Stretch, player.VideoDisplayMode);
        Assert.False(player.Normalize);
        Assert.False(player.PlayOnLoad);

        viewModel.ToggleLoopCommand.Execute(null);
        Assert.False(viewModel.LoopPlayback);
        Assert.False(player.LoopMedia);
    }

    [Fact]
    public async Task Sync_AdjustsAndResetsTheOffsets()
    {
        await using var harness = await PlayerViewModelHarness.StartAsync();
        var viewModel = harness.ViewModel;
        Assert.False(viewModel.AdjustSyncCommand.CanExecute("audio:+"));
        await viewModel.LoadMediaAsync("song.mp3");
        Assert.True(viewModel.AdjustSyncCommand.CanExecute("audio:+"));

        viewModel.AdjustSyncCommand.Execute("audio:+");
        viewModel.AdjustSyncCommand.Execute("cdg:-");
        viewModel.AdjustSyncCommand.Execute("subtitle:+");
        viewModel.AdjustSyncCommand.Execute("subtitle:+");

        Assert.Equal("+0.1s", viewModel.AudioVideoSyncText);
        Assert.Equal("-0.1s", viewModel.CdgSyncText);
        Assert.Equal("+0.2s", viewModel.SubtitleSyncText);
        Assert.Equal("Subtitle sync +0.2s", viewModel.StatusText);
        Assert.Equal(TimeSpan.FromMilliseconds(100), harness.Player.AudioSyncOffset);

        viewModel.ResetSyncCommand.Execute(null);
        Assert.Equal("0.0s", viewModel.AudioVideoSyncText);
        Assert.Equal(TimeSpan.Zero, harness.Player.SubtitleSyncOffset);
        Assert.Equal("Playback sync offsets reset.", viewModel.StatusText);
    }

    [Fact]
    public async Task PositionTimer_FollowsThePlaybackPosition()
    {
        await using var harness = await PlayerViewModelHarness.StartAsync();
        var viewModel = harness.ViewModel;
        await harness.LoadAsync("song.mp3");
        var engine = harness.Engine.Engine;
        engine.Duration = 3725;
        engine.Position = 61.6;

        // A tick while the previous read is out is skipped: tick until one reads the new values.
        await harness.Dispatcher.UntilAsync(() =>
        {
            harness.PositionTimer.Tick();
            return viewModel.DurationSeconds == 3725;
        });

        Assert.Equal(3725, viewModel.DurationSeconds);
        Assert.Equal(61.6, viewModel.PositionSeconds, 6);
        Assert.Equal(61.6, viewModel.SeekPositionSeconds, 6);
        Assert.True(viewModel.CanSeek);
        Assert.Equal("1:01", viewModel.ElapsedText);
        Assert.Equal("-1:01:03", viewModel.RemainingText);
        Assert.Equal(MediaPlayerState.Playing, viewModel.PlaybackState);
        Assert.True(viewModel.IsPlaying);
    }

    [Fact]
    public async Task SeekPreview_ScrubsWhileDraggingAndSeeksOnRelease()
    {
        await using var harness = await PlayerViewModelHarness.StartAsync();
        var viewModel = harness.ViewModel;
        var player = harness.Player;
        await harness.LoadAsync("song.mp3");
        var engine = harness.Engine.Engine;

        viewModel.BeginSeekPreview();
        await harness.Dispatcher.UntilAsync(() => player.Controls.IsScrubbing);

        viewModel.SeekPositionSeconds = 30;
        Assert.Equal(30, viewModel.PositionSeconds);
        await harness.Dispatcher.UntilAsync(() => Math.Abs(engine.Position - 30) < 0.001);

        viewModel.SeekPositionSeconds = 500;
        Assert.Equal(120, viewModel.SeekPositionSeconds);
        viewModel.SeekPositionSeconds = 45;
        viewModel.EndSeekPreview();

        await harness.Dispatcher.UntilAsync(() => !player.Controls.IsScrubbing && Math.Abs(engine.Position - 45) < 0.001);
        Assert.Equal(45, viewModel.PositionSeconds);
        viewModel.CancelSeekPreview();
        await player.FlushControlOperationsAsync();
        Assert.False(player.Controls.IsScrubbing);
    }

    [Fact]
    public async Task SeekPreview_NeverBlocksTheCallerBehindASlowSeek()
    {
        await using var harness = await PlayerViewModelHarness.StartAsync();
        var viewModel = harness.ViewModel;
        var player = harness.Player;
        await harness.LoadAsync("song.mp3");
        var engine = harness.Engine.Engine;
        using var release = new ManualResetEventSlim();
        var held = 0;
        engine.OnSeek = seconds =>
        {
            if (seconds == 5 && Interlocked.Exchange(ref held, 1) == 0)
                release.Wait(TestEnvironment.EventuallyTimeout);
        };
        var slow = player.SeekAsync(5);
        await harness.Dispatcher.UntilAsync(() => Volatile.Read(ref held) == 1);

        // BeginScrub and EndScrub wait behind the held seek, but not on the caller's thread.
        viewModel.BeginSeekPreview();
        viewModel.SeekPositionSeconds = 20;
        viewModel.SeekPositionSeconds = 25;
        viewModel.EndSeekPreview();
        Assert.False(player.Controls.IsScrubbing);
        Assert.Equal(25, viewModel.PositionSeconds);

        release.Set();
        Assert.True(await slow);
        await harness.Dispatcher.UntilAsync(() => Math.Abs(engine.Position - 25) < 0.001);
        await player.FlushControlOperationsAsync();
        await harness.Dispatcher.UntilAsync(() => !player.Controls.IsScrubbing);
    }

    [Fact]
    public async Task SeekTo_SeeksOnceWithoutAScrub()
    {
        await using var harness = await PlayerViewModelHarness.StartAsync();
        var viewModel = harness.ViewModel;
        await harness.LoadAsync("song.mp3");
        var scrubbing = false;
        harness.Engine.Engine.OnSeek = _ => scrubbing |= harness.Player.Controls.IsScrubbing;

        viewModel.SeekTo(70);

        Assert.Equal(70, viewModel.PositionSeconds);
        await harness.Dispatcher.UntilAsync(() => Math.Abs(harness.Engine.Engine.Position - 70) < 0.001);
        Assert.False(scrubbing);
    }

    [Fact]
    public async Task SeekPosition_WithoutAPreview_DoesNotSeek()
    {
        await using var harness = await PlayerViewModelHarness.StartAsync();
        var viewModel = harness.ViewModel;
        await harness.LoadAsync("song.mp3");
        var seeks = 0;
        harness.Engine.Engine.OnSeek = _ => Interlocked.Increment(ref seeks);

        viewModel.SeekPositionSeconds = 30;
        await harness.Player.FlushControlOperationsAsync();

        Assert.Equal(0, Volatile.Read(ref seeks));
        Assert.Equal(0, viewModel.PositionSeconds);
    }

    [Fact]
    public async Task Flyouts_OpenOneAtATimeAndToggle()
    {
        await using var harness = new PlayerViewModelHarness();
        var viewModel = harness.ViewModel;

        viewModel.ToggleFlyoutCommand.Execute("Audio");
        Assert.True(viewModel.IsAudioFlyoutActive);
        viewModel.ToggleFlyoutCommand.Execute("Log");
        Assert.False(viewModel.IsAudioFlyoutActive);
        Assert.True(viewModel.IsLogFlyoutActive);
        viewModel.ToggleFlyoutCommand.Execute("Log");
        Assert.Equal(PlayerFlyout.None, viewModel.ActiveFlyout);
        viewModel.ToggleFlyoutCommand.Execute("AV");
        viewModel.CloseFlyout();
        Assert.Equal(PlayerFlyout.None, viewModel.ActiveFlyout);
        Assert.Equal(PlayerFlyout.Sync, PlayerViewModel.ParseFlyout("Sync"));
        Assert.Equal(PlayerFlyout.None, PlayerViewModel.ParseFlyout("sync"));
    }

    [Fact]
    public async Task Diagnostics_AreLoggedNewestFirstAndCopiedOldestFirst()
    {
        await using var harness = await PlayerViewModelHarness.StartAsync();
        var viewModel = harness.ViewModel;
        viewModel.ClearLogCommand.Execute(null);
        List<string> statuses = [];
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PlayerViewModel.StatusText))
                statuses.Add(viewModel.StatusText);
        };

        DiagnosticRouter.Raise(harness.Player, MediaDiagnosticLevel.Info, "Unit", "first");
        DiagnosticRouter.Raise(harness.Player, MediaDiagnosticLevel.Warning, "Unit", "second");

        await harness.Player.FlushControlOperationsAsync();
        await harness.Dispatcher.UntilAsync(() => viewModel.DiagnosticLog.Count(static entry => entry.Category == "Unit") == 2);

        DiagnosticLogEntry[] unit = [.. viewModel.DiagnosticLog.Where(static entry => entry.Category == "Unit")];
        Assert.Equal(["second", "first"], unit.Select(static entry => entry.Message));
        Assert.Contains("[Warning] Unit: second", statuses);
        Assert.DoesNotContain("[Info] Unit: first", statuses);

        await viewModel.CopyLogCommand.ExecuteAsync(null);
        var copied = Assert.IsType<string>(harness.Clipboard.Text);
        Assert.True(copied.IndexOf("Unit: first", StringComparison.Ordinal) < copied.IndexOf("Unit: second", StringComparison.Ordinal));
        Assert.StartsWith("Copied ", viewModel.StatusText);
    }

    [Fact]
    public async Task CopyLog_WithoutAClipboard_SaysSo()
    {
        await using var harness = new PlayerViewModelHarness(withShell: false);

        await harness.ViewModel.CopyLogCommand.ExecuteAsync(null);
        await harness.ViewModel.OpenFileCommand.ExecuteAsync(null);

        Assert.Equal("No file picker is available.", harness.ViewModel.StatusText);
        Assert.Equal(string.Empty, harness.ViewModel.LogFilePath);
    }

    [Fact]
    public async Task Equalizer_FlattenSetsEveryBandToZero()
    {
        await using var harness = await PlayerViewModelHarness.StartAsync(new PlayerStartupOptions { EqualizerGains = [20, -4.44] });
        var viewModel = harness.ViewModel;

        Assert.Equal(12, viewModel.EqualizerBands[0].GainDb);
        Assert.Equal(-4.4, viewModel.EqualizerBands[1].GainDb);
        Assert.Equal("31 Hz band, +12 dB", viewModel.EqualizerBands[0].ToolTipText);
        Assert.Equal("16k", viewModel.EqualizerBands[^1].Label);

        viewModel.FlattenEqualizerCommand.Execute(null);

        Assert.All(viewModel.EqualizerBands, static band => Assert.Equal(0, band.GainDb));
        Assert.Equal("Equalizer flattened.", viewModel.StatusText);
        Assert.False(harness.Player.Equalizer.IsEffective);

        viewModel.EqualizerEnabled = false;
        Assert.Equal(AudioEqualizerSettings.Disabled, harness.Player.Equalizer);
    }

    [Fact]
    public async Task VisualTransforms_AreClampedAppliedAndReset()
    {
        await using var harness = await PlayerViewModelHarness.StartAsync(new PlayerStartupOptions { VisualTransformDemo = true });
        var viewModel = harness.ViewModel;
        Assert.Equal("ACTIVE", viewModel.BaseVisualTransformState);
        Assert.Equal(30f, harness.Player.BaseVisualTransform.HueDegrees);

        viewModel.Contrast = 9;
        viewModel.HueDegrees = double.NaN;
        Assert.Equal(1.5, viewModel.Contrast);
        Assert.Equal(0, viewModel.HueDegrees);
        Assert.Equal(1.5f, harness.Player.BaseVisualTransform.Contrast);

        viewModel.ResetBaseVisualTransformCommand.Execute(null);
        Assert.Equal("IDENTITY", viewModel.BaseVisualTransformState);
        Assert.True(harness.Player.BaseVisualTransform.IsIdentity);
    }

    [Fact]
    public async Task NetworkUrl_EnablesOpenForHttpAndHttpsOnly()
    {
        await using var harness = await PlayerViewModelHarness.StartAsync();
        var viewModel = harness.ViewModel;

        viewModel.NetworkUrl = "ftp://example.com/a.mp3";
        Assert.False(viewModel.OpenUrlCommand.CanExecute(null));
        viewModel.NetworkUrl = " https://example.com/live.m3u8 ";
        Assert.True(viewModel.OpenUrlCommand.CanExecute(null));
        Assert.True(PlayerViewModel.TryParseNetworkUrl("http://example.com/a.mp4", out var uri));
        Assert.Equal("example.com", uri.Host);
        Assert.False(PlayerViewModel.TryParseNetworkUrl("song.mp3", out _));
    }

    [Fact]
    public async Task ChooseNext_PreparesThePickedFile()
    {
        await using var harness = await PlayerViewModelHarness.StartAsync();
        harness.FilePicker.Result = "next.mp3";

        await harness.ViewModel.ChooseNextCommand.ExecuteAsync(null);

        Assert.Equal("Choose the next item", Assert.Single(harness.FilePicker.Requests).Title);
        await harness.Dispatcher.UntilAsync(() => harness.ViewModel.DiagnosticLog.Any(static entry =>
            entry.Category == "PreparedNext" && entry.Message.Contains("next.mp3", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Subtitles_OnlyOffWithoutTracks()
    {
        await using var harness = await PlayerViewModelHarness.StartAsync();
        await harness.ViewModel.LoadMediaAsync("song.mp3");
        await harness.Player.FlushControlOperationsAsync();
        _ = harness.Dispatcher.RunPending();

        var off = Assert.Single(harness.ViewModel.SubtitleChoices);
        Assert.Null(off.TrackId);
        Assert.False(harness.ViewModel.HasSubtitleChoices);
        Assert.Same(off, harness.ViewModel.SelectedSubtitle);
    }

    [Fact]
    public async Task DisposeAsync_DisposesTheEngineAndStopsListening()
    {
        var harness = await PlayerViewModelHarness.StartAsync();
        var player = harness.Player;
        var viewModel = harness.ViewModel;
        var entries = viewModel.DiagnosticLog.Count;

        await viewModel.DisposeAsync();
        await viewModel.DisposeAsync();

        Assert.Equal(1, harness.Engine.Engine.DisposeCount);
        Assert.Null(viewModel.Player);
        Assert.False(viewModel.PlayCommand.CanExecute(null));
        Assert.False(harness.PositionTimer.IsRunning);
        Assert.True(harness.PositionTimer.IsDisposed);
        _ = harness.Dispatcher.RunPending();
        Assert.Equal(entries, viewModel.DiagnosticLog.Count);
        Assert.Throws<ObjectDisposedException>(() => player.Play());
    }

    [Fact]
    public async Task NetworkUrl_AtStartupLoadsTheStream()
    {
        var uri = new Uri("https://example.com/live.m3u8");
        await using var harness = await PlayerViewModelHarness.StartAsync(new PlayerStartupOptions { NetworkUri = uri });

        Assert.Equal(uri.AbsoluteUri, harness.ViewModel.NetworkUrl);
        await harness.Dispatcher.UntilAsync(() => harness.ViewModel.LoadedMediaText == uri.ToString()
            && harness.ViewModel.DiagnosticLog.Any(static entry => entry.Category == "Network"));
    }

    [Fact]
    public async Task AddLogEntry_LogsHostLinesOnTheUiThread()
    {
        await using var harness = new PlayerViewModelHarness();

        harness.ViewModel.AddLogEntry(MediaDiagnosticLevel.Info, "Surface", "presents via frame sink");
        Assert.Empty(harness.ViewModel.DiagnosticLog);
        _ = harness.Dispatcher.RunPending();

        var entry = Assert.Single(harness.ViewModel.DiagnosticLog);
        Assert.Equal("Surface", entry.Category);
        Assert.EndsWith("[Info] Surface: presents via frame sink", entry.Line);
    }

    [Theory]
    [InlineData(0, "0:00")]
    [InlineData(59.9, "0:59")]
    [InlineData(61, "1:01")]
    [InlineData(3600, "1:00:00")]
    [InlineData(double.NaN, "0:00")]
    [InlineData(-5, "0:00")]
    public void FormatTime_ShowsMinutesOrHours(double seconds, string expected) =>
        Assert.Equal(expected, PlayerViewModel.FormatTime(seconds));
}
