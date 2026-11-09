using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FatzMedia.Video.Core.Abstractions;
using FatzMedia.Video.Core.Configuration;
using FatzMedia.Video.Sample.Player.Shared.Presentation;
using FatzMedia.Video.Wpf;
using WpfPlayer = FatzMedia.Video.Wpf.Player;

namespace FatzMedia.Video.Sample.Player;

/// <summary>
/// The A/B swap demo (<c>--ab-demo=</c>, Stage 051A): two players on two stacked surfaces; the next
/// clip loads into the hidden one, and its <see cref="MediaSurface.FirstFrameReady"/> handler shows it,
/// hides the other and changes the panel above them in the same UI-thread turn. Every swap is traced
/// (<c>[ABDemo] swap i/N ready after x ms</c>) for <c>scripts/smoke-051A.ps1</c>.
/// </summary>
internal sealed class AbSwapDemoWindow : Window
{
    private static readonly TimeSpan FirstFrameTimeout = TimeSpan.FromSeconds(10);
    private static readonly Brush[] PanelColours = [Brushes.Crimson, Brushes.RoyalBlue];

    private readonly Func<IPlatformFactory> _createPlatformFactory;
    private readonly IReadOnlyList<string> _nativeLibraryPaths;
    private readonly AbSwapDemoOptions _options;
    private readonly IReadOnlyList<string> _clips;
    private readonly WpfPresentationMode _mode;
    private readonly MediaSurface[] _surfaces = new MediaSurface[2];
    private readonly WpfPlayer?[] _players = new WpfPlayer?[2];
    private readonly Border _panel = new() { Padding = new Thickness(16, 8, 16, 8), Background = Brushes.DimGray, VerticalAlignment = VerticalAlignment.Top };
    private readonly TextBlock _panelText = new() { Foreground = Brushes.White, FontSize = 28, Text = "A/B swap demo: starting..." };
    private bool _closed;

    public AbSwapDemoWindow(
        Func<IPlatformFactory> createPlatformFactory,
        IReadOnlyList<string> nativeLibraryPaths,
        AbSwapDemoOptions options,
        IReadOnlyList<string> clips,
        WpfPresentationMode mode,
        bool autoHide)
    {
        _createPlatformFactory = createPlatformFactory;
        _nativeLibraryPaths = nativeLibraryPaths;
        _options = options;
        _clips = clips;
        _mode = mode;
        Title = $"A/B swap demo (--ab-demo, {mode}, waiting surface {WaitingVisibility})";
        Width = 1280;
        Height = 760;
        Background = Brushes.Black;

        var stage = new Grid();
        for (var i = 0; i < _surfaces.Length; i++)
        {
            _surfaces[i] = new MediaSurface { PresentationMode = mode, AutoHide = autoHide, Visibility = WaitingVisibility };
            _ = stage.Children.Add(_surfaces[i]);
        }

        // In native mode the video windows cover WPF content, so the panel sits above the video area.
        _panel.Child = _panelText;
        var root = new DockPanel();
        DockPanel.SetDock(_panel, Dock.Top);
        _ = root.Children.Add(_panel);
        _ = root.Children.Add(stage);
        Content = root;

        Loaded += async (_, _) => await RunAsync();
        Closed += (_, _) => Shutdown();
    }

    private Visibility WaitingVisibility => _options.Collapsed ? Visibility.Collapsed : Visibility.Hidden;

    private static void Log(string message) => Trace.WriteLine($"[ABDemo] {message}");

