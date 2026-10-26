using FatzBomb.MediaControl.Core.Configuration;
using FatzBomb.MediaControl.Player.Shared;
using FatzBomb.MediaControl.Player.Shared.Hosting;
using FatzBomb.MediaControl.Rendering.Direct3D11;

namespace FatzBomb.MediaControl.Player.WinForms;

/// <summary>
/// The WinForms UI thread: the <see cref="SynchronizationContext"/> WinForms installs on the thread
/// that creates the first control.
/// </summary>
internal sealed class WinFormsPlayerDispatcher : IPlayerDispatcher
{
    private readonly SynchronizationContext _context;
    private readonly Thread _uiThread = Thread.CurrentThread;

    public WinFormsPlayerDispatcher() =>
        _context = SynchronizationContext.Current
            ?? throw new InvalidOperationException("Create the dispatcher on the UI thread after the first control.");

    public bool CheckAccess() => Thread.CurrentThread == _uiThread;

    public void Post(Action action) => _context.Post(static state => ((Action)state!)(), action);

    public IPlayerTimer CreateTimer(TimeSpan interval, Action tick) => new Timer(interval, tick);

    private sealed class Timer : IPlayerTimer
    {
        private readonly System.Windows.Forms.Timer _timer;
        private readonly Action _tick;

        public Timer(TimeSpan interval, Action tick)
        {
            _tick = tick;
            _timer = new System.Windows.Forms.Timer { Interval = Math.Max(1, (int)interval.TotalMilliseconds) };
            _timer.Tick += OnTick;
        }

        public void Start() => _timer.Start();

        public void Stop() => _timer.Stop();

        public void Dispose()
        {
            _timer.Stop();
            _timer.Tick -= OnTick;
            _timer.Dispose();
        }

        private void OnTick(object? sender, EventArgs e) => _tick();
    }
}

/// <summary>The WinForms clipboard and open-file dialog, owned by the main form.</summary>
internal sealed class WinFormsPlayerShell(IWin32Window owner) : IPlayerClipboard, IPlayerFilePicker
{
    public Task SetTextAsync(string text)
    {
        Clipboard.SetText(text);
        return Task.CompletedTask;
    }

    public Task<string?> PickFileAsync(string title, string filter)
    {
        using var dialog = new OpenFileDialog { Title = title, Filter = filter };
        return Task.FromResult(dialog.ShowDialog(owner) == DialogResult.OK ? dialog.FileName : null);
    }
}

/// <summary>
/// The engine for the WinForms player: Windows + Direct3D11 into the HWND <c>MediaSurface</c>.
/// <c>--renderer=auto</c> (the default: the GPU, else WARP), <c>gpu</c> or <c>warp</c>; the software
/// renderer needs a frame-sink surface, which the HWND surface is not, so it falls back to auto.
/// </summary>
internal sealed class WinFormsPlayerEngineFactory(PlayerStartupOptions options) : IPlayerEngineFactory
{
    private IMediaControlPlatformFactory? _platformFactory;
    private Direct3D11DeviceSelection _deviceSelection;

    public IReadOnlyList<string> NativeLibraryPaths { get; } = ResolveNativePaths(options);

    public NativeDependencyReport? NativeDependenciesAfterFailure => _platformFactory?.NativeDependencies;

    public Task<PlayerRendererInfo> ResolveRendererAsync()
    {
        List<(MediaDiagnosticLevel, string)> lines = [];
        var requested = options.Renderer?.Trim().ToLowerInvariant() ?? (options.SoftwareRenderer ? "software" : "auto");
        (_deviceSelection, VideoDecodingMethod decoding, var summary) = requested switch
        {
            "gpu" or "d3d11" or "direct3d11" or "hardware" => (Direct3D11DeviceSelection.Hardware, VideoDecodingMethod.Hardware, "Direct3D11 GPU (forced)"),
            "warp" => (Direct3D11DeviceSelection.Warp, VideoDecodingMethod.Software, "Direct3D11 WARP (forced)"),
            _ => (Direct3D11DeviceSelection.Auto, VideoDecodingMethod.Auto, "Direct3D11 (GPU, else WARP)"),
        };

        if (requested is "software" or "cpu")
            lines.Add((MediaDiagnosticLevel.Warning, "The WinForms surface is a native window: it needs Direct3D11, not the software renderer; using auto."));
        else if (requested is not ("auto" or "gpu" or "d3d11" or "direct3d11" or "hardware" or "warp"))
            lines.Add((MediaDiagnosticLevel.Warning, $"Unknown --renderer value '{options.Renderer}' (use auto, gpu or warp); using auto."));

        lines.Add((MediaDiagnosticLevel.Info, $"{summary}: Direct3D11 device selection {_deviceSelection}, {decoding} video decoding."));
        lines.Add((MediaDiagnosticLevel.Info, $"Native search paths: {(NativeLibraryPaths.Count == 0 ? "(none exist)" : string.Join("; ", NativeLibraryPaths))}"));
        return Task.FromResult(new PlayerRendererInfo(summary, decoding, RequiresFrameSinkSurface: false, lines));
    }

    public Task<MediaControlPlayer> CreatePlayerAsync(PlayerRendererInfo renderer, MediaPlayerConfiguration configuration)
    {
        var platform = new WindowsDirect3D11MediaControlPlatformFactory(new Direct3D11PresentationOptions { DeviceSelection = _deviceSelection });
        _platformFactory = platform;
        // Loading BASS and FFmpeg blocks, so the engine is created off the UI thread.
        return Task.Run(() => new MediaControlPlayer(platform, configuration, NativeLibraryPaths));
    }

    private static IReadOnlyList<string> ResolveNativePaths(PlayerStartupOptions options)
    {
        IReadOnlyList<string> defaults = WindowsNativeLibraryPaths.GetDefaultSearchPaths();
        if (defaults.Count == 0)
        {
            // A stable drop folder even before any binaries are supplied.
            var nativeFolder = Path.Combine(AppContext.BaseDirectory, WindowsNativeLibraryPaths.AppNativeRelativePath);
            _ = Directory.CreateDirectory(nativeFolder);
            defaults = [nativeFolder];
        }

        return PlayerNativePaths.Combine(options.NativeDirectories, defaults);
    }
}
