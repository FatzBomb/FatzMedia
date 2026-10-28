using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FatzBomb.MediaControl.Core.Abstractions;
using FatzBomb.MediaControl.Core.Configuration;
using FatzBomb.MediaControl.Player.Shared.Hosting;

namespace FatzBomb.MediaControl.Player.Shared.ViewModels;

/// <summary>The test players' flyout panels (one open at a time).</summary>
public enum PlayerFlyout
{
    None,
    Sync,
    Video,
    Audio,
    AV,
    Next,
    Log
}

/// <summary>
/// One test player's state and commands, shared by every host (WPF, Avalonia, WinForms): engine
/// creation, transport, seek scrubbing, sync offsets, effects, equalizer, subtitles, visual
/// transforms, prepared next and fades, network sources and the diagnostics log. Written against
/// the Core <see cref="MediaControlPlayer"/>; the host supplies its UI thread, timer, clipboard,
/// file picker and engine through <see cref="PlayerHost"/>. Every property changes on the UI
/// thread: engine events are marshalled there.
/// </summary>
public partial class PlayerViewModel : ObservableObject, IDisposable, IAsyncDisposable
{
    private static readonly TimeSpan SeekRefreshHold = TimeSpan.FromMilliseconds(1000);
    // Lets the surface bind to the new player before the startup media loads.
    private static readonly TimeSpan StartupMediaDelay = TimeSpan.FromMilliseconds(500);

    private readonly PlayerHost _host;
    private readonly IPlayerTimer _positionTimer;
    private readonly Lock _startGate = new();
    private Task? _startTask;
    // Cancelled when the view model is disposed, so a close right after start does not wait out
    // the startup media delay.
    private readonly CancellationTokenSource _disposing = new();
    private bool _isSeekPreviewActive;
    private DateTime _suppressPositionRefresh = DateTime.MinValue;
    private int _pitch;
    private int _tempo = 100;
    private bool _disposed;

    public PlayerViewModel(PlayerStartupOptions options, PlayerHost host)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(host);
        Options = options;
        _host = host;
        NativeLibraryPaths = host.Engine.NativeLibraryPaths;
        LoadMediaCommand = new AsyncRelayCommand<string?>(LoadMediaAsync, CanLoadMedia);
        OpenFileCommand = new AsyncRelayCommand(OpenFileAsync, () => CanOpen);
        PlayCommand = new AsyncRelayCommand(PlayAsync, CanUsePlayer);
        TogglePlayPauseCommand = new AsyncRelayCommand(TogglePlayPauseAsync, CanUsePlayer);
        PauseCommand = new AsyncRelayCommand(PauseAsync, CanUsePlayer);
        StopCommand = new AsyncRelayCommand(StopAsync, CanUsePlayer);
        ToggleLoopCommand = new RelayCommand(() => LoopPlayback = !LoopPlayback);
        AdjustSyncCommand = new RelayCommand<string>(AdjustSync, _ => CanUseSync);
        ResetSyncCommand = new RelayCommand(ResetSync, () => CanUseSync);
        ToggleFlyoutCommand = new RelayCommand<string?>(ToggleFlyout);
        InitializeValidationControls();
        InitializeEqualizerControls();
        InitializePreparedNextControls();
        InitializeNetworkControls();
        InitializeModeControls();

