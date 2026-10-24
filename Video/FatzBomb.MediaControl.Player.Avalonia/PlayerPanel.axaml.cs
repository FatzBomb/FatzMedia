using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform;
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
    private bool _isUpdatingSecondScreen;
    private SecondaryPresentationWindow? _secondScreen;
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
            player.BufferingChanged += OnBufferingChanged;
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

            // --second-screen=<n> opens player 1's second screen at startup.
            if (_index == 1 && StartupOptions.Current.SecondScreen is not null)
                SecondScreenBox.IsChecked = true;

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
        CloseSecondScreen();
        Surface.Player = null;
        MediaControlPlayer? player = _player;
        _player = null;
        if (player is null)
            return;

        player.MediaDiagnostic -= OnDiagnostic;
        player.MediaError -= OnError;
        player.PresentationFramePresented -= OnFramePresented;
        player.BufferingChanged -= OnBufferingChanged;
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
            $"via {DescribePath(Surface.PresentationPath)}{DescribeSecondScreen()}";
    }

    private string DescribeSecondScreen()
    {
        if (_secondScreen?.Surface is not { } surface)
            return string.Empty;

        PixelSize size = surface.AttachedPixelSize;
        return $" | 2nd screen {size.Width}x{size.Height} via {DescribePath(surface.PresentationPath)}{(surface.IsPresentationSuspended ? " (cleared: Audio mode)" : string.Empty)}";
    }

    /// <summary>Opens or closes this player's second screen (Stage 027A slice 5).</summary>
    private void OnSecondScreenChanged(object? sender, RoutedEventArgs e)
    {
        if (_isUpdatingSecondScreen)
            return;

        if (SecondScreenBox.IsChecked == true)
            OpenSecondScreen();
        else
            CloseSecondScreen();
    }

    private void OpenSecondScreen()
    {
        if (_player is not { } player || _secondScreen is not null || TopLevel.GetTopLevel(this) is not Window owner)
        {
            SetSecondScreenBox(false);
            return;
        }

        (Screen? screen, var fullScreen, var where) = ChooseSecondScreen(owner);
        var window = new SecondaryPresentationWindow(player, $"second-screen-{_index}") { Title = $"Player {_index} - second screen" };
        window.Surface.PreferGpuSharedSurface = StartupOptions.Current.PreferGpuSurface;
        window.Surface.PresentationPathChanged += OnSecondScreenPathChanged;
        window.Closed += OnSecondScreenClosed;
        _secondScreen = window;
        AddLog(MediaDiagnosticLevel.Info, "Surface", $"{Label} second screen opens {where}");
        window.ShowOnScreen(screen, fullScreen, owner);
    }

    /// <summary>
    /// <c>--second-screen=&lt;n&gt;</c> when that screen exists, else the first screen that is not the
    /// main window's, full screen; with one screen, a window beside the player.
    /// </summary>
    private static (Screen? Screen, bool FullScreen, string Where) ChooseSecondScreen(Window owner)
    {
        IReadOnlyList<Screen> screens = owner.Screens.All;
        if (StartupOptions.Current.SecondScreen is { } number && number <= screens.Count)
            return (screens[number - 1], true, $"full screen on screen {number} of {screens.Count} ({screens[number - 1].DisplayName})");

        Screen? current = owner.Screens.ScreenFromWindow(owner);
        for (var i = 0; i < screens.Count; i++)
        {
            if (!Equals(screens[i], current))
                return (screens[i], true, $"full screen on screen {i + 1} of {screens.Count} ({screens[i].DisplayName})");
        }

        return (current, false, $"in a window (one screen{(StartupOptions.Current.SecondScreen is { } missing ? $"; screen {missing} does not exist" : string.Empty)})");
    }

    private void CloseSecondScreen()
    {
        SecondaryPresentationWindow? window = _secondScreen;
        if (window is null)
            return;

        _secondScreen = null;
        window.Closed -= OnSecondScreenClosed;
        window.Surface.PresentationPathChanged -= OnSecondScreenPathChanged;
        window.Close();
        SetSecondScreenBox(false);
    }

    // The user closed the window.
    private void OnSecondScreenClosed(object? sender, EventArgs e)
    {
        if (_secondScreen is not { } window || !ReferenceEquals(sender, window))
            return;

        window.Surface.PresentationPathChanged -= OnSecondScreenPathChanged;
        window.Closed -= OnSecondScreenClosed;
        _secondScreen = null;
        SetSecondScreenBox(false);
    }

    private void SetSecondScreenBox(bool isChecked)
    {
        _isUpdatingSecondScreen = true;
        try
        {
            SecondScreenBox.IsChecked = isChecked;
        }
        finally
        {
            _isUpdatingSecondScreen = false;
        }
    }

    private void OnSecondScreenPathChanged(object? sender, EventArgs e)
    {
        if (sender is MediaSurface { PresentationPath: not MediaSurfacePresentationPath.None } surface)
        {
            AddLog(
                MediaDiagnosticLevel.Info,
                "Surface",
                $"{Label} second screen presents via {DescribePath(surface.PresentationPath)}{(surface.PresentationPathReason is { } reason ? $" ({reason})" : string.Empty)}");
        }
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

    private async void OnOpenUrlClick(object? sender, RoutedEventArgs e) => await OpenUrlBoxAsync();

    private async void OnUrlKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            await OpenUrlBoxAsync();
    }

    private async Task OpenUrlBoxAsync()
    {
        var text = UrlBox.Text?.Trim() ?? string.Empty;
        if (!TryParseNetworkUrl(text, out _))
        {
            AddLog(MediaDiagnosticLevel.Warning, "Player", "Enter an http:// or https:// URL.");
            return;
        }

        try
        {
            await OpenAsync(text);
        }
        catch (Exception ex)
        {
            AddLog(MediaDiagnosticLevel.Error, "Player", $"Open failed: {ex.Message}");
        }
    }

    /// <summary>Opens a local file, or an http/https URL as a network source (Stage 039A).</summary>
    private async Task OpenAsync(string path)
    {
        if (_player is not { } player)
            return;

        var isUrl = TryParseNetworkUrl(path, out Uri? uri);
        FileText.Text = isUrl ? path : Path.GetFileName(path);
        if (isUrl)
            UrlBox.Text = path;
        AddLog(MediaDiagnosticLevel.Info, "Player", $"Loading {path}");
        // PlayOnLoad starts playback after a successful load.
        var loaded = isUrl ? await player.LoadAsync(MediaInputSource.FromUri(uri!)) : await player.LoadAsync(path);
        if (!loaded)
        {
            AddLog(MediaDiagnosticLevel.Warning, "Player", $"Could not load {path}");
            return;
        }

        if (isUrl && player.CurrentSourceCapabilities is { } capabilities)
            AddLog(MediaDiagnosticLevel.Info, "Network",
                $"{capabilities.SourceClass}, seekable={capabilities.IsSeekable}, duration={(capabilities.IsDurationKnown ? FormatTime(player.Duration.TotalSeconds) : "unknown")}");
    }

    private static bool TryParseNetworkUrl(string text, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Uri? uri) =>
        Uri.TryCreate(text, UriKind.Absolute, out uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

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
    private void OnBufferingChanged(object? sender, MediaBufferingChangedEventArgs e) =>
        Dispatcher.UIThread.Post(() => AddLog(MediaDiagnosticLevel.Info, "Network", $"Buffering={e.IsBuffering} fill={e.BufferFill:P0}"));

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
        UrlBox.IsEnabled = enabled;
        OpenUrlButton.IsEnabled = enabled;
        ModeBox.IsEnabled = enabled;
        SecondScreenBox.IsEnabled = enabled;
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
