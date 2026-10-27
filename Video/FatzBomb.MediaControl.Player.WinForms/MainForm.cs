using FatzBomb.MediaControl.Player.Configuration;
using FatzBomb.MediaControl.Player.Shared;
using FatzBomb.MediaControl.Player.Shared.Presentation;

namespace FatzBomb.MediaControl.Player.WinForms;

/// <summary>
/// WinForms test player: 1-4 independent <see cref="PlayerPanel"/>s side by side
/// (<c>--players=1..4</c>, Stage 046A slice 3; one view model each, like the WPF and Avalonia
/// players), each fed a once-a-second process reading for its stats line. The toolbar changes the
/// player count and the renderer at run time, as in every test player: a change recreates the
/// players (loaded files are not kept). F11 or a double-click on a picture shows that player alone,
/// full screen; Escape leaves full screen.
/// </summary>
internal sealed class MainForm : Form
{
    private readonly PlayerStartupOptions _options;
    private readonly List<PlayerPanel> _panels = [];
    private readonly Panel _grid = new() { Dock = DockStyle.Fill };
    private readonly ProcessSampler _sampler = new();
    private readonly System.Windows.Forms.Timer _statsTimer = new() { Interval = 1000 };
    private readonly WinFormsPlayerDispatcher _dispatcher = new();
    private readonly FlowLayoutPanel _toolbar = new() { Dock = DockStyle.Top, AutoSize = true, WrapContents = false, Padding = new Padding(6, 4, 6, 4) };
    private readonly ComboBox _playersBox = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 60 };
    private readonly ComboBox _rendererBox = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220 };
    private readonly Label _info = new() { AutoSize = true, Margin = new Padding(16, 8, 3, 3), ForeColor = SystemColors.GrayText };
    private PlayerPanel? _fullScreenPanel;
    private FormWindowState _restoreWindowState;
    private FormBorderStyle _restoreBorderStyle;
    private bool _ready;
    private bool _firstBuild = true;
    private bool _rebuilding;
    private bool _rebuildAgain;
    private Task _rebuild = Task.CompletedTask;
    private bool _closing;
    private bool _closed;

    public MainForm(PlayerStartupOptions options)
    {
        _options = options;
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
        _statsTimer.Tick += OnStatsTick;
        KeyDown += OnFormKeyDown;
        Shown += OnShown;
        FormClosing += OnFormClosing;
    }

    private static Label ToolbarLabel(string text) => new() { Text = text, AutoSize = true, Margin = new Padding(8, 8, 3, 3) };

    private async void OnShown(object? sender, EventArgs e)
    {
        _statsTimer.Start();
        if (_options.LightweightSurfacesWindow)
            ShowLightweightSurfaces();

        _ready = true;
        await RebuildAsync();
    }

    private async void OnLayoutChanged(object? sender, EventArgs e)
    {
        if (!_ready)
            return;

        // The renderer for the panels created from now on; Program.RendererSelection resolves it off the UI thread.
        if (ReferenceEquals(sender, _rendererBox) && _rendererBox.SelectedIndex >= 0)
            Program.UseRenderer(_options, PlayerRendererSelection.Choices[_rendererBox.SelectedIndex].Request);
        await RebuildAsync();
    }

    private Task RebuildAsync()
    {
        if (_rebuilding)
        {
            _rebuildAgain = true;
            return _rebuild;
        }

        _rebuilding = true;
        _rebuild = RebuildLoopAsync();
        return _rebuild;
    }

    private async Task RebuildLoopAsync()
    {
        try
        {
            do
            {
                _rebuildAgain = false;
                await RebuildOnceAsync();
            }
            while (_rebuildAgain && !_closing);
        }
        finally
        {
            _rebuilding = false;
        }
    }

    private async Task RebuildOnceAsync()
    {
        ExitFullScreen();
        PlayerPanel[] old = [.. _panels];
        _panels.Clear();
        _grid.Controls.Clear();
        foreach (PlayerPanel panel in old)
        {
            await DisposePanelAsync(panel);
            panel.Dispose();
        }

        // Closing started while the old players were disposed: build nothing (the closing handler waits for this rebuild).
        if (_closing)
            return;

        var count = _playersBox.SelectedIndex + 1;
        for (var index = 1; index <= count; index++)
        {
            var panel = new PlayerPanel(index, _options, _firstBuild, _dispatcher, this);
            panel.FullScreenToggleRequested += OnFullScreenToggleRequested;
            _panels.Add(panel);
            _grid.Controls.Add(panel);
        }

        _firstBuild = false;
        _grid.PerformLayout();
        _ = ShowRendererSummaryAsync();

        // Created one after the other: native loading is process-wide and happens on the first.
        foreach (PlayerPanel panel in _panels.ToList())
        {
            if (_closing || _rebuildAgain || !_panels.Contains(panel))
                return;

            await panel.InitializeAsync();
        }
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

    private async Task DisposePanelAsync(PlayerPanel panel)
    {
        panel.FullScreenToggleRequested -= OnFullScreenToggleRequested;
        try
        {
            await panel.DisposeAsync();
        }
        catch (Exception ex)
        {
            // One player's failed teardown must not keep the others alive or the form open.
            System.Diagnostics.Trace.WriteLine($"[Player.WinForms] Closing a player failed: {ex}");
        }
    }

    /// <summary>Stage 046A slice 2: the background and karaoke smoke window, with the startup renderer.</summary>
    private async void ShowLightweightSurfaces()
    {
        var selection = await Program.RendererSelection;
        if (_closing)
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
        if (_fullScreenPanel is { } fullScreen)
        {
            fullScreen.Bounds = area;
            return;
        }

        var columns = _panels.Count == 1 ? 1 : 2;
        var rows = (_panels.Count + columns - 1) / columns;
        for (var i = 0; i < _panels.Count; i++)
        {
            int column = i % columns, row = i / columns;
            var x = area.Width * column / columns;
            var y = area.Height * row / rows;
            _panels[i].Bounds = new Rectangle(x, y, (area.Width * (column + 1) / columns) - x, (area.Height * (row + 1) / rows) - y);
        }
    }

    private void OnStatsTick(object? sender, EventArgs e)
    {
        ProcessSample sample = _sampler.Sample();
        foreach (PlayerPanel panel in _panels)
            panel.UpdateStatus(sample);
    }

    private void OnFullScreenToggleRequested(object? sender, EventArgs e)
    {
        if (sender is not PlayerPanel panel)
            return;

        if (ReferenceEquals(_fullScreenPanel, panel))
            ExitFullScreen();
        else
            EnterFullScreen(panel);
    }

    private void OnFormKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.F11 && _panels.Count > 0)
        {
            if (_fullScreenPanel is not null)
                ExitFullScreen();
            else
                EnterFullScreen(_panels.FirstOrDefault(static panel => panel.ContainsFocus) ?? _panels[0]);
            e.Handled = true;
        }
        else if (e.KeyCode == Keys.Escape && _fullScreenPanel is not null)
        {
            ExitFullScreen();
            e.Handled = true;
        }
    }

    private void EnterFullScreen(PlayerPanel panel)
    {
        ExitFullScreen();
        _fullScreenPanel = panel;
        _restoreWindowState = WindowState == FormWindowState.Minimized ? FormWindowState.Normal : WindowState;
        _restoreBorderStyle = FormBorderStyle;
        foreach (PlayerPanel other in _panels)
            other.Visible = ReferenceEquals(other, panel);
        _toolbar.Visible = false;
        FormBorderStyle = FormBorderStyle.None;
        WindowState = FormWindowState.Normal;
        WindowState = FormWindowState.Maximized;
        _grid.PerformLayout();
        panel.SetFullScreen(true);
    }

    private void ExitFullScreen()
    {
        if (_fullScreenPanel is not { } panel)
            return;

        _fullScreenPanel = null;
        panel.SetFullScreen(false);
        FormBorderStyle = _restoreBorderStyle;
        WindowState = _restoreWindowState;
        foreach (PlayerPanel other in _panels)
            other.Visible = true;
        _toolbar.Visible = true;
        _grid.PerformLayout();
    }

    private async void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_closed)
            return;

        // Disposing an engine waits for admitted media operations, so the view models run it off
        // the UI thread; the form closes afterwards. A second close request waits for the first.
        e.Cancel = true;
        if (_closing)
            return;

        _closing = true;
        _statsTimer.Stop();
        ExitFullScreen();
        // A toolbar rebuild may still be disposing the old players or starting the new ones: let it
        // finish (it stops at _closing) so no player outlives the window.
        try
        {
            await _rebuild;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[Player.WinForms] Rebuilding the players failed: {ex}");
        }

        foreach (PlayerPanel panel in _panels.ToList())
            await DisposePanelAsync(panel);

        _closed = true;
        Close();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _statsTimer.Dispose();
            _sampler.Dispose();
        }

        base.Dispose(disposing);
    }
}
