using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FatzBombMedia.MediaControl;
using FatzBombMedia.MediaControl.Core.Configuration;
using FatzBombMedia.MediaControl.Wpf;
using FatzBombMedia.Player.Configuration;
using System.Windows.Media;
using System.Windows.Threading;

using FatzMediaPlayer = FatzBombMedia.MediaControl.MediaPlayer;

namespace FatzBombMedia.Player.ViewModels;

public sealed class MainWindowViewModel : ObservableObject, IDisposable
{
    private readonly DispatcherTimer _positionTimer;
    private bool _isBusy;
    private string _statusText = "Native DLL path is resolved by the host app.";
    private double _volume = 1.0;
    private int _pitch;
    private int _tempo = 100;
    private bool _normalize = true;
    private bool _autoPlay = true;
    private bool _loopPlayback;
    private bool _maintainAspect = true;
    private BitmapScalingMode _bitmapScalingMode = BitmapScalingMode.HighQuality;
    private int _cdgBackgroundOpacity = 100;
    private bool _enableOverlay = true; // kept for compatibility; sample app always uses native overlay.
    private double _positionSeconds;
    private double _seekPositionSeconds;
    private double _durationSeconds;
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
                WpfD3DImagePresentationMode = WpfD3DImagePresentationMode.IndependentPresentationLoop,
                WpfD3DImageSurfaceSizingMode = WpfD3DImageSurfaceSizingMode.SourceNative
            };

            var player = new FatzMediaPlayer(diagnosticsConfig, NativeLibraryPaths)
            {
                Normalize = _normalize
            };
            player.Effects.Volume = _volume;
            player.Effects.Pitch = _pitch;
            player.Effects.Tempo = _tempo;
            player.PlayOnLoad = _autoPlay;
            player.LoopMedia = _loopPlayback;
            player.CdgVisuals.BackgroundOpacity = (byte)_cdgBackgroundOpacity;
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

    public AsyncRelayCommand<string?> LoadMediaCommand { get; }

    public RelayCommand PlayCommand { get; }

    public RelayCommand PauseCommand { get; }

    public RelayCommand StopCommand { get; }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value)) return;
            RefreshCommandStates();
        }
    }

    public bool CanOpen => !IsBusy && Player is not null;

    public bool CanSeek => Player is not null && DurationSeconds > 0;

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public double Volume
    {
        get => _volume;
        set
        {
            var clamped = Math.Clamp(value, 0.0, 1.0);
            if (!SetProperty(ref _volume, clamped)) return;
            if (Player is { } player)
                player.Effects.Volume = _volume;
        }
    }

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
        get => _normalize;
        set
        {
            if (!SetProperty(ref _normalize, value)) return;
            if (Player is { } player)
                player.Normalize = _normalize;
        }
    }

    public bool AutoPlay
    {
        get => _autoPlay;
        set
        {
            if (!SetProperty(ref _autoPlay, value)) return;
            if (Player is { } player)
                player.PlayOnLoad = _autoPlay;
        }
    }

    public bool LoopPlayback
    {
        get => _loopPlayback;
        set
        {
            if (!SetProperty(ref _loopPlayback, value)) return;
            if (Player is { } player)
                player.LoopMedia = _loopPlayback;
        }
    }

    public bool MaintainAspect
    {
        get => _maintainAspect;
        set
        {
            if (!SetProperty(ref _maintainAspect, value)) return;
            OnPropertyChanged(nameof(MediaStretch));
        }
    }

    public Stretch MediaStretch => MaintainAspect ? Stretch.Uniform : Stretch.Fill;

    public BitmapScalingMode BitmapScalingMode
    {
        get => _bitmapScalingMode;
        set => SetProperty(ref _bitmapScalingMode, value);
    }

    public int CdgBackgroundOpacity
    {
        get => _cdgBackgroundOpacity;
        set
        {
            var clamped = Math.Clamp(value, 0, 100);
            if (!SetProperty(ref _cdgBackgroundOpacity, clamped)) return;
            if (Player is { } player)
                player.CdgVisuals.BackgroundOpacity = (byte)_cdgBackgroundOpacity;
        }
    }

    /// <summary>
    /// Legacy compatibility property. The sample player no longer exposes renderer mode selection;
    /// it always uses NativeHwndOverlay so the test path matches the product path.
    /// </summary>
    public bool EnableOverlay
    {
        get => true;
        set
        {
            if (!SetProperty(ref _enableOverlay, true)) return;
            OnPropertyChanged(nameof(PresentationMode));
        }
    }

    public MediaPresentationMode PresentationMode => MediaPresentationMode.NativeHwndOverlay;

    public double PositionSeconds
    {
        get => _positionSeconds;
        private set
        {
            if (!SetProperty(ref _positionSeconds, CleanSeconds(value))) return;
            OnPropertyChanged(nameof(ElapsedText));
            OnPropertyChanged(nameof(RemainingText));
        }
    }

    public double SeekPositionSeconds
    {
        get => _seekPositionSeconds;
        set
        {
            var target = DurationSeconds > 0 ? Math.Clamp(CleanSeconds(value), 0, DurationSeconds) : CleanSeconds(value);

            if (!SetProperty(ref _seekPositionSeconds, target) || !_isSeekPreviewActive)
                return;            

            PositionSeconds = target;
            SuppressPositionRefresh();
            Player?.Controls.Seek(target);
        }
    }

    public double DurationSeconds
    {
        get => _durationSeconds;
        private set
        {
            if (!SetProperty(ref _durationSeconds, CleanSeconds(value))) return;
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
            var player = Player;
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
        Player?.Play();
        RefreshPlaybackPosition();
    }

    private void Pause()
    {
        Player?.Pause();
        RefreshPlaybackPosition();
    }

    private void Stop()
    {
        Player?.Stop();
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
