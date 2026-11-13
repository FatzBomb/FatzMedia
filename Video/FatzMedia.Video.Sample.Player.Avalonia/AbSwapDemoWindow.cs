using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using FatzMedia.Video.Avalonia;
using FatzMedia.Video.Core.Configuration;
using FatzMedia.Video.Sample.Player.Shared.Presentation;
using AvaloniaPlayer = FatzMedia.Video.Avalonia.Player;

namespace FatzMedia.Video.Sample.Player.Avalonia;

/// <summary>
/// The A/B swap demo (<c>--ab-demo=</c>, Stage 051A; the WPF player has the same window): two players on two
/// stacked surfaces; the next clip loads into the hidden one, and its <see cref="MediaSurface.FirstFrameReady"/>
/// handler shows it, hides the other and changes the panel above them in the same UI-thread turn. Every swap is
/// traced (<c>[ABDemo] swap i/N ready after x ms</c>) for <c>scripts/smoke-051A.ps1</c>. The waiting surface is
/// hidden with <c>Opacity = 0</c> (an invisible control is not laid out, so its first frame would be the wrong
/// size); <c>--ab-collapsed</c> hides it with <c>IsVisible = false</c> instead, the counter-check. The surfaces
/// are always composited, so <c>--ab-mode=native</c> has no meaning here.
/// </summary>
sealed class AbSwapDemoWindow : Window
{
    static readonly TimeSpan FirstFrameTimeout = TimeSpan.FromSeconds(10);
    static readonly IBrush[] PanelColours = [Brushes.Crimson, Brushes.RoyalBlue];

    readonly Func<IPlatformFactory> _createPlatformFactory;
    readonly IReadOnlyList<string> _nativeLibraryPaths;
    readonly AbSwapDemoOptions _options;
    readonly IReadOnlyList<string> _clips;
    readonly MediaSurface[] _surfaces = new MediaSurface[2];
    readonly AvaloniaPlayer?[] _players = new AvaloniaPlayer?[2];
    readonly Border _panel = new() { Padding = new Thickness(16, 8), Background = Brushes.DimGray, VerticalAlignment = VerticalAlignment.Top };
    readonly TextBlock _panelText = new() { Foreground = Brushes.White, FontSize = 28, Text = "A/B swap demo: starting..." };
    bool _closed;

    public AbSwapDemoWindow(
        Func<IPlatformFactory> createPlatformFactory,
        IReadOnlyList<string> nativeLibraryPaths,
        AbSwapDemoOptions options,
        IReadOnlyList<string> clips,
        bool preferGpuSurface,
        bool autoHide)
    {
        _createPlatformFactory = createPlatformFactory;
        _nativeLibraryPaths = nativeLibraryPaths;
        _options = options;
        _clips = clips;
        Title = $"A/B swap demo (--ab-demo, waiting surface {WaitingText})";
        Width = 1280;
        Height = 760;
        Background = Brushes.Black;

        var stage = new Grid();
        for (var i = 0; i < _surfaces.Length; i++)
        {
            _surfaces[i] = new MediaSurface { PreferGpuSharedSurface = preferGpuSurface, AutoHide = autoHide };
            Hide(_surfaces[i]);
            stage.Children.Add(_surfaces[i]);
        }

        _panel.Child = _panelText;
        var root = new DockPanel();
        DockPanel.SetDock(_panel, Dock.Top);
        root.Children.Add(_panel);
        root.Children.Add(stage);
        Content = root;

        Opened += async (_, _) => await RunAsync();
        Closed += (_, _) => Shutdown();
    }

    string WaitingText => _options.Collapsed ? "IsVisible=false" : "Opacity=0";

    void Hide(MediaSurface surface)
    {
        if (_options.Collapsed)
            surface.IsVisible = false;
        else
            surface.Opacity = 0;
    }

    static void Show(MediaSurface surface)
    {
        surface.IsVisible = true;
        surface.Opacity = 1;
    }

    static void Log(string message) => Trace.WriteLine($"[ABDemo] {message}");

    async Task RunAsync()
    {
        Log($"start: {_clips.Count} clips, {_options.Swaps} swaps every {_options.Interval.TotalSeconds:0.#} s, composited, waiting surface {WaitingText}");
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
                var player = await AvaloniaPlayer.CreateAsync(MediaPlayerConfiguration.Auto, _nativeLibraryPaths, _createPlatformFactory());
                if (_closed)
                {
                    // The window closed while the player started: Shutdown has run, so release it here.
                    _ = Task.Run(player.Dispose);
                    return;
                }

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
            (Owner as Window ?? this).Close();
    }

    // Loads the clip into the hidden player; its first frame swaps the surfaces and the panel in one
    // UI-thread turn. Returns the load-to-ready time and the surface's pixel size then, or null when
    // no first frame came in time.
    async Task<(double Milliseconds, string Size)?> SwapToAsync(int next, int shown, string clip, int swap)
    {
        var incoming = _surfaces[next];
        var stopwatch = Stopwatch.StartNew();
        var swapped = new TaskCompletionSource<(double, string)>(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnFirstFrameReady(object? sender, EventArgs e)
        {
            incoming.FirstFrameReady -= OnFirstFrameReady;
            var elapsed = stopwatch.Elapsed.TotalMilliseconds;
            // The size the first frame was rendered for: an IsVisible=false surface has none.
            var size = $"{incoming.AttachedPixelSize.Width}x{incoming.AttachedPixelSize.Height}";
            Show(incoming);
            if (shown >= 0)
                Hide(_surfaces[shown]);
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

        try
        {
            return await swapped.Task.WaitAsync(FirstFrameTimeout);
        }
        catch (TimeoutException)
        {
            // No first frame in time: give up on this swap.
        }

        incoming.FirstFrameReady -= OnFirstFrameReady;
        return null;
    }

    void OnDiagnostic(object? sender, MediaDiagnosticEventArgs e)
    {
        if (e.Level is MediaDiagnosticLevel.Warning or MediaDiagnosticLevel.Error)
            Log($"diagnostic [{e.Level}] {e.Category}: {e.Message}");
    }

    void Shutdown()
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
