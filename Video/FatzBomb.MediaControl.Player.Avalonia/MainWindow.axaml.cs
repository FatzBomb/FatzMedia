using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using FatzBomb.MediaControl.Player.Shared;
using FatzBomb.MediaControl.Player.Shared.Presentation;

namespace FatzBomb.MediaControl.Player.Avalonia;

/// <summary>
/// Hosts 1-4 independent <see cref="PlayerPanel"/>s side by side (one shared view model each) and
/// feeds each a once-a-second process reading for its status line. Changing the player count or
/// renderer recreates the players (loaded files are not kept). F11 or a double-click on a picture
/// shows that player alone, full screen; Escape closes its flyout, then leaves full screen.
/// </summary>
public partial class MainWindow : Window
{
    private readonly PlayerStartupOptions _options = StartupOptions.Current;
    private readonly IReadOnlyList<string> _nativePaths;
    private readonly List<PlayerPanel> _panels = [];
    private readonly ProcessSampler _sampler = new();
    private readonly DispatcherTimer _statsTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private IReadOnlyList<RendererOption> _renderers = [];
    private IReadOnlyList<string> _startupWarnings = [];
    private bool _ready;
    private bool _rebuilding;
    private bool _rebuildAgain;
    private bool _closing;
    private bool _firstBuild = true;
    private PlayerPanel? _fullScreenPanel;
    private WindowState _restoreWindowState;

    public MainWindow()
    {
        InitializeComponent();
        _nativePaths = NativePaths.Resolve(_options);
        _statsTimer.Tick += OnStatsTick;
        Opened += OnOpened;
        AddHandler(KeyDownEvent, OnWindowKeyDown, global::Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        InfoText.Text = "probing renderers...";
        _renderers = await Task.Run(Renderers.Probe);

        foreach (RendererOption option in _renderers)
            RendererBox.Items.Add(new ComboBoxItem { Content = option.Label, IsEnabled = option.IsAvailable, Tag = option.Kind });

        (RendererKind chosen, var rendererWarning) = Renderers.Choose(_options.RendererRequest, _renderers);
        RendererBox.SelectedIndex = _renderers.ToList().FindIndex(option => option.Kind == chosen);
        // The command line's problems, logged by the first build's players (Stage 046A slice 3).
        List<string> warnings = [.. _options.DescribeUnknownValues()];
        if (rendererWarning is not null)
            warnings.Add(rendererWarning);
        if (_options.HdrOutput)
        {
            warnings.Add("--hdr-output is not available in the Avalonia player: its surfaces are composited (shared GPU image or "
                + "frame sink), and HDR output needs a Direct3D11 native window surface (the WPF and WinForms players). Ignored.");
        }

        _startupWarnings = warnings;
        PlayersBox.SelectedIndex = _options.Players - 1;
        InfoText.Text = $"{PlatformComposition.Name} platform | native: {string.Join(", ", _nativePaths)}";
        _ready = true;
        _statsTimer.Start();
        if (_options.LightweightSurfacesWindow)
            ShowLightweightSurfaces(chosen);
        await RebuildAsync();
    }

    /// <summary>Stage 046A slice 2: the background and karaoke smoke window, with the startup renderer.</summary>
    private void ShowLightweightSurfaces(RendererKind kind) =>
        new LightweightSurfacesWindow(
            () => PlatformComposition.Create(Renderers.Create(kind)),
            _nativePaths,
            !_options.PreferFrameSinkSurface,
            _options.AutoHideSurfaces).Show(this);

    private async void OnLayoutChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_ready)
            await RebuildAsync();
    }

    private async Task RebuildAsync()
    {
        if (_rebuilding)
        {
            _rebuildAgain = true;
            return;
        }

        _rebuilding = true;
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
        PlayersGrid.Children.Clear();
        foreach (PlayerPanel panel in old)
        {
            panel.FullScreenToggleRequested -= OnFullScreenToggleRequested;
            await panel.DisposeAsync();
        }

        // Closed while the old players were disposed: OnClosing has already run, so build nothing it would miss.
        if (_closing)
            return;

        var count = PlayersBox.SelectedIndex + 1;
        var kind = RendererBox.SelectedItem is ComboBoxItem { Tag: RendererKind selected } ? selected : RendererKind.Software;
        RendererOption option = _renderers.First(candidate => candidate.Kind == kind);
        PlayersGrid.Columns = count == 1 ? 1 : 2;

        for (var i = 0; i < count; i++)
        {
            // Only the first player of the first build gets the startup media.
            var panel = new PlayerPanel(
                i + 1,
                option,
                _nativePaths,
                _firstBuild ? _options.ForPlayer(i + 1) : _options.WithoutStartupMedia(),
                _firstBuild ? _startupWarnings : []);
            panel.FullScreenToggleRequested += OnFullScreenToggleRequested;
            _panels.Add(panel);
            PlayersGrid.Children.Add(panel);
        }

        // Created one after the other: native loading is process-wide and happens on the first.
        for (var i = 0; i < _panels.Count && !_closing; i++)
            await _panels[i].InitializeAsync();

        _firstBuild = false;
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

    private void EnterFullScreen(PlayerPanel panel)
    {
        ExitFullScreen();
        _fullScreenPanel = panel;
        _restoreWindowState = WindowState;
        Toolbar.IsVisible = false;
        foreach (PlayerPanel other in _panels)
            other.IsVisible = ReferenceEquals(other, panel);
        PlayersGrid.Columns = 1;
        panel.SetFullScreen(true);
        WindowState = WindowState.FullScreen;
    }

    private void ExitFullScreen()
    {
        if (_fullScreenPanel is not { } panel)
            return;

        _fullScreenPanel = null;
        panel.SetFullScreen(false);
        foreach (PlayerPanel other in _panels)
            other.IsVisible = true;
        PlayersGrid.Columns = _panels.Count == 1 ? 1 : 2;
        Toolbar.IsVisible = true;
        WindowState = _restoreWindowState == WindowState.FullScreen ? WindowState.Normal : _restoreWindowState;
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.F11 && _panels.Count > 0)
        {
            if (_fullScreenPanel is not null)
                ExitFullScreen();
            else
                EnterFullScreen(_panels.FirstOrDefault(static panel => panel.IsKeyboardFocusWithin) ?? _panels[0]);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            if (_fullScreenPanel?.CloseFlyout() == true)
                e.Handled = true;
            else if (_fullScreenPanel is not null)
            {
                ExitFullScreen();
                e.Handled = true;
            }
        }
    }

    private void OnStatsTick(object? sender, EventArgs e)
    {
        ProcessSample sample = _sampler.Sample();
        foreach (PlayerPanel panel in _panels)
            panel.UpdateStatus(sample);
    }

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (_closing)
            return;

        // Disposing a player blocks until its media operations finish: do it off the UI thread,
        // then close for real.
        _closing = true;
        e.Cancel = true;
        _statsTimer.Stop();
        PlayerPanel[] panels = [.. _panels];
        _panels.Clear();
        foreach (PlayerPanel panel in panels)
            await panel.DisposeAsync();

        _sampler.Dispose();
        Close();
    }
}
