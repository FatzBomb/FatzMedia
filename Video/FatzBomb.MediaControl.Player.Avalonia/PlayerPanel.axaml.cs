using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using FatzBomb.MediaControl.Avalonia;
using FatzBomb.MediaControl.Core.Abstractions;
using FatzBomb.MediaControl.Core.Configuration;

namespace FatzBomb.MediaControl.Player.Avalonia;

/// <summary>
/// One independent player: its own engine, <c>MediaSurface</c>, transport, effects, log and status
/// line. Plain code-behind; every engine call goes through the public <see cref="MediaControlPlayer"/> API.
/// </summary>
public partial class PlayerPanel : UserControl
{
    private const int MaxLogEntries = 500;

    private readonly int _index;
    private readonly RendererOption _renderer;
    private readonly IReadOnlyList<string> _nativePaths;
    private readonly ObservableCollection<string> _log = [];
    private readonly DispatcherTimer _uiTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private MediaControlPlayer? _player;
    private long _frames;
    private long _lastFrameTimestamp = Stopwatch.GetTimestamp();
    private bool _isSeeking;
    private bool _isUpdatingSeek;
    private bool _isUpdatingMode;
    private bool _isUpdatingLoop;
    private bool _disposed;

    // Designer/XAML loader constructor.
    public PlayerPanel()
        : this(1, new RendererOption(RendererKind.Software, true, string.Empty), [])
    {
    }

