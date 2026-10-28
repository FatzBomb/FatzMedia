using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using FatzBomb.MediaControl.Player.Configuration;
using FatzBomb.MediaControl.Player.Shared;
using FatzBomb.MediaControl.Player.Shared.Presentation;

namespace FatzBomb.MediaControl.Player;

/// <summary>
/// Hosts 1-4 independent <see cref="PlayerView"/>s side by side (<c>--players=1..4</c>, Stage 046A
/// slice 3; one view model each, like the Avalonia and WinForms players) and feeds each a
/// once-a-second process reading for its stats line. The toolbar changes the player count and the
/// renderer at run time, as in every test player: a change recreates the players (loaded files are
/// not kept). F11 or a double-click on a picture shows that player alone, full screen; Escape
/// closes its flyout, then leaves full screen.
/// </summary>
public partial class MainWindow : Window
{
    private readonly List<PlayerView> _players = [];
    private readonly ProcessSampler _sampler = new();
    private readonly DispatcherTimer _statsTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private PlayerView? _fullScreenPlayer;
    private WindowState _restoreWindowState;
    private WindowStyle _restoreWindowStyle;
    private ResizeMode _restoreResizeMode;
    private bool _restoreTopmost;
    private bool _ready;
    private bool _firstBuild = true;
    private bool _rebuilding;
    private bool _rebuildAgain;
    private Task _rebuild = Task.CompletedTask;
    private bool _closing;
    private bool _closed;

    public MainWindow()
    {
        InitializeComponent();

        if (App.StartupOptions.Players > 1)
        {
            Width = 1600;
            Height = 1000;
        }

        foreach ((PlayerRendererRequest request, var label) in PlayerRendererSelection.Choices)
            _ = RendererBox.Items.Add(new ComboBoxItem { Content = label, Tag = request });
        RendererBox.SelectedIndex = Math.Max(0, PlayerRendererSelection.Choices.ToList().FindIndex(static choice => choice.Request == App.StartupOptions.RendererRequest));
        PlayersBox.SelectedIndex = App.StartupOptions.Players - 1;

        _statsTimer.Tick += StatsTimer_Tick;
        Loaded += MainWindow_Loaded;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _statsTimer.Start();
        _ready = true;
        await RebuildReportingFailureAsync();
    }

