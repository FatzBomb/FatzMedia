using System.Collections.Specialized;
using FatzBomb.MediaControl.Core.Configuration;
using FatzBomb.MediaControl.Player.Shared;
using FatzBomb.MediaControl.Player.Shared.Hosting;
using FatzBomb.MediaControl.Player.Shared.ViewModels;
using FatzBomb.MediaControl.WinForms;

namespace FatzBomb.MediaControl.Player.WinForms;

/// <summary>
/// WinForms test player: an HWND <see cref="MediaSurface"/> presented by Direct3D11 over the shared
/// <see cref="PlayerViewModel"/>, wired with <see cref="ViewModelBinder"/>. The WPF player's flyouts
/// are tabs here (Sync, Video, Audio, A/V, Next, Log). Plain code, no designer; the code-behind
/// keeps only view behaviour: the seek bar's drag, full screen, the second screen and file drops.
/// </summary>
internal sealed class MainForm : Form
{
    private const int SeekScale = 10;
    private static readonly TimeSpan FullScreenChromeDelay = TimeSpan.FromSeconds(2.5);

    private readonly PlayerViewModel _viewModel;
    private readonly ViewModelBinder _binder;
    private readonly List<ViewModelBinder> _bandBinders = [];
    private readonly MediaSurface _surface = new() { Dock = DockStyle.Fill, Margin = Padding.Empty, BackColor = Color.Black };
    private readonly TableLayoutPanel _layout = new() { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
    private readonly Control _topBar;
    private readonly Control _bottom;
    private readonly ScrubTrackBar _seekBar = new() { Dock = DockStyle.Fill, TickStyle = TickStyle.None, Maximum = SeekScale, SmallChange = 5 * SeekScale, LargeChange = 10 * SeekScale, AutoSize = false, Height = 28 };
    private readonly CheckBox _secondScreenBox = new() { Text = "Second screen", AutoSize = true, Margin = new Padding(8, 7, 0, 0) };
    private readonly ListView _log = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HeaderStyle = ColumnHeaderStyle.Nonclickable, Font = new Font(FontFamily.GenericMonospace, 8.5f) };
    private readonly System.Windows.Forms.Timer _chromeTimer = new() { Interval = (int)FullScreenChromeDelay.TotalMilliseconds };
    private SecondScreenForm? _secondScreen;
    private bool _isDragging;
    private bool _isUpdatingSeek;
    private bool _isUpdatingSecondScreen;
    private bool _isFullScreen;
    private bool _cursorHidden;
    private FormWindowState _restoreWindowState;
    private FormBorderStyle _restoreBorderStyle;
    private bool _closing;
    private bool _closed;

    public MainForm(PlayerStartupOptions options)
    {
        Text = "FatzBomb.MediaControl WinForms test player";
        ClientSize = new Size(1120, 760);
        MinimumSize = new Size(900, 600);
        KeyPreview = true;
        AllowDrop = true;

        var shell = new WinFormsPlayerShell(this);
        _viewModel = new PlayerViewModel(
            options,
            new PlayerHost(new WinFormsPlayerDispatcher(), new WinFormsPlayerEngineFactory(options), shell, shell, Program.LogFilePath));
        _binder = new ViewModelBinder(_viewModel);

        _topBar = BuildTopBar();
        _bottom = BuildBottom();
        _layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _layout.Controls.Add(_topBar, 0, 0);
        _layout.Controls.Add(_surface, 0, 1);
        _layout.Controls.Add(_bottom, 0, 2);
        Controls.Add(_layout);

        _binder.OnChanged(() => _surface.Player = _viewModel.Player, nameof(PlayerViewModel.Player));
        _binder.OnChanged(() => _surface.VideoDisplayMode = _viewModel.VideoDisplayMode, nameof(PlayerViewModel.VideoDisplayMode));
        _binder.OnChanged(UpdateSeekBar, nameof(PlayerViewModel.SeekPositionSeconds), nameof(PlayerViewModel.DurationSeconds));

        _seekBar.PressStarting += OnSeekPressStarting;
        _seekBar.MouseDown += OnSeekMouseDown;
        _seekBar.MouseUp += OnSeekMouseUp;
        _seekBar.MouseCaptureChanged += OnSeekCaptureChanged;
        _seekBar.ValueChanged += OnSeekValueChanged;
        _surface.DoubleClick += (_, _) => ToggleFullScreen();
        _surface.MouseMove += (_, _) => OnFullScreenPointerMoved();
        _secondScreenBox.CheckedChanged += OnSecondScreenChanged;
        _chromeTimer.Tick += OnChromeTimerTick;
        DragEnter += OnDragEnter;
        DragDrop += OnDragDrop;
        KeyDown += OnFormKeyDown;
        Shown += async (_, _) => await _viewModel.StartAsync();
        FormClosing += OnFormClosing;
    }

