using Avalonia.Controls;
using Avalonia.Input;
using FatzBomb.MediaControl.Player.Shared;
using FatzBomb.MediaControl.Player.Shared.Hosting;
using FatzBomb.MediaControl.Player.Shared.Presentation;

namespace FatzBomb.MediaControl.Player.Avalonia;

/// <summary>
/// Hosts 1-4 independent <see cref="PlayerPanel"/>s side by side (one shared view model each).
/// Changing the player count or renderer recreates the players (loaded files are not kept). F11 or
/// a double-click on a picture shows that player alone, full screen; Escape closes a flyout, then
/// leaves full screen. The rebuild, full screen, keys, stats tick and closing are the shared
/// <see cref="PlayerSetController{TSlot}"/>'s; this window creates and places the panels and owns
/// the window state.
/// </summary>
public partial class MainWindow : Window, IPlayerSetHost<PlayerPanel>
{
    private readonly PlayerStartupOptions _options = StartupOptions.Current;
    private readonly IReadOnlyList<string> _nativePaths;
    private readonly PlayerSetController<PlayerPanel> _players;
    private IReadOnlyList<RendererOption> _renderers = [];
    private IReadOnlyList<string> _startupWarnings = [];
    private WindowState _restoreWindowState;

    public MainWindow()
    {
        InitializeComponent();
        _nativePaths = NativePaths.Resolve(_options);
        _players = new PlayerSetController<PlayerPanel>(this, AvaloniaPlayerDispatcher.Instance);
        Opened += OnOpened;
        // F11 before any control sees it; Escape after, so an open drop-down or text box inside a
        // flyout handles its own Escape first (WPF's KeyDown bubbles too).
        AddHandler(KeyDownEvent, OnWindowKeyDown, global::Avalonia.Interactivity.RoutingStrategies.Tunnel);
        KeyDown += OnWindowEscapeKey;
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        try
        {
            await OpenAsync();
        }
        catch (Exception ex)
        {
            // An async void handler must not let an exception end the process.
            ReportFailure("Starting the players failed", ex);
        }
    }

    private async Task OpenAsync()
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
        if (_options.LightweightSurfacesWindow)
            ShowLightweightSurfaces(chosen);
        await _players.StartAsync();
    }

    /// <summary>Stage 046A slice 2: the background and karaoke smoke window, with the startup renderer.</summary>
    private void ShowLightweightSurfaces(RendererKind kind) =>
        new LightweightSurfacesWindow(
            () => PlatformComposition.Create(Renderers.Create(kind)),
            _nativePaths,
            !_options.PreferFrameSinkSurface,
            _options.AutoHideSurfaces).Show(this);

    // Before the first build (OpenAsync fills the boxes) the controller rebuilds nothing; a rebuild never throws.
    private void OnLayoutChanged(object? sender, SelectionChangedEventArgs e) => _ = _players.RebuildAsync();

    private void ReportFailure(string what, Exception ex)
    {
        System.Diagnostics.Trace.WriteLine($"[Player.Avalonia] {what}: {ex}");
        InfoText.Text = $"{what}: {ex.Message}";
    }

    int IPlayerSetHost<PlayerPanel>.RequestedPlayerCount => PlayersBox.SelectedIndex + 1;

    PlayerPanel IPlayerSetHost<PlayerPanel>.CreateSlot(int index, bool atStartup)
    {
        var kind = RendererBox.SelectedItem is ComboBoxItem { Tag: RendererKind selected } ? selected : RendererKind.Software;
        RendererOption option = _renderers.First(candidate => candidate.Kind == kind);
        // Only the first player of the first build gets the startup media and second screen.
        return new PlayerPanel(index, atStartup, option, _nativePaths, _options.ForBuild(index, atStartup), atStartup ? _startupWarnings : []);
    }

    void IPlayerSetHost<PlayerPanel>.Attach(PlayerPanel slot) => PlayersGrid.Children.Add(slot);

    void IPlayerSetHost<PlayerPanel>.Detach(PlayerPanel slot) => PlayersGrid.Children.Remove(slot);

    void IPlayerSetHost<PlayerPanel>.Layout(IReadOnlyList<PlayerPanel> slots, PlayerPanel? fullScreen)
    {
        Toolbar.IsVisible = fullScreen is null;
        foreach (PlayerPanel panel in slots)
            panel.IsVisible = fullScreen is null || ReferenceEquals(panel, fullScreen);
        PlayersGrid.Columns = fullScreen is not null || slots.Count == 1 ? 1 : 2;
    }

    void IPlayerSetHost<PlayerPanel>.SetWindowFullScreen(bool fullScreen)
    {
        if (fullScreen)
        {
            _restoreWindowState = WindowState;
            WindowState = WindowState.FullScreen;
        }
        else
        {
            WindowState = _restoreWindowState == WindowState.FullScreen ? WindowState.Normal : _restoreWindowState;
        }
    }

    void IPlayerSetHost<PlayerPanel>.ReportFailure(string what, Exception exception) => ReportFailure(what, exception);

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.F11 && _players.HandleFullScreenKey())
            e.Handled = true;
    }

    private void OnWindowEscapeKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && !e.Handled && _players.HandleEscapeKey())
            e.Handled = true;
    }

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (_players.IsShutDown)
            return;

        // Disposing a player blocks until its media operations finish: the controller waits for a
        // toolbar rebuild and disposes every player off the UI thread, then the window closes for
        // real. A second close request waits for the first.
        e.Cancel = true;
        if (await _players.ShutdownAsync())
            Close();
    }
}
