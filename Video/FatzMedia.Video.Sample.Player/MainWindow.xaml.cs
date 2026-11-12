using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FatzMedia.Video.Sample.Player.Configuration;
using FatzMedia.Video.Sample.Player.Hosting;
using FatzMedia.Video.Sample.Player.Shared;
using FatzMedia.Video.Sample.Player.Shared.Hosting;
using FatzMedia.Video.Sample.Player.Shared.Presentation;
using FatzMedia.Video.Wpf;

namespace FatzMedia.Video.Sample.Player;

/// <summary>
/// Hosts 1-4 independent <see cref="PlayerView"/>s side by side (<c>--players=1..4</c>, Stage 046A
/// slice 3; one view model each, like the Avalonia and WinForms players). The toolbar changes the
/// player count and the renderer at run time, as in every test player: a change recreates the
/// players (loaded files are not kept). F11 or a double-click on a picture shows that player alone,
/// full screen; Escape closes a flyout, then leaves full screen. The rebuild, full screen, keys,
/// stats tick and closing are the shared <see cref="PlayerSetController{TSlot}"/>'s; this window
/// creates and places the views and owns the window state (borderless, topmost, maximized).
/// </summary>
public partial class MainWindow : Window, IPlayerSetHost<PlayerView>
{
    readonly PlayerSetController<PlayerView> _players;
    readonly WindowFullScreen _fullScreen;

    public MainWindow()
    {
        InitializeComponent();
        _fullScreen = new WindowFullScreen(this);
        _players = new PlayerSetController<PlayerView>(this, new WpfPlayerDispatcher());

        if (App.StartupOptions.Players > 1)
        {
            Width = 1600;
            Height = 1000;
        }

        foreach ((var request, var label) in PlayerRendererSelection.Choices)
            _ = RendererBox.Items.Add(new ComboBoxItem { Content = label, Tag = request });
        RendererBox.SelectedIndex = Math.Max(0, PlayerRendererSelection.Choices.ToList().FindIndex(static choice => choice.Request == App.StartupOptions.RendererRequest));
        PlayersBox.SelectedIndex = App.StartupOptions.Players - 1;

        Loaded += MainWindow_Loaded;
    }

    void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _ = ShowRendererSummaryAsync();
        _ = _players.StartAsync();
    }

    void Layout_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // The boxes are set in the constructor: nothing is rebuilt before the first build.
        if (!_players.IsStarted)
            return;

        // The renderer for the players created from now on; App.RendererSelection resolves it off the UI thread.
        if (ReferenceEquals(sender, RendererBox) && RendererBox.SelectedItem is ComboBoxItem { Tag: PlayerRendererRequest request })
            App.UseRenderer(request);
        _ = ShowRendererSummaryAsync();
        // A rebuild never throws: the controller reports failures through ReportFailure.
        _ = _players.RebuildAsync();
    }

    async Task ShowRendererSummaryAsync()
    {
        var pending = App.RendererSelection;
        InfoText.Text = "resolving the renderer...";
        var selection = await pending;
        // A later choice owns the text.
        if (ReferenceEquals(pending, App.RendererSelection))
            InfoText.Text = selection.Summary;
    }

    int IPlayerSetHost<PlayerView>.RequestedPlayerCount => PlayersBox.SelectedIndex + 1;

    PlayerView IPlayerSetHost<PlayerView>.CreateSlot(int index, bool atStartup) => new(this, index, atStartup);

    void IPlayerSetHost<PlayerView>.Attach(PlayerView slot) => _ = PlayersGrid.Children.Add(slot);

    void IPlayerSetHost<PlayerView>.Detach(PlayerView slot) => PlayersGrid.Children.Remove(slot);

    void IPlayerSetHost<PlayerView>.Layout(IReadOnlyList<PlayerView> slots, PlayerView? fullScreen)
    {
        foreach (var player in slots)
            player.Visibility = fullScreen is null || ReferenceEquals(player, fullScreen) ? Visibility.Visible : Visibility.Collapsed;
        PlayersGrid.Columns = fullScreen is not null || slots.Count == 1 ? 1 : 2;
        Toolbar.Visibility = fullScreen is null ? Visibility.Visible : Visibility.Collapsed;
    }

    void IPlayerSetHost<PlayerView>.SetWindowFullScreen(bool fullScreen)
    {
        if (fullScreen)
            _fullScreen.Enter(topmost: true);
        else
            _fullScreen.Exit();
    }

    void IPlayerSetHost<PlayerView>.FallBackRenderer()
    {
        var current = RendererBox.SelectedItem is ComboBoxItem { Tag: PlayerRendererRequest selected } ? selected : App.StartupOptions.RendererRequest;
        var choices = PlayerRendererSelection.Choices.ToList();
        if (PlayerRendererRules.NextAvailableAfterDeviceLoss(current, windows: true, request => choices.Exists(choice => choice.Request == request)) is not { } next)
        {
            InfoText.Text = "The renderer lost its GPU device and no other renderer is left.";
            return;
        }

        System.Diagnostics.Trace.WriteLine($"[Player] The {current} renderer lost its GPU device for good; switching to {next} (ENG-050).");
        // The box's SelectionChanged switches the renderer and rebuilds the players.
        RendererBox.SelectedIndex = choices.FindIndex(choice => choice.Request == next);
    }

    void IPlayerSetHost<PlayerView>.ReportFailure(string what, Exception exception)
    {
        System.Diagnostics.Trace.WriteLine($"[Player] {what}: {exception}");
        InfoText.Text = $"{what}: {exception.Message}";
    }

    // F11 before any control sees it (a focused control could handle it and the window would never see
    // it: ENG-044); Escape after, so an open drop-down or text box handles its own Escape first.
    void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F11 && _players.HandleFullScreenKey())
            e.Handled = true;
    }

    void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _players.HandleEscapeKey())
            e.Handled = true;
    }

    protected override async void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (_players.IsShutDown)
            return;

        // Disposing an engine blocks until its media operations finish: the controller waits for a
        // toolbar rebuild and the view models dispose off the UI thread, then the window closes for
        // real. A second close request waits for the first.
        e.Cancel = true;
        if (await _players.ShutdownAsync())
            Close();
    }
}
