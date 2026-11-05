using FatzBomb.MediaControl.Player.Configuration;
using FatzBomb.MediaControl.Player.Shared;
using FatzBomb.MediaControl.Player.Shared.Hosting;
using FatzBomb.MediaControl.Player.Shared.Presentation;

namespace FatzBomb.MediaControl.Player.WinForms;

/// <summary>
/// WinForms test player: 1-4 independent <see cref="PlayerPanel"/>s side by side
/// (<c>--players=1..4</c>, Stage 046A slice 3; one view model each, like the WPF and Avalonia
/// players), each fed a once-a-second process reading for its stats line. The toolbar changes the
/// player count and the renderer at run time, as in every test player: a change recreates the
/// players (loaded files are not kept). F11 or a double-click on a picture shows that player alone,
/// full screen; Escape leaves full screen. The rebuild, full screen, keys, stats tick and closing
/// are the shared <see cref="PlayerSetController{TSlot}"/>'s; this form creates and lays out the
/// panels and owns the window state.
/// </summary>
internal sealed class MainForm : Form, IPlayerSetHost<PlayerPanel>
{
    private readonly PlayerStartupOptions _options;
    private readonly Panel _grid = new() { Dock = DockStyle.Fill };
    private readonly WinFormsPlayerDispatcher _dispatcher = new();
    private readonly PlayerSetController<PlayerPanel> _players;
    private readonly FlowLayoutPanel _toolbar = new() { Dock = DockStyle.Top, AutoSize = true, WrapContents = false, Padding = new Padding(6, 4, 6, 4) };
    private readonly ComboBox _playersBox = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 60 };
    private readonly ComboBox _rendererBox = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220 };
    private readonly Label _info = new() { AutoSize = true, Margin = new Padding(16, 8, 3, 3), ForeColor = SystemColors.GrayText };
    private FormWindowState _restoreWindowState;
    private FormBorderStyle _restoreBorderStyle;

    public MainForm(PlayerStartupOptions options)
    {
        _options = options;
        _players = new PlayerSetController<PlayerPanel>(this, _dispatcher);
        Text = "FatzBomb.MediaControl WinForms test player";
        ClientSize = options.Players == 1 ? new Size(1120, 760) : new Size(1600, 1000);
        MinimumSize = new Size(900, 600);
        KeyPreview = true;

        _playersBox.Items.AddRange(["1", "2", "3", "4"]);
        _playersBox.SelectedIndex = options.Players - 1;
        foreach ((PlayerRendererRequest _, var label) in PlayerRendererSelection.Choices)
            _rendererBox.Items.Add(label);
        _rendererBox.SelectedIndex = Math.Max(0, PlayerRendererSelection.Choices.ToList().FindIndex(choice => choice.Request == options.RendererRequest));
        _playersBox.SelectedIndexChanged += OnLayoutChanged;
        _rendererBox.SelectedIndexChanged += OnLayoutChanged;
        _toolbar.Controls.AddRange(
        [
            ToolbarLabel("Players:"),
            _playersBox,
            ToolbarLabel("Renderer:"),
            _rendererBox,
            _info,
        ]);

        _grid.Layout += (_, _) => LayoutPanels();
        // The grid first: docking lays out the last-added control (the toolbar, at the top) first.
        Controls.Add(_grid);
        Controls.Add(_toolbar);
        KeyDown += OnFormKeyDown;
        Shown += OnShown;
        FormClosing += OnFormClosing;
    }

    private static Label ToolbarLabel(string text) => new() { Text = text, AutoSize = true, Margin = new Padding(8, 8, 3, 3) };

    private void OnShown(object? sender, EventArgs e)
    {
        if (_options.LightweightSurfacesWindow)
            ShowLightweightSurfaces();

        _ = ShowRendererSummaryAsync();
        _ = _players.StartAsync();
    }

    private void OnLayoutChanged(object? sender, EventArgs e)
    {
        // The boxes are set in the constructor: nothing is rebuilt before the first build.
        if (!_players.IsStarted)
            return;

        // The renderer for the panels created from now on; Program.RendererSelection resolves it off the UI thread.
        if (ReferenceEquals(sender, _rendererBox) && _rendererBox.SelectedIndex >= 0)
            Program.UseRenderer(_options, PlayerRendererSelection.Choices[_rendererBox.SelectedIndex].Request);
        _ = ShowRendererSummaryAsync();
        // A rebuild never throws: the controller reports failures through ReportFailure.
        _ = _players.RebuildAsync();
    }

    private async Task ShowRendererSummaryAsync()
    {
        Task<PlayerRendererSelection> pending = Program.RendererSelection;
        _info.Text = "resolving the renderer...";
        PlayerRendererSelection selection = await pending;
        // A later choice owns the text.
        if (ReferenceEquals(pending, Program.RendererSelection) && !IsDisposed)
            _info.Text = selection.Summary;
    }

    /// <summary>Stage 046A slice 2: the background and karaoke smoke window, with the startup renderer.</summary>
    private async void ShowLightweightSurfaces()
    {
        var selection = await Program.RendererSelection;
        if (_players.IsShuttingDown)
            return;

        var form = new LightweightSurfacesForm(
            () => WinFormsPlayerEngineFactory.CreatePlatformFactory(selection),
            WinFormsPlayerEngineFactory.ResolveNativePaths(_options),
            _options.AutoHideSurfaces,
            !_options.PreferFrameSinkSurface)
        {
            Owner = this,
        };
        form.Show();
    }

    // One panel, two side by side, or a 2x2 grid; in full screen only that panel.
    private void LayoutPanels()
    {
        Rectangle area = _grid.ClientRectangle;
        if (_players.FullScreenSlot is { } fullScreen)
        {
            fullScreen.Bounds = area;
            return;
        }

        IReadOnlyList<PlayerPanel> panels = _players.Slots;
        var columns = panels.Count == 1 ? 1 : 2;
        var rows = (panels.Count + columns - 1) / columns;
        for (var i = 0; i < panels.Count; i++)
        {
            int column = i % columns, row = i / columns;
            var x = area.Width * column / columns;
            var y = area.Height * row / rows;
            panels[i].Bounds = new Rectangle(x, y, (area.Width * (column + 1) / columns) - x, (area.Height * (row + 1) / rows) - y);
        }
    }

    int IPlayerSetHost<PlayerPanel>.RequestedPlayerCount => _playersBox.SelectedIndex + 1;

    PlayerPanel IPlayerSetHost<PlayerPanel>.CreateSlot(int index, bool atStartup) => new(index, _options, atStartup, _dispatcher, this);

    void IPlayerSetHost<PlayerPanel>.Attach(PlayerPanel slot) => _grid.Controls.Add(slot);

    void IPlayerSetHost<PlayerPanel>.Detach(PlayerPanel slot) => _grid.Controls.Remove(slot);

    void IPlayerSetHost<PlayerPanel>.Layout(IReadOnlyList<PlayerPanel> slots, PlayerPanel? fullScreen)
    {
        foreach (PlayerPanel panel in slots)
            panel.Visible = fullScreen is null || ReferenceEquals(panel, fullScreen);
        _toolbar.Visible = fullScreen is null;
        _grid.PerformLayout();
    }

    void IPlayerSetHost<PlayerPanel>.SetWindowFullScreen(bool fullScreen)
    {
        if (fullScreen)
        {
            _restoreWindowState = WindowState == FormWindowState.Minimized ? FormWindowState.Normal : WindowState;
            _restoreBorderStyle = FormBorderStyle;
            FormBorderStyle = FormBorderStyle.None;
            WindowState = FormWindowState.Normal;
            WindowState = FormWindowState.Maximized;
        }
        else
        {
            FormBorderStyle = _restoreBorderStyle;
            WindowState = _restoreWindowState;
        }
    }

    void IPlayerSetHost<PlayerPanel>.FallBackRenderer()
    {
        var choices = PlayerRendererSelection.Choices.ToList();
        PlayerRendererRequest current = _rendererBox.SelectedIndex >= 0 ? choices[_rendererBox.SelectedIndex].Request : _options.RendererRequest;
        if (PlayerRendererRules.NextAvailableAfterDeviceLoss(current, windows: true, request => choices.Exists(choice => choice.Request == request)) is not { } next)
        {
            _info.Text = "The renderer lost its GPU device and no other renderer is left.";
            return;
        }

        System.Diagnostics.Trace.WriteLine($"[Player.WinForms] The {current} renderer lost its GPU device for good; switching to {next} (ENG-050).");
        // The box's SelectedIndexChanged switches the renderer and rebuilds the players.
        _rendererBox.SelectedIndex = choices.FindIndex(choice => choice.Request == next);
    }

    void IPlayerSetHost<PlayerPanel>.ReportFailure(string what, Exception exception)
    {
        System.Diagnostics.Trace.WriteLine($"[Player.WinForms] {what}: {exception}");
        if (!IsDisposed)
            _info.Text = $"{what}: {exception.Message}";
    }

    private void OnFormKeyDown(object? sender, KeyEventArgs e)
    {
        if ((e.KeyCode == Keys.F11 && _players.HandleFullScreenKey()) || (e.KeyCode == Keys.Escape && _players.HandleEscapeKey()))
            e.Handled = true;
    }

    private async void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_players.IsShutDown)
            return;

        // Disposing an engine waits for admitted media operations: the controller waits for a toolbar
        // rebuild and the view models dispose off the UI thread; the form closes afterwards. A second
        // close request waits for the first.
        e.Cancel = true;
        if (await _players.ShutdownAsync())
            Close();
    }
}
