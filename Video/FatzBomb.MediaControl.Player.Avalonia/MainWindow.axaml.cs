using Avalonia.Controls;
using Avalonia.Threading;

namespace FatzBomb.MediaControl.Player.Avalonia;

/// <summary>
/// Hosts 1-4 independent <see cref="PlayerPanel"/>s side by side and feeds each a once-a-second
/// process reading for its status line. Changing the player count or renderer recreates the
/// players (loaded files are not kept).
/// </summary>
public partial class MainWindow : Window
{
    private readonly StartupOptions _options = StartupOptions.Current;
    private readonly IReadOnlyList<string> _nativePaths;
    private readonly List<PlayerPanel> _panels = [];
    private readonly ProcessSampler _sampler = new();
    private readonly DispatcherTimer _statsTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private IReadOnlyList<RendererOption> _renderers = [];
    private bool _ready;
    private bool _rebuilding;
    private bool _rebuildAgain;
    private bool _closing;
    private bool _firstBuild = true;

    public MainWindow()
    {
        InitializeComponent();
        _nativePaths = NativePaths.Resolve(_options);
        _statsTimer.Tick += OnStatsTick;
        Opened += OnOpened;
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        InfoText.Text = "probing renderers...";
        _renderers = await Task.Run(Renderers.Probe);

        foreach (RendererOption option in _renderers)
            RendererBox.Items.Add(new ComboBoxItem { Content = option.Label, IsEnabled = option.IsAvailable, Tag = option.Kind });

        RendererKind chosen = Renderers.Choose(_options.Renderer, _renderers);
        RendererBox.SelectedIndex = (int)chosen;
        PlayersBox.SelectedIndex = _options.Players - 1;
        InfoText.Text = $"{PlatformComposition.Name} platform | native: {string.Join(", ", _nativePaths)}";
        _ready = true;
        _statsTimer.Start();
        await RebuildAsync();
    }

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
        PlayerPanel[] old = [.. _panels];
        _panels.Clear();
        PlayersGrid.Children.Clear();
        foreach (PlayerPanel panel in old)
            await panel.DisposeAsync();

        var count = PlayersBox.SelectedIndex + 1;
        var kind = RendererBox.SelectedItem is ComboBoxItem { Tag: RendererKind selected } ? selected : RendererKind.Software;
        RendererOption option = _renderers.First(candidate => candidate.Kind == kind);
        PlayersGrid.Columns = count == 1 ? 1 : 2;

        for (var i = 0; i < count; i++)
        {
            var panel = new PlayerPanel(i + 1, option, _nativePaths);
            _panels.Add(panel);
            PlayersGrid.Children.Add(panel);
        }

        // Created one after the other: native loading is process-wide and happens on the first.
        for (var i = 0; i < _panels.Count && !_closing; i++)
            await _panels[i].InitializeAsync(_firstBuild && i == 0 ? _options.MediaPath : null);

        _firstBuild = false;
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
