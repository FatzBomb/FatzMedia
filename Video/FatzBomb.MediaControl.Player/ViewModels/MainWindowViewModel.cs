using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FatzBomb.MediaControl;
using FatzBomb.MediaControl.Core.Configuration;
using FatzBomb.MediaControl.Rendering.Software;
using FatzBomb.MediaControl.Wpf;
using FatzBomb.MediaControl.Player.Configuration;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;

using FatzMediaPlayer = FatzBomb.MediaControl.Wpf.Player;

namespace FatzBomb.MediaControl.Player.ViewModels;

public enum PlayerFlyout
{
    None,
    Sync,
    Video,
    Audio,
    AV
}

public sealed partial class MainWindowViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan SeekRefreshHold = TimeSpan.FromMilliseconds(1000);

    private readonly DispatcherTimer _positionTimer;
    private bool _isSeekPreviewActive;
    private DateTime _suppressPositionRefresh = DateTime.MinValue;
    private int _pitch;
    private int _tempo = 100;
    private bool _disposed;

    public MainWindowViewModel()
    {
        NativeLibraryPaths = PlayerNativeLibraries.ResolveSearchPaths();
        LoadMediaCommand = new AsyncRelayCommand<string?>(LoadMediaAsync, CanLoadMedia);
        PlayCommand = new AsyncRelayCommand(PlayAsync, CanUsePlayer);
        PauseCommand = new AsyncRelayCommand(PauseAsync, CanUsePlayer);
        StopCommand = new AsyncRelayCommand(StopAsync, CanUsePlayer);
        ToggleLoopCommand = new RelayCommand(() => LoopPlayback = !LoopPlayback);
        AdjustSyncCommand = new RelayCommand<string>(AdjustSync, _ => CanUseSync);
        ResetSyncCommand = new RelayCommand(ResetSync, () => CanUseSync);
        ToggleFlyoutCommand = new RelayCommand<string?>(ToggleFlyout);
        InitializeValidationControls();

        _positionTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(200)
        };
        _positionTimer.Tick += PositionTimer_Tick;

        _ = InitializePlayerAsync();
        RefreshCommandStates();
        _positionTimer.Start();
    }

    public FatzMediaPlayer? Player
    {
        get;
        private set
        {
            if (!SetProperty(ref field, value)) return;
            RefreshCommandStates();
            OnPropertyChanged(nameof(CanChangePresentationMode));
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

    public IReadOnlyList<WpfPresentationMode> PresentationModes { get; } =
        App.UseSoftwareRenderer
            ? [WpfPresentationMode.WpfComposited]
            :
            [
                WpfPresentationMode.NativeHwndOverlay,
                WpfPresentationMode.WpfComposited
            ];

    public AsyncRelayCommand<string?> LoadMediaCommand { get; }

    public AsyncRelayCommand PlayCommand { get; }

    public AsyncRelayCommand PauseCommand { get; }

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
            OnPropertyChanged(nameof(CanChangePresentationMode));
        }
    }

    public bool CanOpen => !IsBusy && Player is not null;

    public bool CanSeek => Player is not null && DurationSeconds > 0;

    public bool CanUseSync => Player?.IsOpen == true;

    public bool CanChangePresentationMode => !App.UseSoftwareRenderer && !IsBusy && Player?.IsOpen != true;

    [ObservableProperty]
    public partial string AudioVideoSyncText { get; private set; } = "+0.0s";

    [ObservableProperty]
    public partial string CdgSyncText { get; private set; } = "+0.0s";

    [ObservableProperty]
    public partial string SubtitleSyncText { get; private set; } = "+0.0s";

    [ObservableProperty]
    public partial string StatusText { get; private set; } = "Native media engine is initializing.";

    public ObservableCollection<DiagnosticLogEntry> DiagnosticLog { get; } = [];

    private static void AppendToLog(ObservableCollection<DiagnosticLogEntry> log, DiagnosticLogEntry entry)
    {
        log.Add(entry);
        while (log.Count > 200)
            log.RemoveAt(0);
    }

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
            if (!SetProperty(ref field, value)) return;
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
        }
    }

    public bool IsSyncFlyoutActive => ActiveFlyout == PlayerFlyout.Sync;

    public bool IsVideoFlyoutActive => ActiveFlyout == PlayerFlyout.Video;

    public bool IsAudioFlyoutActive => ActiveFlyout == PlayerFlyout.Audio;

    public bool IsAVFlyoutActive => ActiveFlyout == PlayerFlyout.AV;

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

    public WpfPresentationMode PresentationMode
    {
        get;
        set
        {
            if (Player?.IsOpen == true)
                throw new InvalidOperationException("PresentationMode can only be changed before media is loaded.");

            _ = SetProperty(ref field, value);
        }
    } = App.UseSoftwareRenderer
        ? WpfPresentationMode.WpfComposited
        : WpfPresentationMode.NativeHwndOverlay;

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

    public double SeekPositionSeconds
    {
        get;
        set
        {
            var target = DurationSeconds > 0 ? Math.Clamp(CleanSeconds(value), 0, DurationSeconds) : CleanSeconds(value);

            if (!SetProperty(ref field, target) || !_isSeekPreviewActive)
                return;

            PositionSeconds = target;
            SuppressPositionRefresh();
            _ = Player?.Controls.Seek(target);
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

    public async Task LoadMediaAsync(string? filePath)
    {
        if (!CanLoadMedia(filePath)) return;
        var mediaPath = filePath!;

        try
        {
            IsBusy = true;
            StatusText = $"Loading: {mediaPath}";
            FatzMediaPlayer? player = Player;
            var loaded = player is not null && await player.LoadAsync(mediaPath);
            RefreshPlaybackPosition();
            RefreshSyncOffsets();
            UpdateEffectOnUiThread(RefreshEffectValues);
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

    public void BeginSeekPreview()
    {
        if (!CanSeek) return;
        _isSeekPreviewActive = true;
    }

    public void EndSeekPreview()
    {
        _isSeekPreviewActive = false;
        SuppressPositionRefresh();
        _ = Player?.Seek(SeekPositionSeconds);
    }

    public void CancelSeekPreview()
        => EndSeekPreview();

    private async Task InitializePlayerAsync()
    {
        try
        {
            IsBusy = true;
            StatusText = "Initializing native media engine...";

            var rendererName = App.UseSoftwareRenderer ? "Software" : "Direct3D11";
            var decodingMethod = App.UseSoftwareRenderer
                ? VideoDecodingMethod.Software
                : VideoDecodingMethod.Hardware;
            var diagnosticsConfig = new MediaPlayerConfiguration(decodingMethod)
            {
                EnableLogging = true,
                EnablePerformanceLogging = true,
                EnableVerboseLogging = false,
                ResetPitchOnMediaLoad = App.ResetPitchOnMediaLoad,
                ResetTempoOnMediaLoad = App.ResetTempoOnMediaLoad,
                SilenceTrim = new SilenceTrimOptions { Enabled = App.EnableSilenceTrim }
            };

            IMediaControlPlatformFactory platformFactory = App.UseSoftwareRenderer
                ? new WindowsMediaControlPlatformFactory(new SoftwarePresentationCoordinatorFactory())
                : new WindowsDirect3D11MediaControlPlatformFactory();
            FatzMediaPlayer player = await FatzMediaPlayer.CreateAsync(
                diagnosticsConfig,
                NativeLibraryPaths,
                platformFactory);
            if (_disposed)
            {
                player.Dispose();
                return;
            }

            player.Normalize = Normalize;
            player.VideoDisplayMode = VideoDisplayMode;
            player.Effects.Volume = Volume;
            player.Effects.Pitch = Pitch;
            player.Effects.Tempo = Tempo;
            player.PlayOnLoad = AutoPlay;
            player.LoopMedia = LoopPlayback;
            player.ShowMidiLyrics = true;
            player.CdgVisuals.BackgroundOpacity = (byte)CdgBackgroundOpacity;
            ApplyValidationControls(player);
            Player = player;
            RefreshSyncOffsets();
            player.MediaDiagnostic += OnPlayerDiagnostic;
            player.MediaError += OnPlayerError;
            player.PitchChanged += OnPlayerPitchChanged;
            player.TempoChanged += OnPlayerTempoChanged;
            StartPreparedNextValidation(player);
            var loadResetSummary = $"Load resets: Pitch={ResetPitchOnMediaLoad}, Tempo={ResetTempoOnMediaLoad}";
            var transformSummary = $"Visual transform={BaseVisualTransformState}";
            var trimSummary = $"Silence trim={App.EnableSilenceTrim}";
            StatusText = $"Renderer: {rendererName} | Presentation: {PresentationMode} | AutoHide={AutoHideSurfaces} | {transformSummary} | {loadResetSummary} | {trimSummary} | Native DLL path: {string.Join("; ", NativeLibraryPaths)}";
        }
        catch (Exception ex)
        {
            StartupError = ex;
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

    private Task StopAsync() =>
        RunTransportCommandAsync("Stopping...", "Stopped", "Could not stop playback.", player => player.StopAsync());

    private async Task RunTransportCommandAsync(
        string workingStatus,
        string completedStatus,
        string failedStatus,
        Func<FatzMediaPlayer, Task<bool>> operation)
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
        PlayCommand.NotifyCanExecuteChanged();
        PauseCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        AdjustSyncCommand.NotifyCanExecuteChanged();
        ResetSyncCommand.NotifyCanExecuteChanged();
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
        var flyout = key switch
        {
            "Sync" => PlayerFlyout.Sync,
            "Video" => PlayerFlyout.Video,
            "Audio" => PlayerFlyout.Audio,
            "AV" => PlayerFlyout.AV,
            _ => PlayerFlyout.None
        };

        ActiveFlyout = ActiveFlyout == flyout ? PlayerFlyout.None : flyout;
    }

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

    private static string FormatSyncOffset(TimeSpan offset) => $"{offset.TotalSeconds:+0.0;-0.0;0.0}s";

    private void PositionTimer_Tick(object? sender, EventArgs e)
    {
        if (!_isSeekPreviewActive)
            RefreshPlaybackPosition();
    }

    private void RefreshPlaybackPosition()
    {
        if (DateTime.UtcNow < _suppressPositionRefresh) return;

        if (Player is null)
        {
            PositionSeconds = 0;
            SeekPositionSeconds = 0;
            DurationSeconds = 0;
            return;
        }

        var duration = Player.Controls.Duration.TotalSeconds;
        var position = Player.Controls.Position.TotalSeconds;

        DurationSeconds = duration;
        PositionSeconds = DurationSeconds > 0 ? Math.Clamp(position, 0, DurationSeconds) : CleanSeconds(position);
        SeekPositionSeconds = PositionSeconds;
    }

    private void SuppressPositionRefresh() => _suppressPositionRefresh = DateTime.UtcNow + SeekRefreshHold;

    private static double CleanSeconds(double seconds) => double.IsFinite(seconds) && seconds > 0 ? seconds : 0;

    private static string FormatTime(double totalSeconds)
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

    private void HandleDiagnosticOnUiThread(MediaDiagnosticLevel level, string category, string message)
    {
        if (_disposed)
            return;

        Dispatcher? dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess())
        {
            HandleDiagnostic(level, category, message);
            return;
        }

        _ = dispatcher.BeginInvoke(() =>
        {
            if (!_disposed)
                HandleDiagnostic(level, category, message);
        });
    }

    private void OnPlayerPitchChanged(object? sender, MediaValueChangedEventArgs<int> e)
    {
        UpdateEffectOnUiThread(() => SetPitchFromPlayer(e.CurrentValue));
    }

    private void OnPlayerTempoChanged(object? sender, MediaValueChangedEventArgs<int> e)
    {
        UpdateEffectOnUiThread(() => SetTempoFromPlayer(e.CurrentValue));
    }

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

    private void UpdateEffectOnUiThread(Action update)
    {
        if (_disposed)
            return;

        Dispatcher? dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess())
        {
            update();
            return;
        }

        _ = dispatcher.BeginInvoke(() =>
        {
            if (!_disposed)
                update();
        });
    }

    private void HandleDiagnostic(MediaDiagnosticLevel level, string category, string message)
    {
        var entry = new DiagnosticLogEntry(DateTime.Now, level, category, message);
        AppendToLog(DiagnosticLog, entry);

        if (level >= MediaDiagnosticLevel.Warning)
            StatusText = $"[{level}] {category}: {message}";
    }

    public void Dispose()
    {
        if (_disposed) return;

        _disposed = true;
        _positionTimer.Stop();
        _positionTimer.Tick -= PositionTimer_Tick;
        if (Player is { } player)
        {
            player.MediaDiagnostic -= OnPlayerDiagnostic;
            player.MediaError -= OnPlayerError;
            player.PitchChanged -= OnPlayerPitchChanged;
            player.TempoChanged -= OnPlayerTempoChanged;
            StopPreparedNextValidation(player);
            player.Dispose();
        }
    }
}
