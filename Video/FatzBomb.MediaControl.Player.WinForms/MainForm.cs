using FatzBomb.MediaControl.Player.Shared;
using FatzBomb.MediaControl.Player.Shared.Presentation;

namespace FatzBomb.MediaControl.Player.WinForms;

/// <summary>
/// WinForms test player: 1-4 independent <see cref="PlayerPanel"/>s side by side
/// (<c>--players=1..4</c>, Stage 046A slice 3; one view model each, like the WPF and Avalonia
/// players), each fed a once-a-second process reading for its stats line. F11 or a double-click on
/// a picture shows that player alone, full screen; Escape leaves full screen.
/// </summary>
internal sealed class MainForm : Form
{
    private readonly PlayerStartupOptions _options;
    private readonly List<PlayerPanel> _panels = [];
    private readonly Panel _grid = new() { Dock = DockStyle.Fill };
    private readonly ProcessSampler _sampler = new();
    private readonly System.Windows.Forms.Timer _statsTimer = new() { Interval = 1000 };
    private PlayerPanel? _fullScreenPanel;
    private FormWindowState _restoreWindowState;
    private FormBorderStyle _restoreBorderStyle;
    private bool _closing;
    private bool _closed;

    public MainForm(PlayerStartupOptions options)
    {
        _options = options;
        Text = "FatzBomb.MediaControl WinForms test player";
        ClientSize = options.Players == 1 ? new Size(1120, 760) : new Size(1600, 1000);
        MinimumSize = new Size(900, 600);
        KeyPreview = true;

        var dispatcher = new WinFormsPlayerDispatcher();
        for (var index = 1; index <= options.Players; index++)
        {
            var panel = new PlayerPanel(index, options, dispatcher, this);
            panel.FullScreenToggleRequested += OnFullScreenToggleRequested;
            _panels.Add(panel);
            _grid.Controls.Add(panel);
        }

        _grid.Layout += (_, _) => LayoutPanels();
        Controls.Add(_grid);
        _statsTimer.Tick += OnStatsTick;
        KeyDown += OnFormKeyDown;
        Shown += OnShown;
        FormClosing += OnFormClosing;
    }

    private async void OnShown(object? sender, EventArgs e)
    {
        _statsTimer.Start();
        if (_options.LightweightSurfacesWindow)
            ShowLightweightSurfaces();

        // Created one after the other: native loading is process-wide and happens on the first.
        foreach (PlayerPanel panel in _panels.ToList())
        {
            if (_closing)
                return;

            await panel.InitializeAsync();
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
        foreach (PlayerPanel panel in _panels)
        {
            panel.FullScreenToggleRequested -= OnFullScreenToggleRequested;
            await panel.DisposeAsync();
        }

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