    private Control BuildTopBar()
    {
        var bar = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 4, RowCount = 1, Padding = new Padding(4) };
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 320));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var open = new Button { Text = "Open...", AutoSize = true };
        var loaded = new Label { AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
        var url = new TextBox { Dock = DockStyle.Fill, PlaceholderText = "http(s)://... (MP3/OGG stream, MP4, MKV, HLS)", Margin = new Padding(3, 5, 3, 3) };
        var openUrl = new Button { Text = "URL...", AutoSize = true };
        _binder.Command(open, _viewModel.OpenFileCommand);
        _binder.Text(loaded, () => _viewModel.LoadedMediaText, nameof(PlayerViewModel.LoadedMediaText));
        _binder.Edit(url, () => _viewModel.NetworkUrl, value => _viewModel.NetworkUrl = value, nameof(PlayerViewModel.NetworkUrl));
        _binder.Command(openUrl, _viewModel.OpenUrlCommand);
        url.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter && _viewModel.OpenUrlCommand.CanExecute(null))
            {
                _viewModel.OpenUrlCommand.Execute(null);
                e.SuppressKeyPress = true;
            }
        };
        bar.Controls.AddRange([open, loaded, url, openUrl]);
        return bar;
    }

    private Control BuildBottom()
    {
        var bottom = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 1, RowCount = 4 };
        bottom.Controls.Add(BuildSeekRow(), 0, 0);
        bottom.Controls.Add(BuildTransportRow(), 0, 1);
        bottom.Controls.Add(BuildTabs(), 0, 2);
        var status = new Label { Dock = DockStyle.Fill, AutoSize = false, Height = 20, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
        _binder.Text(status, () => _viewModel.StatusText, nameof(PlayerViewModel.StatusText));
        bottom.Controls.Add(status, 0, 3);
        return bottom;
    }

    private Control BuildSeekRow()
    {
        var row = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 3, RowCount = 1 };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 60));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 60));
        var elapsed = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleRight };
        var remaining = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
        _binder.Text(elapsed, () => _viewModel.ElapsedText, nameof(PlayerViewModel.ElapsedText));
        _binder.Text(remaining, () => _viewModel.RemainingText, nameof(PlayerViewModel.RemainingText));
        _binder.OnChanged(() => _seekBar.Enabled = _viewModel.CanSeek, nameof(PlayerViewModel.CanSeek));
        row.Controls.Add(elapsed, 0, 0);
        row.Controls.Add(_seekBar, 1, 0);
        row.Controls.Add(remaining, 2, 0);
        return row;
    }

    private Control BuildTransportRow()
    {
        var row = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
        var play = new Button { Text = "Play", AutoSize = true };
        var pause = new Button { Text = "Pause", AutoSize = true };
        var stop = new Button { Text = "Stop", AutoSize = true };
        var loop = new CheckBox { Text = "Loop", Appearance = Appearance.Button, AutoSize = true };
        var mode = new ComboBox { Width = 110, Margin = new Padding(3, 5, 3, 3) };
        var volume = new TrackBar { Width = 140, TickStyle = TickStyle.None, AutoSize = false, Height = 28 };
        var volumeText = new Label { AutoSize = true, Margin = new Padding(0, 8, 8, 0) };
        var fullScreen = new Button { Text = "Full screen", AutoSize = true };
        _binder.Command(play, _viewModel.PlayCommand);
        _binder.Command(pause, _viewModel.PauseCommand);
        _binder.Command(stop, _viewModel.StopCommand);
        _binder.Check(loop, () => _viewModel.LoopPlayback, value => _viewModel.LoopPlayback = value, nameof(PlayerViewModel.LoopPlayback));
        _binder.Choice(mode, _viewModel.PlayerModes, () => _viewModel.PlayerMode, value => _viewModel.PlayerMode = value, nameof(PlayerViewModel.PlayerMode));
        _binder.OnChanged(() => mode.Enabled = _viewModel.CanSwitchMode, nameof(PlayerViewModel.CanSwitchMode));
        _binder.OnChanged(() => _secondScreenBox.Enabled = _viewModel.CanOpen, nameof(PlayerViewModel.CanOpen));
        _binder.Track(volume, () => _viewModel.Volume, value => _viewModel.Volume = value, nameof(PlayerViewModel.Volume), 0, 1, 100);
        _binder.Text(volumeText, () => $"{_viewModel.Volume:P0}", nameof(PlayerViewModel.Volume));
        fullScreen.Click += (_, _) => ToggleFullScreen();
        row.Controls.AddRange(
        [
            play, pause, stop, loop,
            Caption("Mode"), mode,
            _secondScreenBox,
            Caption("Volume"), volume, volumeText,
            fullScreen,
        ]);
        return row;
    }

    private TabControl BuildTabs()
    {
        var tabs = new TabControl { Dock = DockStyle.Top, Height = 230 };
        tabs.TabPages.Add(Tab("Sync", BuildSyncTab()));
        tabs.TabPages.Add(Tab("Video", BuildVideoTab()));
        tabs.TabPages.Add(Tab("Audio", BuildAudioTab()));
        tabs.TabPages.Add(Tab("A/V", BuildAVTab()));
        tabs.TabPages.Add(Tab("Next", BuildNextTab()));
        tabs.TabPages.Add(Tab("Log", BuildLogTab()));
        // The log is where a test starts; the other tabs are the WPF player's flyouts.
        tabs.SelectedIndex = tabs.TabPages.Count - 1;
        return tabs;
    }

    private Control BuildSyncTab()
    {
        var panel = Flow();
        foreach ((var label, var key, Func<string> text, var property) in new (string, string, Func<string>, string)[]
        {
            ("A/V", "audio", () => _viewModel.AudioVideoSyncText, nameof(PlayerViewModel.AudioVideoSyncText)),
            ("CDG", "cdg", () => _viewModel.CdgSyncText, nameof(PlayerViewModel.CdgSyncText)),
            ("SUB", "subtitle", () => _viewModel.SubtitleSyncText, nameof(PlayerViewModel.SubtitleSyncText)),
        })
        {
            var minus = SmallButton("−");
            var value = new Label { AutoSize = false, Width = 50, Height = 26, TextAlign = ContentAlignment.MiddleCenter };
            var plus = SmallButton("+");
            _binder.Command(minus, _viewModel.AdjustSyncCommand, $"{key}:-");
            _binder.Text(value, text, property);
            _binder.Command(plus, _viewModel.AdjustSyncCommand, $"{key}:+");
            panel.Controls.AddRange([Caption(label), minus, value, plus]);
        }

        var reset = SmallButton("Reset");
        _binder.Command(reset, _viewModel.ResetSyncCommand);
        var subtitles = new ComboBox { Width = 220, DropDownStyle = ComboBoxStyle.DropDownList, Margin = new Padding(3, 5, 3, 3), DisplayMember = nameof(SubtitleChoice.Label) };
        var updatingSubtitles = false;
        void RefreshSubtitles()
        {
            updatingSubtitles = true;
            try
            {
                subtitles.Items.Clear();
                foreach (SubtitleChoice choice in _viewModel.SubtitleChoices)
                    _ = subtitles.Items.Add(choice);
                subtitles.SelectedItem = _viewModel.SelectedSubtitle;
                subtitles.Enabled = _viewModel.HasSubtitleChoices;
            }
            finally
            {
                updatingSubtitles = false;
            }
        }

        _binder.Collection(_viewModel.SubtitleChoices, (_, _) => RefreshSubtitles());
        _binder.OnChanged(RefreshSubtitles, nameof(PlayerViewModel.SelectedSubtitle), nameof(PlayerViewModel.HasSubtitleChoices));
        subtitles.SelectionChangeCommitted += (_, _) =>
        {
            if (!updatingSubtitles && subtitles.SelectedItem is SubtitleChoice choice)
                _viewModel.SelectedSubtitle = choice;
        };
        panel.Controls.AddRange([reset, Caption("SUBTITLES"), subtitles]);
        return panel;
    }

    private Control BuildVideoTab()
    {
        var panel = Flow();
        AddSlider(panel, "HUE", () => _viewModel.HueDegrees, value => _viewModel.HueDegrees = value, nameof(PlayerViewModel.HueDegrees), -180, 180, 1, "+0;-0;0", "°");
        AddSlider(panel, "SATURATION", () => _viewModel.Saturation, value => _viewModel.Saturation = value, nameof(PlayerViewModel.Saturation), 0, 2, 100, "0.00", "×");
        AddSlider(panel, "BRIGHTNESS", () => _viewModel.Brightness, value => _viewModel.Brightness = value, nameof(PlayerViewModel.Brightness), -0.25, 0.25, 1000, "+0.00;-0.00;0.00", string.Empty);
        AddSlider(panel, "CONTRAST", () => _viewModel.Contrast, value => _viewModel.Contrast = value, nameof(PlayerViewModel.Contrast), 0.5, 1.5, 100, "0.00", "×");
        AddSlider(panel, "RED", () => _viewModel.RedGain, value => _viewModel.RedGain = value, nameof(PlayerViewModel.RedGain), 0, 2, 100, "0.00", "×");
        AddSlider(panel, "GREEN", () => _viewModel.GreenGain, value => _viewModel.GreenGain = value, nameof(PlayerViewModel.GreenGain), 0, 2, 100, "0.00", "×");
        AddSlider(panel, "BLUE", () => _viewModel.BlueGain, value => _viewModel.BlueGain = value, nameof(PlayerViewModel.BlueGain), 0, 2, 100, "0.00", "×");
        var overlays = new CheckBox { Text = "Include overlays", AutoSize = true, Margin = new Padding(3, 8, 3, 3) };
        var reset = SmallButton("Reset visual");
        var state = new Label { AutoSize = true, Margin = new Padding(3, 9, 3, 3) };
        _binder.Check(overlays, () => _viewModel.IncludeOverlays, value => _viewModel.IncludeOverlays = value, nameof(PlayerViewModel.IncludeOverlays));
        _binder.Command(reset, _viewModel.ResetBaseVisualTransformCommand);
        _binder.Text(state, () => _viewModel.BaseVisualTransformState, nameof(PlayerViewModel.BaseVisualTransformState));
        panel.Controls.AddRange([overlays, reset, state]);
        return panel;
    }

    private Control BuildAudioTab()
    {
        var panel = Flow();
        AddSlider(panel, "PITCH", () => _viewModel.Pitch, value => _viewModel.Pitch = (int)Math.Round(value), nameof(PlayerViewModel.Pitch), -12, 12, 1, "+0;-0;0", " st");
        AddSlider(panel, "TEMPO", () => _viewModel.Tempo, value => _viewModel.Tempo = (int)Math.Round(value), nameof(PlayerViewModel.Tempo), 50, 200, 1, "0", "%");
        var checks = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true };
        AddCheck(checks, "Normalize", () => _viewModel.Normalize, value => _viewModel.Normalize = value, nameof(PlayerViewModel.Normalize));
        AddCheck(checks, "Reset pitch on load", () => _viewModel.ResetPitchOnMediaLoad, value => _viewModel.ResetPitchOnMediaLoad = value, nameof(PlayerViewModel.ResetPitchOnMediaLoad));
        AddCheck(checks, "Reset tempo on load", () => _viewModel.ResetTempoOnMediaLoad, value => _viewModel.ResetTempoOnMediaLoad = value, nameof(PlayerViewModel.ResetTempoOnMediaLoad));
        AddCheck(checks, "Silent drag", () => _viewModel.SilentDrag, value => _viewModel.SilentDrag = value, nameof(PlayerViewModel.SilentDrag));
        panel.Controls.Add(checks);

        var eq = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true };
        var enabled = new CheckBox { Text = "EQ", AutoSize = true };
        var flat = SmallButton("Flat");
        _binder.Check(enabled, () => _viewModel.EqualizerEnabled, value => _viewModel.EqualizerEnabled = value, nameof(PlayerViewModel.EqualizerEnabled));
        _binder.Command(flat, _viewModel.FlattenEqualizerCommand);
        eq.Controls.AddRange([enabled, flat]);
        panel.Controls.Add(eq);

        var bands = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        foreach (EqualizerBandViewModel band in _viewModel.EqualizerBands)
        {
            var binder = new ViewModelBinder(band);
            _bandBinders.Add(binder);
            var column = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, Margin = new Padding(0) };
            var gain = new Label { AutoSize = false, Width = 40, TextAlign = ContentAlignment.MiddleCenter };
            // A vertical track bar has its minimum at the top: it holds the negated gain.
            var slider = new TrackBar { Orientation = Orientation.Vertical, Height = 110, Width = 40, TickStyle = TickStyle.None, AutoSize = false };
            var label = new Label { AutoSize = false, Width = 40, TextAlign = ContentAlignment.MiddleCenter, Text = band.Label };
            binder.Text(gain, () => $"{band.GainDb:+0;-0;0}", nameof(EqualizerBandViewModel.GainDb));
            binder.Track(slider, () => -band.GainDb, value => band.GainDb = -value, nameof(EqualizerBandViewModel.GainDb), -EqualizerBandViewModel.MaximumGainDb, -EqualizerBandViewModel.MinimumGainDb);
            _binder.OnChanged(() => slider.Enabled = _viewModel.EqualizerEnabled, nameof(PlayerViewModel.EqualizerEnabled));
            column.Controls.AddRange([gain, slider, label]);
            bands.Controls.Add(column);
        }

        panel.Controls.Add(bands);
        return panel;
    }

    private Control BuildAVTab()
    {
        var panel = Flow();
        var checks = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true };
        AddCheck(checks, "Auto play", () => _viewModel.AutoPlay, value => _viewModel.AutoPlay = value, nameof(PlayerViewModel.AutoPlay));
        AddCheck(checks, "Display-locked", () => _viewModel.DisplayLockedTiming, value => _viewModel.DisplayLockedTiming = value, nameof(PlayerViewModel.DisplayLockedTiming));
        panel.Controls.Add(checks);
        var display = new ComboBox { Width = 120, Margin = new Padding(3, 5, 3, 3) };
        _binder.Choice(display, _viewModel.VideoDisplayModes, () => _viewModel.VideoDisplayMode, value => _viewModel.VideoDisplayMode = value, nameof(PlayerViewModel.VideoDisplayMode));
        panel.Controls.AddRange([Caption("DISPLAY"), display]);
        AddSlider(panel, "CD+G BG", () => _viewModel.CdgBackgroundOpacity, value => _viewModel.CdgBackgroundOpacity = (int)Math.Round(value), nameof(PlayerViewModel.CdgBackgroundOpacity), 0, 100, 1, "0", "%");
        return panel;
    }

    private Control BuildNextTab()
    {
        var panel = Flow();
        var choose = SmallButton("Choose...");
        var prepared = new Label { AutoSize = false, Width = 360, Height = 26, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
        var playNext = SmallButton("Play next now");
        var clear = SmallButton("Clear");
        _binder.Command(choose, _viewModel.ChooseNextCommand);
        _binder.Text(prepared, () => _viewModel.PreparedNextText, nameof(PlayerViewModel.PreparedNextText));
        _binder.Command(playNext, _viewModel.PlayNextNowCommand);
        _binder.Command(clear, _viewModel.ClearNextCommand);
        panel.Controls.AddRange([Caption("NEXT"), choose, prepared, playNext, clear]);
        panel.SetFlowBreak(clear, true);

        var autoNext = new CheckBox { Text = "Auto-next", AutoSize = true, Margin = new Padding(3, 8, 3, 3) };
        _binder.Check(autoNext, () => _viewModel.AutoNext, value => _viewModel.AutoNext = value, nameof(PlayerViewModel.AutoNext));
        panel.Controls.Add(autoNext);
        AddSlider(panel, "FADE", () => _viewModel.FadeSeconds, value => _viewModel.FadeSeconds = value, nameof(PlayerViewModel.FadeSeconds), 0, 10, 10, "0.#", "s");
        var fadeOut = SmallButton("Fade out");
        var fadeIn = SmallButton("Fade in");
        var cancel = SmallButton("Cancel");
        _binder.Command(fadeOut, _viewModel.FadeOutCommand);
        _binder.Command(fadeIn, _viewModel.FadeInCommand);
        _binder.Command(cancel, _viewModel.CancelFadeCommand);
        panel.Controls.AddRange([fadeOut, fadeIn, cancel]);
        panel.SetFlowBreak(cancel, true);

        var stream = new Label { AutoSize = false, Width = 520, Height = 26, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
        _binder.Text(stream, () => _viewModel.StreamInfoText, nameof(PlayerViewModel.StreamInfoText));
        panel.Controls.AddRange([Caption("STREAM"), stream]);
        return panel;
    }

    private Control BuildLogTab()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var bar = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        var count = new Label { AutoSize = true, Margin = new Padding(3, 9, 8, 3) };
        var copy = SmallButton("Copy all");
        var clear = SmallButton("Clear");
        var openFile = SmallButton("Open log file");
        _binder.Command(copy, _viewModel.CopyLogCommand);
        _binder.Command(clear, _viewModel.ClearLogCommand);
        _binder.Command(openFile, _viewModel.OpenLogFileCommand);
        bar.Controls.AddRange([count, copy, clear, openFile]);

        _log.Columns.Add("Time", 90);
        _log.Columns.Add("Level", 64);
        _log.Columns.Add("Category", 150);
        _log.Columns.Add("Message", 900);
        void RefreshCount() => count.Text = $"{_viewModel.DiagnosticLog.Count} entries, newest first";
        foreach (DiagnosticLogEntry entry in _viewModel.DiagnosticLog)
            _ = _log.Items.Add(ToItem(entry));
        _binder.Collection(_viewModel.DiagnosticLog, (_, e) =>
        {
            OnLogChanged(e);
            RefreshCount();
        });
        RefreshCount();

        root.Controls.Add(bar, 0, 0);
        root.Controls.Add(_log, 0, 1);
        return root;
    }

    // The view model inserts new entries at the top, trims the bottom and clears.
    private void OnLogChanged(NotifyCollectionChangedEventArgs e)
    {
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add when e.NewItems is { } added:
                for (var i = 0; i < added.Count; i++)
                    _ = _log.Items.Insert(e.NewStartingIndex + i, ToItem((DiagnosticLogEntry)added[i]!));
                break;
            case NotifyCollectionChangedAction.Remove when e.OldItems is { } removed:
                for (var i = 0; i < removed.Count; i++)
                    _log.Items.RemoveAt(e.OldStartingIndex);
                break;
            default:
                _log.Items.Clear();
                foreach (DiagnosticLogEntry entry in _viewModel.DiagnosticLog)
                    _ = _log.Items.Add(ToItem(entry));
                break;
        }
    }

    private static ListViewItem ToItem(DiagnosticLogEntry entry)
    {
        var item = new ListViewItem([entry.TimeText, entry.Level.ToString(), entry.Category, entry.Message]) { UseItemStyleForSubItems = false };
        item.SubItems[1].ForeColor = entry.Level switch
        {
            MediaDiagnosticLevel.Error => Color.FromArgb(255, 110, 110),
            MediaDiagnosticLevel.Warning => Color.Orange,
            _ => SystemColors.GrayText
        };
        item.SubItems[2].ForeColor = Color.LightSkyBlue;
        return item;
    }

    private void AddSlider(Control panel, string caption, Func<double> get, Action<double> set, string property, double minimum, double maximum, double scale, string format, string unit)
    {
        var bar = new TrackBar { Width = 150, TickStyle = TickStyle.None, AutoSize = false, Height = 28 };
        var value = new Label { AutoSize = false, Width = 56, Height = 26, TextAlign = ContentAlignment.MiddleLeft };
        _binder.Track(bar, get, set, property, minimum, maximum, scale);
        _binder.Text(value, () => get().ToString(format, System.Globalization.CultureInfo.CurrentCulture) + unit, property);
        panel.Controls.AddRange([Caption(caption), bar, value]);
    }

    private void AddCheck(Control panel, string text, Func<bool> get, Action<bool> set, string property)
    {
        var box = new CheckBox { Text = text, AutoSize = true };
        _binder.Check(box, get, set, property);
        panel.Controls.Add(box);
    }

    private static TabPage Tab(string text, Control content)
    {
        var page = new TabPage(text) { Padding = new Padding(4) };
        content.Dock = DockStyle.Fill;
        page.Controls.Add(content);
        return page;
    }

    private static FlowLayoutPanel Flow() => new() { AutoScroll = true, WrapContents = true };

    private static Label Caption(string text) => new() { Text = text, AutoSize = true, Margin = new Padding(8, 9, 2, 3), ForeColor = SystemColors.GrayText };

    private static Button SmallButton(string text) => new() { Text = text, AutoSize = true };

    private void UpdateSeekBar()
    {
        _isUpdatingSeek = true;
        try
        {
            _seekBar.Maximum = Math.Max(SeekScale, (int)Math.Ceiling(_viewModel.DurationSeconds * SeekScale));
            if (!_isDragging)
                _seekBar.Value = Math.Clamp((int)Math.Round(_viewModel.SeekPositionSeconds * SeekScale), 0, _seekBar.Maximum);
        }
        finally
        {
            _isUpdatingSeek = false;
        }
    }

    // A press starts a scrub (before the native track bar pages, so that step is a drag seek too)
    // and jumps to the pointer; dragging the thumb seeks as it moves.
    private void OnSeekPressStarting(object? sender, EventArgs e)
    {
        if (_isDragging || !_viewModel.CanSeek)
            return;

        _isDragging = true;
        _viewModel.BeginSeekPreview();
    }

    private void OnSeekMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || !_isDragging)
            return;

        var inset = 13 * DeviceDpi / 96.0;
        var fraction = Math.Clamp((e.X - inset) / Math.Max(1, _seekBar.ClientSize.Width - (2 * inset)), 0, 1);
        _seekBar.Value = (int)Math.Round(fraction * _seekBar.Maximum);
    }

    private void OnSeekMouseUp(object? sender, MouseEventArgs e) => EndSeekDrag();

    // The track bar lost the mouse (Alt+Tab, a dialog): end the drag where it is.
    private void OnSeekCaptureChanged(object? sender, EventArgs e)
    {
        if (!_seekBar.Capture)
            EndSeekDrag();
    }

    private void EndSeekDrag()
    {
        if (!_isDragging)
            return;

        _isDragging = false;
        _viewModel.EndSeekPreview();
    }

    private void OnSeekValueChanged(object? sender, EventArgs e)
    {
        if (_isUpdatingSeek)
            return;

        var seconds = _seekBar.Value / (double)SeekScale;
        if (_isDragging)
            _viewModel.SeekPositionSeconds = seconds;
        else
            _viewModel.SeekTo(seconds);
    }

    private void OnFormKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.F11)
        {
            ToggleFullScreen();
            e.Handled = true;
        }
        else if (e.KeyCode == Keys.Escape && _isFullScreen)
        {
            ExitFullScreen();
            e.Handled = true;
        }
    }

    private void ToggleFullScreen()
    {
        if (_isFullScreen)
            ExitFullScreen();
        else
            EnterFullScreen();
    }

    private void EnterFullScreen()
    {
        _isFullScreen = true;
        _restoreWindowState = WindowState;
        _restoreBorderStyle = FormBorderStyle;
        FormBorderStyle = FormBorderStyle.None;
        WindowState = FormWindowState.Normal;
        WindowState = FormWindowState.Maximized;
        ShowChrome(true);
        _chromeTimer.Start();
    }

    private void ExitFullScreen()
    {
        if (!_isFullScreen)
            return;

        _isFullScreen = false;
        _chromeTimer.Stop();
        ShowChrome(true);
        FormBorderStyle = _restoreBorderStyle;
        WindowState = _restoreWindowState;
    }

    private void OnFullScreenPointerMoved()
    {
        if (!_isFullScreen)
            return;

        ShowChrome(true);
        _chromeTimer.Stop();
        _chromeTimer.Start();
    }

    private void OnChromeTimerTick(object? sender, EventArgs e)
    {
        _chromeTimer.Stop();
        if (_isFullScreen && !_isDragging && !_bottom.ClientRectangle.Contains(_bottom.PointToClient(Cursor.Position)))
            ShowChrome(false);
    }

    private void ShowChrome(bool visible)
    {
        _topBar.Visible = visible;
        _bottom.Visible = visible;
        // Cursor.Show and Hide count: call each only on a change.
        if (visible == _cursorHidden)
        {
            _cursorHidden = !visible;
            if (visible)
                Cursor.Show();
            else
                Cursor.Hide();
        }
    }

    /// <summary>Opens or closes the second screen: the same player on another monitor (projector role).</summary>
    private void OnSecondScreenChanged(object? sender, EventArgs e)
    {
        if (_isUpdatingSecondScreen)
            return;

        if (_secondScreenBox.Checked)
            OpenSecondScreen();
        else
            CloseSecondScreen();
    }

    private void OpenSecondScreen()
    {
        if (_viewModel.Player is not { } player || _secondScreen is not null)
        {
            SetSecondScreenBox(false);
            return;
        }

        Screen current = Screen.FromControl(this);
        Screen? other = Screen.AllScreens.FirstOrDefault(screen => !screen.Equals(current));
        var window = new SecondScreenForm(player, other);
        window.FormClosed += OnSecondScreenClosed;
        _secondScreen = window;
        _viewModel.AddLogEntry(MediaDiagnosticLevel.Info, "Surface", other is null
            ? "Second screen opens in a window (one screen)"
            : $"Second screen opens full screen on {other.DeviceName}");
        window.Show(this);
    }

    private void CloseSecondScreen()
    {
        SecondScreenForm? window = _secondScreen;
        if (window is null)
            return;

        _secondScreen = null;
        window.FormClosed -= OnSecondScreenClosed;
        window.Close();
        SetSecondScreenBox(false);
    }

    // The user closed the window.
    private void OnSecondScreenClosed(object? sender, FormClosedEventArgs e)
    {
        if (!ReferenceEquals(sender, _secondScreen))
            return;

        _secondScreen!.FormClosed -= OnSecondScreenClosed;
        _secondScreen = null;
        SetSecondScreenBox(false);
    }

    private void SetSecondScreenBox(bool isChecked)
    {
        _isUpdatingSecondScreen = true;
        try
        {
            _secondScreenBox.Checked = isChecked;
        }
        finally
        {
            _isUpdatingSecondScreen = false;
        }
    }

    private void OnDragEnter(object? sender, DragEventArgs e) =>
        e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true && _viewModel.CanOpen ? DragDropEffects.Copy : DragDropEffects.None;

    private async void OnDragDrop(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
            await _viewModel.LoadMediaAsync(files[0]);
    }

    private async void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_closed)
            return;

        // Dispose waits for admitted media operations, so the view model runs it off the UI
        // thread; the form closes afterwards. A second close request waits for the first.
        e.Cancel = true;
        if (_closing)
            return;

        _closing = true;
        _chromeTimer.Stop();
        ShowChrome(true);
        CloseSecondScreen();
        _binder.Dispose();
        foreach (ViewModelBinder binder in _bandBinders)
            binder.Dispose();
        _surface.Player = null;
        await _viewModel.DisposeAsync();
        _closed = true;
        Close();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _chromeTimer.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>
