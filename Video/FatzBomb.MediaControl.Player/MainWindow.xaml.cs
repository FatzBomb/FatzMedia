using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using FatzBomb.MediaControl.Player.Shared.Presentation;

namespace FatzBomb.MediaControl.Player;

/// <summary>
/// Hosts 1-4 independent <see cref="PlayerView"/>s side by side (<c>--players=1..4</c>, Stage 046A
/// slice 3; one view model each, like the Avalonia and WinForms players) and feeds each a
/// once-a-second process reading for its stats line. F11 or a double-click on a picture shows that
/// player alone, full screen; Escape closes its flyout, then leaves full screen.
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
    private bool _closing;
    private bool _closed;

    public MainWindow()
    {
        InitializeComponent();

        var count = App.StartupOptions.Players;
        PlayersGrid.Columns = count == 1 ? 1 : 2;
        if (count > 1)
        {
            Width = 1600;
            Height = 1000;
        }

        for (var index = 1; index <= count; index++)
        {
            var player = new PlayerView(this, index);
            player.FullScreenToggleRequested += Player_FullScreenToggleRequested;
            _players.Add(player);
            _ = PlayersGrid.Children.Add(player);
        }

        _statsTimer.Tick += StatsTimer_Tick;
        Loaded += MainWindow_Loaded;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _statsTimer.Start();
        // Created one after the other: native loading is process-wide and happens on the first.
        foreach (PlayerView player in _players.ToList())
        {
            if (_closing)
                return;

            await player.InitializeAsync();
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
        foreach (PlayerView player in _players)
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

        _sampler.Dispose();
        _closed = true;
        Close();
    }
}
