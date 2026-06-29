using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FatzBombMedia.MediaControl;
using FatzBombMedia.MediaControl.Core.Configuration;
using FatzBombMedia.MediaControl.Wpf;
using FatzBombMedia.Player.Configuration;
using System.Windows.Media;
using System.Windows.Threading;

using FatzMediaPlayer = FatzBombMedia.MediaControl.Wpf.Player;

namespace FatzBombMedia.Player.ViewModels;

public sealed class MainWindowViewModel : ObservableObject, IDisposable
{
    private readonly DispatcherTimer _positionTimer;
    private bool _isSeekPreviewActive;
    private DateTime _suppressPositionRefresh = DateTime.MinValue;
    private bool _disposed;
    private static readonly TimeSpan SeekRefreshHold = TimeSpan.FromMilliseconds(1000);

    public MainWindowViewModel()
    {
        NativeLibraryPaths = PlayerNativeLibraries.ResolveSearchPaths();
        LoadMediaCommand = new AsyncRelayCommand<string?>(LoadMediaAsync, CanLoadMedia);
        PlayCommand = new RelayCommand(Play, CanUsePlayer);
        PauseCommand = new RelayCommand(Pause, CanUsePlayer);
        StopCommand = new RelayCommand(Stop, CanUsePlayer);
        _positionTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(200)
        };
        _positionTimer.Tick += PositionTimer_Tick;

        try
        {
            var nativeLibraryPath = NativeLibraryPaths.Count > 0 ? NativeLibraryPaths[0] : string.Empty;
            DLLPath.Bass = nativeLibraryPath;
            DLLPath.FFmpeg = nativeLibraryPath;

            var diagnosticsConfig = new MediaPlayerConfiguration(VideoDecodingMethod.Hardware, RenderingMethod.DirectX11)
            {
                EnableLogging = true,
                EnablePerformanceLogging = true,
                EnableVerboseLogging = false,
                UiSharedSurfacePresentationMode = UiSharedSurfacePresentationMode.IndependentPresentationLoop,
                UiSharedSurfaceSizingMode = UiSharedSurfaceSizingMode.SourceNative
            };

            var player = new FatzMediaPlayer(diagnosticsConfig, NativeLibraryPaths)
            {
                Normalize = Normalize,
                VideoDisplayMode = VideoDisplayMode
            };
            player.Effects.Volume = Volume;
            player.Effects.Pitch = Pitch;
            player.Effects.Tempo = Tempo;
            player.PlayOnLoad = AutoPlay;
            player.LoopMedia = LoopPlayback;
            player.CdgVisuals.BackgroundOpacity = (byte)CdgBackgroundOpacity;
            Player = player;
            StatusText = $"Native DLL path: {string.Join("; ", NativeLibraryPaths)} | Presentation: {PresentationMode}";
        }
        catch (Exception ex)
        {
            StartupError = ex;
            StatusText = string.IsNullOrWhiteSpace(FatzMediaPlayer.LastNativeLoadMessage)
                ? $"Native DLLs unavailable: {ex.Message}"
                : $"Native DLLs unavailable: {FatzMediaPlayer.LastNativeLoadMessage}";
        }

        RefreshCommandStates();
        _positionTimer.Start();
    }

    public FatzMediaPlayer? Player { get; }

    public Exception? StartupError { get; }

    public IReadOnlyList<string> NativeLibraryPaths { get; }

    public IReadOnlyList<BitmapScalingMode> QualityModes { get; } =
    [
        BitmapScalingMode.HighQuality,
        BitmapScalingMode.Fant,
        BitmapScalingMode.Linear,
        BitmapScalingMode.LowQuality,
        BitmapScalingMode.NearestNeighbor
    ];

    public IReadOnlyList<VideoDisplayMode> VideoDisplayModes { get; } =
    [
        VideoDisplayMode.Original,
        VideoDisplayMode.Zoom,
        VideoDisplayMode.Stretch,
        VideoDisplayMode.ZoomToFill
    ];

    public AsyncRelayCommand<string?> LoadMediaCommand { get; }

    public RelayCommand PlayCommand { get; }

    public RelayCommand PauseCommand { get; }

    public RelayCommand StopCommand { get; }

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

    public string StatusText
    {
        get;
        private set => SetProperty(ref field, value);
    } = "Native DLL path is resolved by the host app.";

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
        get;
        set
        {
            var clamped = Math.Clamp(value, -12, 12);
            if (!SetProperty(ref field, clamped)) return;
            if (Player is { } player)
                player.Effects.Pitch = field;
        }
    }

    public int Tempo
    {
        get;
        set
        {
            var clamped = Math.Clamp(value, 50, 200);
            if (!SetProperty(ref field, clamped)) return;
            if (Player is { } player)
                player.Effects.Tempo = field;
        }
    } = 100;

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

    public BitmapScalingMode BitmapScalingMode
    {
        get;
        set => SetProperty(ref field, value);
    } = BitmapScalingMode.HighQuality;

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

    public MediaPresentationMode PresentationMode => MediaPresentationMode.NativeHwndOverlay;

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
            _ = (Player?.Controls.Seek(target));
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
            StatusText = loaded ? $"Loaded: {mediaPath}" : $"Could not load: {mediaPath}";
        }
        catch (Exception ex)
        {
            StatusText = $"Load failed: {ex.Message}";
            throw;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanLoadMedia(string? filePath) => !IsBusy && Player is not null && !string.IsNullOrWhiteSpace(filePath);

    private bool CanUsePlayer() => !IsBusy && Player is not null;

    private void Play()
    {
        _ = (Player?.Play());
        RefreshPlaybackPosition();
    }

    private void Pause()
    {
        _ = (Player?.Pause());
        RefreshPlaybackPosition();
    }

    private void Stop()
    {
        _ = (Player?.Stop());
        RefreshPlaybackPosition();
    }

    private void RefreshCommandStates()
    {
        OnPropertyChanged(nameof(CanOpen));
        OnPropertyChanged(nameof(CanSeek));
        LoadMediaCommand.NotifyCanExecuteChanged();
        PlayCommand.NotifyCanExecuteChanged();
        PauseCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
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
    }

    public void CancelSeekPreview()
    {
        _isSeekPreviewActive = false;
        SuppressPositionRefresh();
    }

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

    public void Dispose()
    {
        if (_disposed) return;
        _positionTimer.Stop();
        _positionTimer.Tick -= PositionTimer_Tick;
        Player?.Dispose();
        _disposed = true;
    }
}