        _positionTimer = host.Dispatcher.CreateTimer(PositionRefreshInterval, OnPositionTimerTick);
        RefreshCommandStates();
    }

    /// <summary>Gets the command-line options this player started with.</summary>
    public PlayerStartupOptions Options { get; }

    /// <summary>
    /// Gets or sets a short name for this player (for example "P2" when a host shows several),
    /// put before its lines in the debug log file; null for none.
    /// </summary>
    public string? Label { get; set; }

    /// <summary>Gets what is loaded: the file name, or the URL of a network source.</summary>
    [ObservableProperty]
    public partial string LoadedMediaText { get; private set; } = "(no media)";

    /// <summary>Gets the engine, once <see cref="StartAsync"/> created it; bind the host's surface to it.</summary>
    public MediaControlPlayer? Player
    {
        get;
        private set
        {
            if (!SetProperty(ref field, value)) return;
            _frameCounter.Track(value);
            RefreshCommandStates();
        }
    }

    [ObservableProperty]
    public partial Exception? StartupError { get; private set; }

    public IReadOnlyList<string> NativeLibraryPaths { get; }

    public IReadOnlyList<VideoDisplayMode> VideoDisplayModes { get; } =
    [
        VideoDisplayMode.Original,
        VideoDisplayMode.Zoom,
        VideoDisplayMode.Stretch,
        VideoDisplayMode.ZoomToFill
    ];

    public AsyncRelayCommand<string?> LoadMediaCommand { get; }

    /// <summary>Asks the host's file picker for a media file and loads it.</summary>
    public AsyncRelayCommand OpenFileCommand { get; }

    public AsyncRelayCommand PlayCommand { get; }

    public AsyncRelayCommand PauseCommand { get; }

    /// <summary>Pauses while playing, else plays (one-button transport).</summary>
    public AsyncRelayCommand TogglePlayPauseCommand { get; }

    public AsyncRelayCommand StopCommand { get; }

    public RelayCommand ToggleLoopCommand { get; }

    public RelayCommand<string> AdjustSyncCommand { get; }

    public RelayCommand ResetSyncCommand { get; }

    public RelayCommand<string?> ToggleFlyoutCommand { get; }

    public bool IsBusy
    {
        get;
        private set
        {
            if (!SetProperty(ref field, value)) return;
            RefreshCommandStates();
        }
    }

    public bool CanOpen => !IsBusy && Player is not null;

    public bool CanSeek => Player is not null && DurationSeconds > 0;

    public bool CanUseSync => Player?.IsOpen == true;

    [ObservableProperty]
    public partial string AudioVideoSyncText { get; private set; } = "+0.0s";

    [ObservableProperty]
    public partial string CdgSyncText { get; private set; } = "+0.0s";

    [ObservableProperty]
    public partial string SubtitleSyncText { get; private set; } = "+0.0s";

    [ObservableProperty]
    public partial string StatusText { get; private set; } = "Native media engine is initializing.";

    public double Volume
    {
        get;
        set
        {
            var clamped = Math.Clamp(value, 0.0, 1.0);
            if (!SetProperty(ref field, clamped)) return;
            if (Player is { } player)
                player.Effects.Volume = field;
        }
    } = 1.0;

    public int Pitch
    {
        get => _pitch;
        set
        {
            var clamped = Math.Clamp(value, -12, 12);
            if (!SetProperty(ref _pitch, clamped)) return;
            if (Player is { } player)
                player.Effects.Pitch = _pitch;
        }
    }

    public int Tempo
    {
        get => _tempo;
        set
        {
            var clamped = Math.Clamp(value, 50, 200);
            if (!SetProperty(ref _tempo, clamped)) return;
            if (Player is { } player)
                player.Effects.Tempo = _tempo;
        }
    }

    public bool Normalize
    {
        get;
        set
        {
            if (!SetProperty(ref field, value)) return;
            if (Player is { } player)
                player.Normalize = field;
        }
    } = true;

    /// <summary>
    /// Mutes audio while the seek bar is being dragged; it plays again when the drag rests briefly
    /// or is released.
    /// </summary>
    public bool SilentDrag
    {
        get;
        set
        {
            if (!SetProperty(ref field, value)) return;
            if (Player is { } player)
                player.ScrubAudio = ToScrubAudio(field);
        }
    } = true;

    private static MediaScrubAudio ToScrubAudio(bool silentDrag) =>
        silentDrag ? MediaScrubAudio.SilentWhileMoving : MediaScrubAudio.Audible;

    /// <summary>
    /// Times video frames to each display's refreshes (Stage 017A slices 4 and 5) instead of the
    /// video's own frame times. Off by default while it is validated.
    /// </summary>
    public bool DisplayLockedTiming
    {
        get;
        set
        {
            if (!SetProperty(ref field, value)) return;
            if (Player is { } player)
                player.VideoFrameTiming = ToFrameTiming(field);
        }
    }

    private static VideoFrameTiming ToFrameTiming(bool displayLocked) =>
        displayLocked ? VideoFrameTiming.DisplayLocked : VideoFrameTiming.SourceCadence;

    public bool AutoPlay
    {
        get;
        set
        {
            if (!SetProperty(ref field, value)) return;
            if (Player is { } player)
                player.PlayOnLoad = field;
        }
    } = true;

    public bool LoopPlayback
    {
        get;
        set
        {
            if (!SetProperty(ref field, value) || _syncingFromPlayer) return;
            if (Player is { } player)
                player.LoopMedia = field;
        }
    }

    public PlayerFlyout ActiveFlyout
    {
        get;
        set
        {
            if (!SetProperty(ref field, value)) return;
            OnPropertyChanged(nameof(IsSyncFlyoutActive));
            OnPropertyChanged(nameof(IsVideoFlyoutActive));
            OnPropertyChanged(nameof(IsAudioFlyoutActive));
            OnPropertyChanged(nameof(IsAVFlyoutActive));
            OnPropertyChanged(nameof(IsNextFlyoutActive));
            OnPropertyChanged(nameof(IsLogFlyoutActive));
        }
    }

    public bool IsSyncFlyoutActive => ActiveFlyout == PlayerFlyout.Sync;

    public bool IsVideoFlyoutActive => ActiveFlyout == PlayerFlyout.Video;

    public bool IsAudioFlyoutActive => ActiveFlyout == PlayerFlyout.Audio;

    public bool IsAVFlyoutActive => ActiveFlyout == PlayerFlyout.AV;

    public bool IsNextFlyoutActive => ActiveFlyout == PlayerFlyout.Next;

    public bool IsLogFlyoutActive => ActiveFlyout == PlayerFlyout.Log;

    public VideoDisplayMode VideoDisplayMode
    {
        get;
        set
        {
            if (!SetProperty(ref field, value)) return;
            if (Player is { } player)
                player.VideoDisplayMode = field;
        }
    } = VideoDisplayMode.Zoom;

    public int CdgBackgroundOpacity
    {
        get;
        set
        {
            var clamped = Math.Clamp(value, 0, 100);
            if (!SetProperty(ref field, clamped)) return;
            if (Player is { } player)
                player.CdgVisuals.BackgroundOpacity = (byte)field;
        }
    } = 100;

    public double PositionSeconds
    {
        get;
        private set
        {
            if (!SetProperty(ref field, CleanSeconds(value))) return;
            OnPropertyChanged(nameof(ElapsedText));
            OnPropertyChanged(nameof(RemainingText));
        }
    }

    public double DurationSeconds
    {
        get;
        private set
        {
            if (!SetProperty(ref field, CleanSeconds(value))) return;
            OnPropertyChanged(nameof(CanSeek));
            OnPropertyChanged(nameof(ElapsedText));
            OnPropertyChanged(nameof(RemainingText));
        }
    }

    public string ElapsedText => FormatTime(PositionSeconds);

    public string RemainingText => DurationSeconds > 0 ? $"-{FormatTime(Math.Max(0, DurationSeconds - PositionSeconds))}" : "-0:00";

    /// <summary>
    /// Creates the engine (once; later calls return the same task), then loads the startup media
    /// (<see cref="PlayerStartupOptions.MediaPath"/>). Call it on the UI thread after the view is
    /// bound. A failure does not throw: <see cref="StartupError"/> and the status line say why.
    /// </summary>
    public Task StartAsync()
    {
        lock (_startGate)
            return _startTask ??= StartCoreAsync();
    }

    private async Task StartCoreAsync()
    {
        _positionTimer.Start();
        await InitializePlayerAsync();
        if (Player is null || Options.MediaPath is not { } path)
            return;

        try
        {
            await Task.Delay(StartupMediaDelay, _disposing.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (!_disposed && Player is not null)
            await LoadMediaAsync(path);
    }

    public async Task LoadMediaAsync(string? filePath)
    {
        if (!CanLoadMedia(filePath)) return;
        var mediaPath = filePath!;

        try
        {
            IsBusy = true;
            StatusText = $"Loading: {mediaPath}";
            MediaControlPlayer? player = Player;
            var loaded = player is not null && await player.LoadAsync(mediaPath);
            if (loaded)
                LoadedMediaText = Path.GetFileName(mediaPath) is { Length: > 0 } name ? name : mediaPath;
            RefreshPlaybackPosition();
            RefreshSyncOffsets();
            RunOnUiThread(RefreshEffectValues);
            StatusText = loaded ? $"Loaded: {mediaPath}" : $"Could not load: {mediaPath}";
        }
        catch (Exception ex)
        {
            StatusText = $"Load failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task OpenFileAsync()
    {
        if (_host.FilePicker is not { } picker)
        {
            StatusText = "No file picker is available.";
            return;
        }

        string? path;
        try
        {
            path = await picker.PickFileAsync("Open media file", Player?.OpenFileDialogFilter ?? "All files|*.*");
        }
        catch (Exception ex)
        {
            StatusText = $"Open failed: {ex.Message}";
            return;
        }

        if (path is not null)
            await LoadMediaAsync(path);
    }

    /// <summary>
    /// Called once the host chose the renderer, before the engine is created (on the UI thread).
    /// </summary>
    protected virtual void OnRendererResolved(PlayerRendererInfo renderer)
    {
    }

    /// <summary>Adds host-specific parts to the startup status line (for example the presentation mode), or null.</summary>
    protected virtual string? DescribeHostStartup() => null;

    private async Task InitializePlayerAsync()
    {
        MediaControlPlayer? createdPlayer = null;
        var creationStarted = false;
        try
        {
            IsBusy = true;
            StatusText = "Initializing native media engine...";

            PlayerRendererInfo renderer = await _host.Engine.ResolveRendererAsync();
            if (_disposed)
                return;

            OnRendererResolved(renderer);
            var configuration = new MediaPlayerConfiguration(renderer.VideoDecoding)
            {
                Mode = PlayerMode,
                EnableLogging = true,
                EnablePerformanceLogging = true,
                EnableVerboseLogging = false,
                ResetPitchOnMediaLoad = Options.ResetPitchOnLoad,
                ResetTempoOnMediaLoad = Options.ResetTempoOnLoad,
                SilenceTrim = new SilenceTrimOptions { Enabled = Options.TrimSilence },
                NetworkRetry = new NetworkRetryPolicy { MaxRetries = Options.NetworkRetries },
                NetworkTls = new NetworkTlsPolicy
                {
                    AllowInvalidCertificates = Options.NetworkAllowInvalidCertificates,
                    CertificateAuthorityFile = Options.NetworkCaFile,
                },
                HdrToneMapping = new HdrToneMappingOptions(
                    Disabled: Options.DisableHdrToneMapping,
                    PreferCpu: Options.PreferCpuHdrToneMapping)
            };

            // Logged before the engine starts, so they are there even when native loading fails.
            foreach ((MediaDiagnosticLevel level, string message) in renderer.LogLines)
                HandleDiagnosticOnUiThread(level, "Renderer", message);

            creationStarted = true;
            MediaControlPlayer player = await _host.Engine.CreatePlayerAsync(renderer, configuration);
            createdPlayer = player;
            if (_disposed)
            {
                // Closed while the engine was starting: dispose it off the UI thread.
                await Task.Run(player.Dispose);
                return;
            }

            player.Normalize = Normalize;
            player.VideoDisplayMode = VideoDisplayMode;
            player.Effects.Volume = Volume;
            player.Effects.Pitch = Pitch;
            player.Effects.Tempo = Tempo;
            player.PlayOnLoad = AutoPlay;
            player.ScrubAudio = ToScrubAudio(SilentDrag);
            player.VideoFrameTiming = ToFrameTiming(DisplayLockedTiming);
            player.LoopMedia = LoopPlayback;
            player.ShowMidiLyrics = true;
            player.Equalizer = BuildEqualizerSettings();
            player.CdgVisuals.BackgroundOpacity = (byte)CdgBackgroundOpacity;
            ApplyValidationControls(player);
            // Subscribed before the surface binds to the player: attaching it creates the
            // renderer device, whose diagnostic (which GPU or WARP) was lost when subscribed after.
            player.MediaDiagnostic += OnPlayerDiagnostic;
            player.MediaError += OnPlayerError;
            StartSubtitleTracking(player);
            Player = player;
            // Background mode forces looping: show what the engine reports.
            SyncFromPlayer(player);
            RefreshSyncOffsets();
            player.PitchChanged += OnPlayerPitchChanged;
            player.TempoChanged += OnPlayerTempoChanged;
            StartModeTracking(player);
            StartPreparedNextValidation(player);
            StartNetworkValidation(player);
            if (player.LastNativeLoadMessage.Length > 0)
                HandleDiagnostic(MediaDiagnosticLevel.Info, "NativeLibraries", player.LastNativeLoadMessage);
            ReportNativeDependencies(player.NativeDependencies);
            var loadResetSummary = $"Load resets: Pitch={ResetPitchOnMediaLoad}, Tempo={ResetTempoOnMediaLoad}";
            var transformSummary = $"Visual transform={BaseVisualTransformState}";
            var trimSummary = $"Silence trim={Options.TrimSilence}";
            var hostSummary = DescribeHostStartup() is { Length: > 0 } host ? $" | {host}" : string.Empty;
            StatusText = $"Renderer: {renderer.Summary}{hostSummary} | {transformSummary} | {loadResetSummary} | {trimSummary} | Native DLL path: {string.Join("; ", NativeLibraryPaths)}";
        }
        catch (Exception ex)
        {
            // Setup failed before the view model took the player: nothing else would dispose it.
            if (createdPlayer is not null && !ReferenceEquals(Player, createdPlayer))
                createdPlayer.Dispose();
            StartupError = ex;
            if (creationStarted && _host.Engine.NativeDependenciesAfterFailure is { } report)
                ReportNativeDependencies(report);
            StatusText = $"Native DLLs unavailable: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            RefreshCommandStates();
        }
    }

    private bool CanLoadMedia(string? filePath) => !IsBusy && Player is not null && !string.IsNullOrWhiteSpace(filePath);

    private bool CanUsePlayer() => !IsBusy && Player is not null;

    private Task PlayAsync() =>
        RunTransportCommandAsync("Starting playback...", "Playing", "Could not start playback.", player => player.PlayAsync());

    private Task PauseAsync() =>
        RunTransportCommandAsync("Pausing...", "Paused", "Could not pause playback.", player => player.PauseAsync());

    // State is a plain read (no engine lock), unlike Position: safe on the UI thread.
    private Task TogglePlayPauseAsync() => (Player?.State ?? PlaybackState) == MediaPlayerState.Playing ? PauseAsync() : PlayAsync();

    private Task StopAsync() =>
        RunTransportCommandAsync("Stopping...", "Stopped", "Could not stop playback.", player => player.StopAsync());

    private async Task RunTransportCommandAsync(
        string workingStatus,
        string completedStatus,
        string failedStatus,
        Func<MediaControlPlayer, Task<bool>> operation)
    {
        if (Player is not { } player)
            return;

        try
        {
            IsBusy = true;
            StatusText = workingStatus;
            var success = await operation(player);
            RefreshPlaybackPosition();
            StatusText = success ? completedStatus : failedStatus;
        }
        catch (Exception ex)
        {
            StatusText = $"{failedStatus} {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void RefreshCommandStates()
    {
        OnPropertyChanged(nameof(CanOpen));
        OnPropertyChanged(nameof(CanSeek));
        OnPropertyChanged(nameof(CanUseSync));
        LoadMediaCommand.NotifyCanExecuteChanged();
        OpenFileCommand.NotifyCanExecuteChanged();
        PlayCommand.NotifyCanExecuteChanged();
        PauseCommand.NotifyCanExecuteChanged();
        TogglePlayPauseCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        AdjustSyncCommand.NotifyCanExecuteChanged();
        ResetSyncCommand.NotifyCanExecuteChanged();
        RefreshPreparedNextCommandStates();
        OpenUrlCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanSwitchMode));
    }

    private void AdjustSync(string? command)
    {
        if (Player is not { } player || string.IsNullOrWhiteSpace(command))
            return;

        TimeSpan? offset = command switch
        {
            "audio:-" => player.AdjustAudioSyncOffset(-1),
            "audio:+" => player.AdjustAudioSyncOffset(1),
            "cdg:-" => player.AdjustCdgSyncOffset(-1),
            "cdg:+" => player.AdjustCdgSyncOffset(1),
            "subtitle:-" => player.AdjustSubtitleSyncOffset(-1),
            "subtitle:+" => player.AdjustSubtitleSyncOffset(1),
            _ => null
        };

        if (offset is null)
            return;

        RefreshSyncOffsets();
        StatusText = $"{GetSyncLabel(command)} sync {FormatSyncOffset(offset.Value)}";
    }

    private void ResetSync()
    {
        Player?.ResetSyncOffsets();
        RefreshSyncOffsets();
        StatusText = "Playback sync offsets reset.";
    }

    private void ToggleFlyout(string? key)
    {
        var flyout = ParseFlyout(key);
        ActiveFlyout = ActiveFlyout == flyout ? PlayerFlyout.None : flyout;
    }

    /// <summary>Maps a flyout key ("Sync", "Video", "Audio", "AV", "Next", "Log") to its flyout; anything else is <see cref="PlayerFlyout.None"/>.</summary>
    public static PlayerFlyout ParseFlyout(string? key) => key switch
    {
        "Sync" => PlayerFlyout.Sync,
        "Video" => PlayerFlyout.Video,
        "Audio" => PlayerFlyout.Audio,
        "AV" => PlayerFlyout.AV,
        "Next" => PlayerFlyout.Next,
        "Log" => PlayerFlyout.Log,
        _ => PlayerFlyout.None
    };

    public void CloseFlyout() => ActiveFlyout = PlayerFlyout.None;

    private void RefreshSyncOffsets()
    {
        AudioVideoSyncText = FormatSyncOffset(Player?.AudioSyncOffset ?? TimeSpan.Zero);
        CdgSyncText = FormatSyncOffset(Player?.CdgSyncOffset ?? TimeSpan.Zero);
        SubtitleSyncText = FormatSyncOffset(Player?.SubtitleSyncOffset ?? TimeSpan.Zero);
        RefreshCommandStates();
    }

    private static string GetSyncLabel(string command) => command.StartsWith("audio", StringComparison.Ordinal)
        ? "A/V"
        : command.StartsWith("cdg", StringComparison.Ordinal)
            ? "CDG"
            : "Subtitle";

    /// <summary>Formats a sync offset as shown in the Sync flyout, for example "+0.1s".</summary>
    public static string FormatSyncOffset(TimeSpan offset) => $"{offset.TotalSeconds:+0.0;-0.0;0.0}s";

    private void SuppressPositionRefresh() => _suppressPositionRefresh = DateTime.UtcNow + SeekRefreshHold;

    private static double CleanSeconds(double seconds) => double.IsFinite(seconds) && seconds > 0 ? seconds : 0;

    /// <summary>Formats a time as the seek bar shows it: m:ss, or h:mm:ss from an hour.</summary>
    public static string FormatTime(double totalSeconds)
    {
        var time = TimeSpan.FromSeconds(CleanSeconds(totalSeconds));
        return time.TotalHours >= 1
            ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}"
            : $"{time.Minutes}:{time.Seconds:00}";
    }

    private void OnPlayerDiagnostic(object? sender, MediaDiagnosticEventArgs e)
        => HandleDiagnosticOnUiThread(e.Level, e.Category, e.Message);

    private void OnPlayerError(object? sender, MediaErrorEventArgs e)
        => HandleDiagnosticOnUiThread(MediaDiagnosticLevel.Error, e.Code.ToString(), e.Message);

    // Traced on the raising thread before the hop, so a frozen UI thread does not hide it from
    // the debug log (ENG-039: a GPU reset froze the UI and the log ended before it).
    private void HandleDiagnosticOnUiThread(MediaDiagnosticLevel level, string category, string message)
    {
        var entry = new DiagnosticLogEntry(DateTime.Now, level, category, message);
        TraceLogEntry(entry);
        RunOnUiThread(() => ShowDiagnostic(entry));
    }

    private void OnPlayerPitchChanged(object? sender, MediaValueChangedEventArgs<int> e)
        => RunOnUiThread(() => SetPitchFromPlayer(e.CurrentValue));

    private void OnPlayerTempoChanged(object? sender, MediaValueChangedEventArgs<int> e)
        => RunOnUiThread(() => SetTempoFromPlayer(e.CurrentValue));

    private void RefreshEffectValues()
    {
        if (Player is not { } player)
            return;

        SetPitchFromPlayer(player.Effects.Pitch);
        SetTempoFromPlayer(player.Effects.Tempo);
    }

    private void SetPitchFromPlayer(int value)
    {
        _pitch = Math.Clamp(value, -12, 12);
        OnPropertyChanged(nameof(Pitch));
    }

    private void SetTempoFromPlayer(int value)
    {
        _tempo = Math.Clamp(value, 50, 200);
        OnPropertyChanged(nameof(Tempo));
    }

    /// <summary>
    /// Runs <paramref name="update"/> on the UI thread: at once when already there, else posted
    /// (and skipped when the view model was disposed meanwhile). Engine events use this.
    /// </summary>
    protected void RunOnUiThread(Action update)
    {
        if (_disposed)
            return;

        IPlayerDispatcher dispatcher = _host.Dispatcher;
        if (dispatcher.CheckAccess())
        {
            update();
            return;
        }

        dispatcher.Post(() =>
        {
            if (!_disposed)
                update();
        });
    }

    /// <summary>
    /// Logs each native library that did not load, with its exact file name and any version hint
    /// (for example an FFmpeg 8 DLL where FFmpeg 9 is needed). Required ones are errors.
    /// </summary>
    private void ReportNativeDependencies(NativeDependencyReport report)
    {
        IReadOnlyList<string> lines = report.Describe();
        var requiredCount = report.RequiredProblems.Count;
        for (var index = 0; index < lines.Count; index++)
            HandleDiagnostic(index < requiredCount ? MediaDiagnosticLevel.Error : MediaDiagnosticLevel.Warning, "NativeLibraries", lines[index]);
    }

    /// <summary>
    /// Adds a host line to the log (for example which presentation path a surface took), from any
    /// thread; a warning or an error also shows in the status line.
    /// </summary>
    public void AddLogEntry(MediaDiagnosticLevel level, string category, string message)
        => HandleDiagnosticOnUiThread(level, category, message);

    /// <summary>Adds a line to the log (and the status line when it is a warning or an error). UI thread only.</summary>
    protected void HandleDiagnostic(MediaDiagnosticLevel level, string category, string message)
    {
        var entry = new DiagnosticLogEntry(DateTime.Now, level, category, message);
        TraceLogEntry(entry);
        ShowDiagnostic(entry);
    }

    // UI thread: the log panel and, for a warning or an error, the status line.
    private void ShowDiagnostic(DiagnosticLogEntry entry)
    {
        AppendToLog(entry);
        if (entry.Level >= MediaDiagnosticLevel.Warning)
            StatusText = $"[{entry.Level}] {entry.Category}: {entry.Message}";
    }

    /// <summary>Detaches from the engine and disposes it on the calling thread (it blocks until media operations finish).</summary>
    public void Dispose()
    {
        if (DetachPlayer() is { } player)
            player.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Detaches from the engine on the calling thread, then disposes it off it, so a UI thread is
    /// not blocked. When the engine is still starting, waits for it and disposes it too.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        Task? start;
        lock (_startGate)
            start = _startTask;
        if (DetachPlayer() is { } player)
            await Task.Run(player.Dispose).ConfigureAwait(false);
        if (start is not null)
        {
            // StartCoreAsync never throws; it disposes an engine created after this point.
            await start.ConfigureAwait(false);
        }

        GC.SuppressFinalize(this);
    }

    private MediaControlPlayer? DetachPlayer()
    {
        if (_disposed) return null;

        _disposed = true;
        _disposing.Cancel();
        _positionTimer.Stop();
        _positionTimer.Dispose();
        if (Player is not { } player)
            return null;

        player.MediaDiagnostic -= OnPlayerDiagnostic;
        player.MediaError -= OnPlayerError;
        player.PitchChanged -= OnPlayerPitchChanged;
        player.TempoChanged -= OnPlayerTempoChanged;
        StopModeTracking(player);
        StopSubtitleTracking(player);
        StopPreparedNextValidation(player);
        StopNetworkValidation(player);
        // Surfaces bound to Player let go of it, and the commands turn off, before it is disposed.
        Player = null;
        return player;
    }
}