    private async void Layout_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready)
            return;

        // The renderer for the players created from now on; App.RendererSelection resolves it off the UI thread.
        if (ReferenceEquals(sender, RendererBox) && RendererBox.SelectedItem is ComboBoxItem { Tag: PlayerRendererRequest request })
            App.UseRenderer(request);
        await RebuildReportingFailureAsync();
    }

    // An async void handler must not let a failed rebuild end the process.
    private async Task RebuildReportingFailureAsync()
    {
        try
        {
            await RebuildAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[Player] Rebuilding the players failed: {ex}");
            InfoText.Text = $"Rebuilding the players failed: {ex.Message}";
        }
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
        PlayerView[] old = [.. _players];
        _players.Clear();
        PlayersGrid.Children.Clear();
        foreach (PlayerView player in old)
            await DisposePlayerAsync(player);

        // Closing started while the old players were disposed: build nothing (OnClosing waits for this rebuild).
        if (_closing)
            return;

        var count = PlayersBox.SelectedIndex + 1;
        PlayersGrid.Columns = count == 1 ? 1 : 2;
        for (var index = 1; index <= count; index++)
        {
            var player = new PlayerView(this, index, _firstBuild);
            player.FullScreenToggleRequested += Player_FullScreenToggleRequested;
            _players.Add(player);
            _ = PlayersGrid.Children.Add(player);
        }

        _firstBuild = false;
        _ = ShowRendererSummaryAsync();

        // Created one after the other: native loading is process-wide and happens on the first.
        foreach (PlayerView player in _players.ToList())
        {
            if (_closing || _rebuildAgain || !_players.Contains(player))
                return;

            await player.InitializeAsync();
        }
    }

    private async Task ShowRendererSummaryAsync()
    {
        Task<PlayerRendererSelection> pending = App.RendererSelection;
        InfoText.Text = "resolving the renderer...";
        PlayerRendererSelection selection = await pending;
        // A later choice owns the text.
        if (ReferenceEquals(pending, App.RendererSelection))
            InfoText.Text = selection.Summary;
    }

    private async Task DisposePlayerAsync(PlayerView player)
    {
        player.FullScreenToggleRequested -= Player_FullScreenToggleRequested;
        try
        {
            await player.DisposeAsync();
        }
        catch (Exception ex)
        {
            // One player's failed teardown must not keep the others alive or the window open.
            System.Diagnostics.Trace.WriteLine($"[Player] Closing a player failed: {ex}");
        }
    }

    private void StatsTimer_Tick(object? sender, EventArgs e)
    {
        ProcessSample sample = _sampler.Sample();
        foreach (PlayerView player in _players)
            player.UpdateStatus(sample);
    }

    private void Player_FullScreenToggleRequested(object? sender, EventArgs e)
    {
        if (sender is not PlayerView player)
            return;

        if (ReferenceEquals(_fullScreenPlayer, player))
            ExitFullScreen();
        else
            EnterFullScreen(player);
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F11 && _players.Count > 0)
        {
            if (_fullScreenPlayer is not null)
                ExitFullScreen();
            else
                EnterFullScreen(FocusedPlayer() ?? _players[0]);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            // The full-screen player's flyout, else the focused player's, else any open one.
            PlayerView? target = _fullScreenPlayer
                ?? _players.Where(static player => player.ViewModel.ActiveFlyout != Shared.ViewModels.PlayerFlyout.None)
                    .OrderByDescending(static player => player.IsKeyboardFocusWithin)
                    .FirstOrDefault();
            if (target?.CloseFlyout() == true)
                e.Handled = true;
            else if (_fullScreenPlayer is not null)
            {
                ExitFullScreen();
                e.Handled = true;
            }
        }
    }

    private PlayerView? FocusedPlayer() => _players.FirstOrDefault(static player => player.IsKeyboardFocusWithin);

    private void EnterFullScreen(PlayerView player)
    {
        ExitFullScreen();
        _fullScreenPlayer = player;
        _restoreWindowState = WindowState == WindowState.Minimized ? WindowState.Normal : WindowState;
        _restoreWindowStyle = WindowStyle;
        _restoreResizeMode = ResizeMode;
        _restoreTopmost = Topmost;

        foreach (PlayerView other in _players)
            other.Visibility = ReferenceEquals(other, player) ? Visibility.Visible : Visibility.Collapsed;
        PlayersGrid.Columns = 1;
        Toolbar.Visibility = Visibility.Collapsed;

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        // Maximizing again after the style change makes the borderless window cover the taskbar too.
        WindowState = WindowState.Normal;
        WindowState = WindowState.Maximized;
        player.SetFullScreen(true);
    }

    private void ExitFullScreen()
    {
        if (_fullScreenPlayer is not { } player)
            return;

        _fullScreenPlayer = null;
        player.SetFullScreen(false);
        foreach (PlayerView other in _players)
            other.Visibility = Visibility.Visible;
        PlayersGrid.Columns = _players.Count == 1 ? 1 : 2;
        Toolbar.Visibility = Visibility.Visible;

        WindowState = WindowState.Normal;
        WindowStyle = _restoreWindowStyle;
        ResizeMode = _restoreResizeMode;
        Topmost = _restoreTopmost;
        WindowState = _restoreWindowState;
    }

    protected override async void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (_closed)
            return;

        // Disposing an engine blocks until its media operations finish: the view models do it off
        // the UI thread, then the window closes for real. A second close request waits for the first.
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
            System.Diagnostics.Trace.WriteLine($"[Player] Rebuilding the players failed: {ex}");
        }

        PlayerView[] players = [.. _players];
        _players.Clear();
        foreach (PlayerView player in players)
            await DisposePlayerAsync(player);

        _sampler.Dispose();
        _closed = true;
        Close();
    }
}