    internal PlayerPanel(int index, RendererOption renderer, IReadOnlyList<string> nativePaths)
    {
        _index = index;
        _renderer = renderer;
        _nativePaths = nativePaths;
        InitializeComponent();

        LogList.ItemsSource = _log;
        _isUpdatingMode = true;
        ModeBox.ItemsSource = Enum.GetValues<MediaPlayerMode>();
        ModeBox.SelectedItem = StartupOptions.Current.Mode;
        _isUpdatingMode = false;
        UpdateEffectLabels();
        SetControlsEnabled(false);

        // Seek on release (a click on the track also ends with a release); keys seek immediately.
        SeekSlider.AddHandler(PointerPressedEvent, OnSeekPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        SeekSlider.AddHandler(PointerReleasedEvent, OnSeekReleased, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        SeekSlider.PointerCaptureLost += OnSeekCaptureLost;

        // --surface=framesink forces the readback path for comparison (Stage 027A slice 2).
        Surface.PreferGpuSharedSurface = StartupOptions.Current.PreferGpuSurface;
        Surface.PresentationPathChanged += OnPresentationPathChanged;

        _uiTimer.Tick += OnUiTimerTick;
    }

    private string Label => $"P{_index}";

    /// <summary>Creates the engine (off the UI thread: it loads the native libraries) and binds the surface.</summary>
    internal async Task InitializeAsync(string? startupPath)
    {
        AddLog(MediaDiagnosticLevel.Info, "Player", $"{PlatformComposition.Name} platform, {StartupOptions.Current.Mode} mode, renderer {_renderer.Kind}: {_renderer.Detail}");
        AddLog(MediaDiagnosticLevel.Info, "Player", $"Native search paths: {(_nativePaths.Count == 0 ? "(none exist)" : string.Join("; ", _nativePaths))}");
        try
        {
            IMediaControlPlatformFactory platform = PlatformComposition.Create(Renderers.Create(_renderer.Kind));
            // Every renderer here decodes in software, so CPU comparisons isolate the presentation path.
            var configuration = new MediaPlayerConfiguration(VideoDecodingMethod.Software)
            {
                Mode = StartupOptions.Current.Mode,
                EnableLogging = true,
                EnablePerformanceLogging = true
            };

            MediaControlPlayer player = await Task.Run(() => new MediaControlPlayer(platform, configuration, _nativePaths));
            if (_disposed)
            {
                await Task.Run(player.Dispose);
                return;
            }

            // Subscribed before the surface binds so renderer device diagnostics are not missed.
            player.MediaDiagnostic += OnDiagnostic;
            player.MediaError += OnError;
            player.PresentationFramePresented += OnFramePresented;
            player.PlayOnLoad = true;
            player.LoopMedia = LoopBox.IsChecked == true;
            player.Effects.Volume = VolumeSlider.Value / 100;
            player.Effects.Pitch = (int)PitchSlider.Value;
            player.Effects.Tempo = (int)TempoSlider.Value;
            _player = player;
            Surface.Player = player;
            ShowPlayerMode(player);

            ReportNativeLibraries(player);
            SetControlsEnabled(true);
            _lastFrameTimestamp = Stopwatch.GetTimestamp();
            _uiTimer.Start();

            if (!string.IsNullOrEmpty(startupPath))
                await OpenAsync(startupPath);
        }
        catch (Exception ex)
        {
            AddLog(MediaDiagnosticLevel.Error, "Player", $"Engine unavailable: {ex.Message}");
            StatusText.Text = $"{Label}: engine unavailable ({ex.GetType().Name}); see the log";
        }
    }

    internal async Task DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        _uiTimer.Stop();
        Surface.Player = null;
        MediaControlPlayer? player = _player;
        _player = null;
        if (player is null)
            return;

        player.MediaDiagnostic -= OnDiagnostic;
        player.MediaError -= OnError;
        player.PresentationFramePresented -= OnFramePresented;
        // Dispose waits for admitted media operations: never on the UI thread.
        await Task.Run(player.Dispose);
    }

    internal void UpdateStatus(ProcessSample process)
    {
        if (_player is not { } player)
            return;

        var now = Stopwatch.GetTimestamp();
        var elapsed = Math.Max(Stopwatch.GetElapsedTime(_lastFrameTimestamp, now).TotalSeconds, 0.001);
        _lastFrameTimestamp = now;
        var fps = Interlocked.Exchange(ref _frames, 0) / elapsed;

        PixelSize size = Surface.AttachedPixelSize;
        StatusText.Text =
            $"{Label} {player.State} | {fps:F1} fps | CPU {process.CpuPercent:F0}% ({process.CoresUsed:F2} of {Environment.ProcessorCount} cores) | " +
            $"managed {process.ManagedMegabytes:F0} MB, working set {process.WorkingSetMegabytes:F0} MB (whole process) | surface {size.Width}x{size.Height} " +
            $"via {DescribePath(Surface.PresentationPath)}";
    }

    private async void OnOpenClick(object? sender, RoutedEventArgs e)
    {
        if (_player is not { } player || TopLevel.GetTopLevel(this) is not { } topLevel)
            return;

        try
        {
            IReadOnlyList<IStorageFile> files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = $"Open media (player {_index})",
                AllowMultiple = false,
                FileTypeFilter = ToFileTypes(player.OpenFileDialogFilter)
            });

            if (files.Count > 0 && files[0].TryGetLocalPath() is { } path)
                await OpenAsync(path);
        }
        catch (Exception ex)
        {
            AddLog(MediaDiagnosticLevel.Error, "Player", $"Open failed: {ex.Message}");
        }
    }

    private async Task OpenAsync(string path)
    {
        if (_player is not { } player)
            return;

        FileText.Text = Path.GetFileName(path);
        AddLog(MediaDiagnosticLevel.Info, "Player", $"Loading {path}");
        // PlayOnLoad starts playback after a successful load.
        if (!await player.LoadAsync(path))
            AddLog(MediaDiagnosticLevel.Warning, "Player", $"Could not load {path}");
    }

    private async void OnPlayPauseClick(object? sender, RoutedEventArgs e)
    {
        if (_player is not { IsOpen: true } player)
            return;

        _ = player.State == MediaPlayerState.Playing ? await player.PauseAsync() : await player.PlayAsync();
    }

    private async void OnStopClick(object? sender, RoutedEventArgs e)
    {
        if (_player is { IsOpen: true } player)
            _ = await player.StopAsync();
    }

    private void OnLoopChanged(object? sender, RoutedEventArgs e)
    {
        if (!_isUpdatingLoop && _player is { } player)
            player.LoopMedia = LoopBox.IsChecked == true;
    }

    /// <summary>Switches this player's mode on the fly (Stage 043A slice 2).</summary>
    private async void OnModeSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingMode || _player is not { } player || ModeBox.SelectedItem is not MediaPlayerMode mode || mode == player.Mode)
            return;

        ModeBox.IsEnabled = false;
        AddLog(MediaDiagnosticLevel.Info, "Player", $"Switching to {mode} mode");
        try
        {
            if (!await player.SetModeAsync(mode))
                AddLog(MediaDiagnosticLevel.Warning, "Player", $"Could not switch to {mode} mode");
        }
        catch (Exception ex)
        {
            AddLog(MediaDiagnosticLevel.Error, "Player", $"Mode switch failed: {ex.Message}");
        }
        finally
        {
            ModeBox.IsEnabled = true;
            ShowPlayerMode(player);
        }
    }

    /// <summary>Shows the player's mode and its loop setting (Background mode forces loop on).</summary>
    private void ShowPlayerMode(MediaControlPlayer player)
    {
        _isUpdatingMode = true;
        _isUpdatingLoop = true;
        try
        {
            ModeBox.SelectedItem = player.Mode;
            LoopBox.IsChecked = player.LoopMedia;
        }
        finally
        {
            _isUpdatingMode = false;
            _isUpdatingLoop = false;
        }
    }

    private void OnVolumeChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        UpdateEffectLabels();
        if (_player is { } player)
            player.Effects.Volume = VolumeSlider.Value / 100;
    }

    private void OnPitchChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        UpdateEffectLabels();
        if (_player is { } player)
            player.Effects.Pitch = (int)PitchSlider.Value;
    }

    private void OnTempoChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        UpdateEffectLabels();
        if (_player is { } player)
            player.Effects.Tempo = (int)TempoSlider.Value;
    }

    private void OnSeekPressed(object? sender, PointerPressedEventArgs e) => _isSeeking = true;

    private void OnSeekReleased(object? sender, PointerReleasedEventArgs e) => CompleteSeek();

    private void OnSeekCaptureLost(object? sender, PointerCaptureLostEventArgs e) => CompleteSeek();

    private void CompleteSeek()
    {
        if (!_isSeeking)
            return;

        _isSeeking = false;
        if (_player is { IsOpen: true } player)
            _ = player.SeekAsync(SeekSlider.Value);
    }

    private void OnSeekValueChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (_isUpdatingSeek)
            return;

        PositionText.Text = FormatTime(SeekSlider.Value);
        // Not dragging with the pointer: a keyboard step seeks at once.
        if (!_isSeeking && _player is { IsOpen: true } player)
            _ = player.SeekAsync(SeekSlider.Value);
    }

    private void OnUiTimerTick(object? sender, EventArgs e)
    {
        if (_player is not { } player)
            return;

        PlayPauseButton.Content = player.State == MediaPlayerState.Playing ? "Pause" : "Play";
        var duration = player.Duration.TotalSeconds;
        _isUpdatingSeek = true;
        try
        {
            SeekSlider.Maximum = Math.Max(duration, 1);
            DurationText.Text = FormatTime(duration);
            if (!_isSeeking)
            {
                SeekSlider.Value = Math.Clamp(player.Position.TotalSeconds, 0, SeekSlider.Maximum);
                PositionText.Text = FormatTime(SeekSlider.Value);
            }
        }
        finally
        {
            _isUpdatingSeek = false;
        }
    }

    // The engine raises these on background threads.
    private void OnDiagnostic(object? sender, MediaDiagnosticEventArgs e) =>
        Dispatcher.UIThread.Post(() => AddLog(e.Level, e.Category, e.Message));

    private void OnError(object? sender, MediaErrorEventArgs e) =>
        Dispatcher.UIThread.Post(() => AddLog(MediaDiagnosticLevel.Error, "MediaError", $"{e.Code}: {e.Message}"));

    private void OnFramePresented(object? sender, EventArgs e) => Interlocked.Increment(ref _frames);

    // Raised on the UI thread. The surface also raises an Info diagnostic for every MediaSurface.
    private void OnPresentationPathChanged(object? sender, EventArgs e)
    {
        if (Surface.PresentationPath != MediaSurfacePresentationPath.None)
        {
            AddLog(
                MediaDiagnosticLevel.Info,
                "Surface",
                $"{Label} presents via {DescribePath(Surface.PresentationPath)}{(Surface.PresentationPathReason is { } reason ? $" ({reason})" : string.Empty)}");
        }
    }

    private static string DescribePath(MediaSurfacePresentationPath path) => path switch
    {
        MediaSurfacePresentationPath.SharedGpuImage => "shared GPU image",
        MediaSurfacePresentationPath.FrameSink => "frame sink",
        _ => "none"
    };

    private void ReportNativeLibraries(MediaControlPlayer player)
    {
        if (player.LastNativeLoadMessage.Length > 0)
            AddLog(MediaDiagnosticLevel.Info, "Native", player.LastNativeLoadMessage);

        NativeDependencyReport report = player.NativeDependencies;
        foreach (NativeLibraryCheck library in report.Libraries.Where(static library => library.Status != NativeLibraryStatus.Loaded))
        {
            AddLog(
                library.IsRequired ? MediaDiagnosticLevel.Error : MediaDiagnosticLevel.Warning,
                "Native",
                $"{library.Component} {library.FileName}: {library.Status}{(library.Hint is null ? string.Empty : $" ({library.Hint})")}");
        }
    }

    private void AddLog(MediaDiagnosticLevel level, string category, string message)
    {
        _log.Insert(0, $"{DateTime.Now:HH:mm:ss.fff} {level,-7} {category}: {message}");
        while (_log.Count > MaxLogEntries)
            _log.RemoveAt(_log.Count - 1);
    }

    private void SetControlsEnabled(bool enabled)
    {
        OpenButton.IsEnabled = enabled;
        ModeBox.IsEnabled = enabled;
        PlayPauseButton.IsEnabled = enabled;
        StopButton.IsEnabled = enabled;
        SeekSlider.IsEnabled = enabled;
    }

    private void UpdateEffectLabels()
    {
        VolumeText.Text = $"{VolumeSlider.Value:F0}%";
        PitchText.Text = $"{(int)PitchSlider.Value:+0;-0;0} st";
        TempoText.Text = $"{(int)TempoSlider.Value}%";
    }

    private static string FormatTime(double seconds)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0, double.IsFinite(seconds) ? seconds : 0));
        return span.TotalHours >= 1 ? span.ToString(@"h\:mm\:ss") : span.ToString(@"m\:ss");
    }

    /// <summary>Converts the engine's "Name|*.a;*.b|Name2|*.c" dialog filter into picker file types.</summary>
    private static List<FilePickerFileType> ToFileTypes(string filter)
    {
        List<FilePickerFileType> types = [];
        var parts = filter.Split('|');
        for (var i = 0; i + 1 < parts.Length; i += 2)
        {
            types.Add(new FilePickerFileType(parts[i])
            {
                Patterns = [.. parts[i + 1].Split(';', StringSplitOptions.RemoveEmptyEntries).Select(static pattern => pattern == "*.*" ? "*" : pattern)]
            });
        }

        return types;
    }
}