/// The second screen: a borderless maximized window on another monitor (a 960x540 window with one
/// monitor) whose HWND surface joins the player as its projector surface.
/// </summary>
internal sealed class SecondScreenForm : Form
{
    private readonly MediaSurface _surface = new() { Dock = DockStyle.Fill, Role = MediaPresentationSurfaceRole.Projector, SurfaceId = MediaSurface.DefaultSecondarySurfaceId };

    public SecondScreenForm(MediaControlPlayer player, Screen? screen)
    {
        Text = "WinForms player - second screen";
        BackColor = Color.Black;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        if (screen is not null)
        {
            FormBorderStyle = FormBorderStyle.None;
            Bounds = screen.Bounds;
        }
        else
        {
            ClientSize = new Size(960, 540);
            Location = new Point(80, 80);
        }

        Controls.Add(_surface);
        _surface.Player = player;
        KeyPreview = true;
        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape)
                Close();
        };
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _surface.Player = null;
        base.OnFormClosed(e);
    }
}

/// <summary>A track bar that says when the left button goes down, before the native control pages or drags.</summary>
internal sealed class ScrubTrackBar : TrackBar
{
    private const int WmLeftButtonDown = 0x0201;
    private const int WmLeftButtonDoubleClick = 0x0203;

    /// <summary>Raised on the left button press, before the native control handles it.</summary>
    public event EventHandler? PressStarting;

    protected override void WndProc(ref Message m)
    {
        // The second press of a quick double click arrives as a double-click message.
        if (m.Msg is WmLeftButtonDown or WmLeftButtonDoubleClick)
            PressStarting?.Invoke(this, EventArgs.Empty);
        base.WndProc(ref m);
    }
}
