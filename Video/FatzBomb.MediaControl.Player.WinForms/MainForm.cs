using FatzBomb.MediaControl.Core.Abstractions;
using FatzBomb.MediaControl.Core.Configuration;
using FatzBomb.MediaControl.WinForms;

namespace FatzBomb.MediaControl.Player.WinForms;

/// <summary>
/// Small WinForms test player: an HWND <see cref="MediaSurface"/> presented by Direct3D11, plus
/// open, play/pause, stop, seek, volume, loop and a diagnostics list (newest first). Plain code, no designer.
/// </summary>
internal sealed class MainForm : Form
{
    private const int MaxLogEntries = 500;

    private readonly string? _startupPath;
    private readonly MediaSurface _surface = new() { Dock = DockStyle.Fill };
    private readonly Button _openButton = new() { Text = "Open...", AutoSize = true, Enabled = false };
    private readonly Button _playPauseButton = new() { Text = "Play", AutoSize = true, Enabled = false };
    private readonly Button _stopButton = new() { Text = "Stop", AutoSize = true, Enabled = false };
    private readonly CheckBox _loopBox = new() { Text = "Loop", AutoSize = true };
    private readonly TrackBar _seekBar = new() { Dock = DockStyle.Fill, TickStyle = TickStyle.None, Maximum = 1, Enabled = false };
    private readonly TrackBar _volumeBar = new() { Width = 120, TickStyle = TickStyle.None, Maximum = 100, Value = 80 };
    private readonly Label _timeLabel = new() { Text = "0:00 / 0:00", AutoSize = true, Anchor = AnchorStyles.Left };
    private readonly Label _statusLabel = new() { Text = "starting...", AutoSize = true, Anchor = AnchorStyles.Left };
    private readonly ListBox _log = new() { Dock = DockStyle.Fill, HorizontalScrollbar = true, IntegralHeight = false };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 200 };
    private MediaControlPlayer? _player;
    private bool _isSeeking;
    private bool _isUpdatingSeek;
    private bool _closed;

    public MainForm(string? startupPath)
    {
        _startupPath = startupPath;
        Text = "FatzBomb.MediaControl WinForms test player";
        ClientSize = new Size(960, 700);

        var transport = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false };
        transport.Controls.AddRange([_openButton, _playPauseButton, _stopButton, _loopBox, new Label { Text = "Volume", AutoSize = true, Margin = new Padding(12, 6, 0, 0) }, _volumeBar]);

        var seekRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, AutoSize = true };
        seekRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        seekRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        seekRow.Controls.Add(_seekBar, 0, 0);
        seekRow.Controls.Add(_timeLabel, 1, 0);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5 };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 70));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 30));
        layout.Controls.Add(_surface, 0, 0);
        layout.Controls.Add(seekRow, 0, 1);
        layout.Controls.Add(transport, 0, 2);
        layout.Controls.Add(_statusLabel, 0, 3);
        layout.Controls.Add(_log, 0, 4);
        Controls.Add(layout);

        _openButton.Click += async (_, _) => await OpenFileAsync();
        _playPauseButton.Click += async (_, _) => await TogglePlayPauseAsync();
        _stopButton.Click += async (_, _) =>
        {
            if (_player is { IsOpen: true } player)
                _ = await player.StopAsync();
        };
        _loopBox.CheckedChanged += (_, _) =>
        {
            if (_player is { } player)
                player.LoopMedia = _loopBox.Checked;
        };
        _volumeBar.ValueChanged += (_, _) =>
        {
            if (_player is { } player)
                player.Effects.Volume = _volumeBar.Value / 100.0;
        };
        // Seek when the drag ends (mouse up) or a key steps the bar.
        _seekBar.MouseDown += (_, _) => _isSeeking = true;
        _seekBar.MouseUp += (_, _) => CompleteSeek();
        _seekBar.ValueChanged += (_, _) =>
        {
            if (_isUpdatingSeek)
                return;
            if (!_isSeeking)
                CompleteSeek(force: true);
        };
        _timer.Tick += (_, _) => RefreshTransport();

        Shown += async (_, _) => await InitializePlayerAsync();
        FormClosing += OnFormClosing;
    }

    private async Task InitializePlayerAsync()
    {
        AddLog(MediaDiagnosticLevel.Info, "Player", "Initializing native media engine (Windows + Direct3D11)...");
        var nativePaths = WindowsNativeLibraryPaths.GetDefaultSearchPaths();
        if (nativePaths.Count == 0)
        {
            var nativeFolder = Path.Combine(AppContext.BaseDirectory, WindowsNativeLibraryPaths.AppNativeRelativePath);
            _ = Directory.CreateDirectory(nativeFolder);
            nativePaths = [nativeFolder];
        }

        AddLog(MediaDiagnosticLevel.Info, "Player", $"Native search paths: {string.Join("; ", nativePaths)}");
        try
        {
            // Loading BASS and FFmpeg blocks, so the engine is created off the UI thread.
            var platform = new WindowsDirect3D11MediaControlPlatformFactory();
            MediaControlPlayer player = await Task.Run(() => new MediaControlPlayer(platform, MediaPlayerConfiguration.Auto, nativePaths));
            // Subscribed before the surface binds so the renderer's device diagnostics are not missed.
            player.MediaDiagnostic += (_, e) => OnUiThread(() => AddLog(e.Level, e.Category, e.Message));
            player.MediaError += (_, e) => OnUiThread(() => AddLog(MediaDiagnosticLevel.Error, "MediaError", $"{e.Code}: {e.Message}"));
            player.PlayOnLoad = true;
            player.LoopMedia = _loopBox.Checked;
            player.Effects.Volume = _volumeBar.Value / 100.0;
            _player = player;
            _surface.Player = player;

            foreach (NativeLibraryCheck library in player.NativeDependencies.Libraries.Where(static library => library.Status != NativeLibraryStatus.Loaded))
                AddLog(library.IsRequired ? MediaDiagnosticLevel.Error : MediaDiagnosticLevel.Warning, "Native", $"{library.Component} {library.FileName}: {library.Status}");

            _openButton.Enabled = _playPauseButton.Enabled = _stopButton.Enabled = _seekBar.Enabled = true;
            _statusLabel.Text = "Ready";
            _timer.Start();
            if (!string.IsNullOrEmpty(_startupPath))
                await LoadAsync(_startupPath);
        }
        catch (Exception ex)
        {
            AddLog(MediaDiagnosticLevel.Error, "Player", $"Engine unavailable: {ex.Message}");
            _statusLabel.Text = $"Native DLLs unavailable: {ex.Message}";
        }
    }

    private async Task OpenFileAsync()
    {
        if (_player is not { } player)
            return;

        using var dialog = new OpenFileDialog { Filter = player.OpenFileDialogFilter, Title = "Open media" };
        if (dialog.ShowDialog(this) == DialogResult.OK)
            await LoadAsync(dialog.FileName);
    }

    private async Task LoadAsync(string path)
    {
        if (_player is not { } player)
            return;

        AddLog(MediaDiagnosticLevel.Info, "Player", $"Loading {path}");
        _statusLabel.Text = Path.GetFileName(path);
        // PlayOnLoad starts playback after a successful load.
        if (!await player.LoadAsync(path))
            AddLog(MediaDiagnosticLevel.Warning, "Player", $"Could not load {path}");
    }

    private async Task TogglePlayPauseAsync()
    {
        if (_player is not { IsOpen: true } player)
            return;

        _ = player.State == MediaPlayerState.Playing ? await player.PauseAsync() : await player.PlayAsync();
    }

    private void CompleteSeek(bool force = false)
    {
        if (!_isSeeking && !force)
            return;

        _isSeeking = false;
        if (_player is { IsOpen: true } player)
            _ = player.SeekAsync(_seekBar.Value);
    }

    private void RefreshTransport()
    {
        if (_player is not { } player)
            return;

        _playPauseButton.Text = player.State == MediaPlayerState.Playing ? "Pause" : "Play";
        var duration = player.Duration.TotalSeconds;
        var position = player.Position.TotalSeconds;
        if (!_isSeeking)
        {
            _isUpdatingSeek = true;
            try
            {
                _seekBar.Maximum = Math.Max(1, (int)Math.Ceiling(duration));
                _seekBar.Value = Math.Clamp((int)position, 0, _seekBar.Maximum);
            }
            finally
            {
                _isUpdatingSeek = false;
            }
        }

        _timeLabel.Text = $"{FormatTime(_isSeeking ? _seekBar.Value : position)} / {FormatTime(duration)}";
    }

    // Engine events arrive on background threads.
    private void OnUiThread(Action action)
    {
        if (IsDisposed || !IsHandleCreated)
            return;

        try
        {
            _ = BeginInvoke(action);
        }
        catch (InvalidOperationException)
        {
            // The form closed between the check and the call.
        }
    }

    private void AddLog(MediaDiagnosticLevel level, string category, string message)
    {
        _log.Items.Insert(0, $"{DateTime.Now:HH:mm:ss.fff} {level,-7} {category}: {message}");
        while (_log.Items.Count > MaxLogEntries)
            _log.Items.RemoveAt(_log.Items.Count - 1);
    }

    private static string FormatTime(double seconds)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0, double.IsFinite(seconds) ? seconds : 0));
        return span.TotalHours >= 1 ? span.ToString(@"h\:mm\:ss") : span.ToString(@"m\:ss");
    }

    private async void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_closed)
            return;

        // Dispose waits for admitted media operations, so run it off the UI thread and close afterwards.
        e.Cancel = true;
        _timer.Stop();
        MediaControlPlayer? player = _player;
        _player = null;
        _surface.Player = null;
        if (player is not null)
            await Task.Run(player.Dispose);

        _closed = true;
        Close();
    }
}