    private async Task RunAsync()
    {
        Log($"start: {_clips.Count} clips, {_options.Swaps} swaps every {_options.Interval.TotalSeconds:0.#} s, {_mode}, waiting surface {WaitingVisibility}");
        if (_clips.Count == 0)
        {
            Log("done: no clips (give video files or a folder with --ab-demo=)");
            _panelText.Text = "No clips: give video files or a folder with --ab-demo=";
            return;
        }

        try
        {
            for (var i = 0; i < _players.Length; i++)
            {
                WpfPlayer player = await WpfPlayer.CreateAsync(MediaPlayerConfiguration.Auto, _nativeLibraryPaths, _createPlatformFactory());
                player.PlayOnLoad = true;
                player.LoopMedia = true;
                player.MediaDiagnostic += OnDiagnostic;
                _players[i] = player;
                _surfaces[i].Player = player;
            }
        }
        catch (Exception ex)
        {
            Log($"done: the players could not start: {ex.Message}");
            _panelText.Text = $"The players could not start: {ex.Message}";
            return;
        }

        List<double> ready = [];
        var missed = 0;
        var shown = -1;
        for (var swap = 1; swap <= _options.Swaps && !_closed; swap++)
        {
            var next = shown == 0 ? 1 : 0;
            var clip = _clips[(swap - 1) % _clips.Count];
            var name = Path.GetFileName(clip);
            var result = await SwapToAsync(next, shown, clip, swap);
            if (_closed)
                return;

            if (result is { } swapped)
            {
                ready.Add(swapped.Milliseconds);
                Log($"swap {swap}/{_options.Swaps} ready after {swapped.Milliseconds:F0} ms, surface {swapped.Size}: {name}");
                if (shown >= 0)
                    _ = _players[shown]!.StopAsync();
                shown = next;
            }
            else
            {
                missed++;
                Log($"swap {swap}/{_options.Swaps} MISSED: no first frame within {FirstFrameTimeout.TotalSeconds:0} s: {name}");
                _ = _players[next]!.StopAsync();
            }

            await Task.Delay(_options.Interval);
        }

        var summary = AbSwapDemoSummary.Describe(_options.Swaps, ready, missed);
        Log($"done: {summary}");
        _panelText.Text = $"Done: {summary}";
        // The main window's normal close path disposes its players and ends the process.
        if (_options.ExitWhenDone)
            (Owner ?? (Window)this).Close();
    }

    // Loads the clip into the hidden player; its first frame swaps the surfaces and the panel in one
    // UI-thread turn. Returns the load-to-ready time and the surface's pixel size then, or null when
    // no first frame came in time.
    private async Task<(double Milliseconds, string Size)?> SwapToAsync(int next, int shown, string clip, int swap)
    {
        MediaSurface incoming = _surfaces[next];
        var stopwatch = Stopwatch.StartNew();
        var swapped = new TaskCompletionSource<(double, string)>(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnFirstFrameReady(object? sender, EventArgs e)
        {
            incoming.FirstFrameReady -= OnFirstFrameReady;
            var elapsed = stopwatch.Elapsed.TotalMilliseconds;
            // The size the first frame was rendered for: a Collapsed surface has none (0x0, so 1x1).
            DpiScale dpi = VisualTreeHelper.GetDpi(incoming);
            var size = $"{Math.Round(incoming.ActualWidth * dpi.DpiScaleX)}x{Math.Round(incoming.ActualHeight * dpi.DpiScaleY)}";
            incoming.Visibility = Visibility.Visible;
            if (shown >= 0)
                _surfaces[shown].Visibility = WaitingVisibility;
            _panel.Background = PanelColours[swap % PanelColours.Length];
            _panelText.Text = $"Swap {swap}/{_options.Swaps}: {Path.GetFileName(clip)}";
            _ = swapped.TrySetResult((elapsed, size));
        }

        incoming.FirstFrameReady += OnFirstFrameReady;
        var loaded = await _players[next]!.LoadAsync(clip);
        if (!loaded)
        {
            incoming.FirstFrameReady -= OnFirstFrameReady;
            Log($"swap {swap}/{_options.Swaps}: could not load {clip}");
            return null;
        }

        Task finished = await Task.WhenAny(swapped.Task, Task.Delay(FirstFrameTimeout));
        if (finished == swapped.Task)
            return await swapped.Task;

        incoming.FirstFrameReady -= OnFirstFrameReady;
        return null;
    }

    private void OnDiagnostic(object? sender, MediaDiagnosticEventArgs e)
    {
        if (e.Level is MediaDiagnosticLevel.Warning or MediaDiagnosticLevel.Error)
            Log($"diagnostic [{e.Level}] {e.Category}: {e.Message}");
    }

    private void Shutdown()
    {
        _closed = true;
        for (var i = 0; i < _players.Length; i++)
        {
            _surfaces[i].Player = null;
            if (_players[i] is not { } player)
                continue;

            player.MediaDiagnostic -= OnDiagnostic;
            _players[i] = null;
            // Dispose blocks: off the UI thread.
            _ = Task.Run(player.Dispose);
        }
    }
}
