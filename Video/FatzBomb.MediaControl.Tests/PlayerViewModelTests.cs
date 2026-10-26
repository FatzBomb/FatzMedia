using FatzBomb.MediaControl.Core.Abstractions;
using FatzBomb.MediaControl.Core.Configuration;
using FatzBomb.MediaControl.Player.Shared;
using FatzBomb.MediaControl.Player.Shared.ViewModels;

namespace FatzBomb.MediaControl.Tests;

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
        await using PlayerViewModelHarness harness = await PlayerViewModelHarness.StartAsync(options);
        PlayerViewModel viewModel = harness.ViewModel;
        MediaControlPlayer player = harness.Player;

        Assert.Null(viewModel.StartupError);
        Assert.False(viewModel.IsBusy);
        Assert.True(viewModel.CanOpen);
        Assert.True(harness.PositionTimer.IsRunning);
        MediaPlayerConfiguration configuration = Assert.NotNull(harness.Engine.Configuration);
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
        await using PlayerViewModelHarness harness = await PlayerViewModelHarness.StartAsync(engine: engine);
        PlayerViewModel viewModel = harness.ViewModel;

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

        Task first = harness.ViewModel.StartAsync();
        Assert.Same(first, harness.ViewModel.StartAsync());
        await first;
        Assert.Single(harness.Engine.Platform.Engines);
    }

    [Fact]
    public async Task Start_LoadsTheStartupMedia()
    {
        await using PlayerViewModelHarness harness = await PlayerViewModelHarness.StartAsync(new PlayerStartupOptions { MediaPath = "song.mp3" });

        Assert.True(harness.Player.IsOpen);
        Assert.Equal("Loaded: song.mp3", harness.ViewModel.StatusText);
    }

    [Fact]
    public async Task OpenFile_LoadsThePickedFileWithTheEngineFilter()
    {
        await using PlayerViewModelHarness harness = await PlayerViewModelHarness.StartAsync();
        harness.FilePicker.Result = "song.mp3";

        await harness.ViewModel.OpenFileCommand.ExecuteAsync(null);

        (string title, string filter) = Assert.Single(harness.FilePicker.Requests);
        Assert.Equal("Open media file", title);
        Assert.Equal(harness.Player.OpenFileDialogFilter, filter);
        Assert.True(harness.Player.IsOpen);
        Assert.Equal("Loaded: song.mp3", harness.ViewModel.StatusText);
    }

    [Fact]
    public async Task OpenFile_Cancelled_LoadsNothing()
    {
        await using PlayerViewModelHarness harness = await PlayerViewModelHarness.StartAsync();

        await harness.ViewModel.OpenFileCommand.ExecuteAsync(null);

        Assert.Single(harness.FilePicker.Requests);
        Assert.False(harness.Player.IsOpen);
    }

    [Fact]
    public async Task Transport_PlaysPausesAndStops()
    {
        await using PlayerViewModelHarness harness = await PlayerViewModelHarness.StartAsync();
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
    }

    [Fact]
    public async Task Settings_AreClampedAndForwardedToTheEngine()
    {
        await using PlayerViewModelHarness harness = await PlayerViewModelHarness.StartAsync();
        PlayerViewModel viewModel = harness.ViewModel;
        MediaControlPlayer player = harness.Player;

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
        await using PlayerViewModelHarness harness = await PlayerViewModelHarness.StartAsync();
        PlayerViewModel viewModel = harness.ViewModel;
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
        await using PlayerViewModelHarness harness = await PlayerViewModelHarness.StartAsync();
        PlayerViewModel viewModel = harness.ViewModel;
        await viewModel.LoadMediaAsync("song.mp3");
        TestMediaEngine engine = harness.Engine.Engine;
        engine.Duration = 3725;
        engine.Position = 61.6;

        harness.PositionTimer.Tick();

        Assert.Equal(3725, viewModel.DurationSeconds);
        Assert.Equal(61.6, viewModel.PositionSeconds, 6);
        Assert.Equal(61.6, viewModel.SeekPositionSeconds, 6);
        Assert.True(viewModel.CanSeek);
        Assert.Equal("1:01", viewModel.ElapsedText);
        Assert.Equal("-1:01:03", viewModel.RemainingText);
    }

    [Fact]
    public async Task SeekPreview_ScrubsWhileDraggingAndSeeksOnRelease()
    {
        await using PlayerViewModelHarness harness = await PlayerViewModelHarness.StartAsync();
        PlayerViewModel viewModel = harness.ViewModel;
        MediaControlPlayer player = harness.Player;
        await viewModel.LoadMediaAsync("song.mp3");
        harness.PositionTimer.Tick();
        TestMediaEngine engine = harness.Engine.Engine;

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
    }

    [Fact]
    public async Task SeekPosition_WithoutAPreview_DoesNotSeek()
    {
        await using PlayerViewModelHarness harness = await PlayerViewModelHarness.StartAsync();
        PlayerViewModel viewModel = harness.ViewModel;
        await viewModel.LoadMediaAsync("song.mp3");
        harness.PositionTimer.Tick();
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
        PlayerViewModel viewModel = harness.ViewModel;

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
        await using PlayerViewModelHarness harness = await PlayerViewModelHarness.StartAsync();
        PlayerViewModel viewModel = harness.ViewModel;
        viewModel.ClearLogCommand.Execute(null);
        List<string> statuses = [];
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PlayerViewModel.StatusText))
                statuses.Add(viewModel.StatusText);
        };

        using (DiagnosticRouter.EnterOwner(harness.Player))
        {
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Info, "Unit", "first");
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Warning, "Unit", "second");
        }

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
        await using PlayerViewModelHarness harness = await PlayerViewModelHarness.StartAsync(new PlayerStartupOptions { EqualizerGains = [20, -4.44] });
        PlayerViewModel viewModel = harness.ViewModel;

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
        await using PlayerViewModelHarness harness = await PlayerViewModelHarness.StartAsync(new PlayerStartupOptions { VisualTransformDemo = true });
        PlayerViewModel viewModel = harness.ViewModel;
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
        await using PlayerViewModelHarness harness = await PlayerViewModelHarness.StartAsync();
        PlayerViewModel viewModel = harness.ViewModel;

        viewModel.NetworkUrl = "ftp://example.com/a.mp3";
        Assert.False(viewModel.OpenUrlCommand.CanExecute(null));
        viewModel.NetworkUrl = " https://example.com/live.m3u8 ";
        Assert.True(viewModel.OpenUrlCommand.CanExecute(null));
        Assert.True(PlayerViewModel.TryParseNetworkUrl("http://example.com/a.mp4", out Uri? uri));
        Assert.Equal("example.com", uri.Host);
        Assert.False(PlayerViewModel.TryParseNetworkUrl("song.mp3", out _));
    }

    [Fact]
    public async Task ChooseNext_PreparesThePickedFile()
    {
        await using PlayerViewModelHarness harness = await PlayerViewModelHarness.StartAsync();
        harness.FilePicker.Result = "next.mp3";

        await harness.ViewModel.ChooseNextCommand.ExecuteAsync(null);

        Assert.Equal("Choose the next item", Assert.Single(harness.FilePicker.Requests).Title);
        await harness.Dispatcher.UntilAsync(() => harness.ViewModel.DiagnosticLog.Any(static entry =>
            entry.Category == "PreparedNext" && entry.Message.Contains("next.mp3", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Subtitles_OnlyOffWithoutTracks()
    {
        await using PlayerViewModelHarness harness = await PlayerViewModelHarness.StartAsync();
        await harness.ViewModel.LoadMediaAsync("song.mp3");
        await harness.Player.FlushControlOperationsAsync();
        _ = harness.Dispatcher.RunPending();

        SubtitleChoice off = Assert.Single(harness.ViewModel.SubtitleChoices);
        Assert.Null(off.TrackId);
        Assert.False(harness.ViewModel.HasSubtitleChoices);
        Assert.Same(off, harness.ViewModel.SelectedSubtitle);
    }

    [Fact]
    public async Task DisposeAsync_DisposesTheEngineAndStopsListening()
    {
        PlayerViewModelHarness harness = await PlayerViewModelHarness.StartAsync();
        MediaControlPlayer player = harness.Player;
        PlayerViewModel viewModel = harness.ViewModel;
        var entries = viewModel.DiagnosticLog.Count;

        await viewModel.DisposeAsync();
        await viewModel.DisposeAsync();

        Assert.Equal(1, harness.Engine.Engine.DisposeCount);
        Assert.False(harness.PositionTimer.IsRunning);
        Assert.True(harness.PositionTimer.IsDisposed);
        _ = harness.Dispatcher.RunPending();
        Assert.Equal(entries, viewModel.DiagnosticLog.Count);
        Assert.Throws<ObjectDisposedException>(() => player.Play());
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
