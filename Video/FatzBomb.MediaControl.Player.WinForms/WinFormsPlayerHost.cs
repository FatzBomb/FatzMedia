using FatzBomb.MediaControl.Core.Configuration;
using FatzBomb.MediaControl.Player.Shared;
using FatzBomb.MediaControl.Player.Shared.Hosting;
using FatzBomb.MediaControl.Rendering.Direct3D11;
using FatzBomb.MediaControl.Rendering.Software;

namespace FatzBomb.MediaControl.Player.WinForms;

/// <summary>
/// The WinForms UI thread: the <see cref="WindowsFormsSynchronizationContext"/> of the thread that
/// creates the dispatcher.
/// </summary>
internal sealed class WinFormsPlayerDispatcher : IPlayerDispatcher
{
    private readonly SynchronizationContext _context;
    private readonly Thread _uiThread = Thread.CurrentThread;

    /// <summary>Call on the UI thread (normally from the main form's constructor).</summary>
    public WinFormsPlayerDispatcher() =>
        _context = SynchronizationContext.Current as WindowsFormsSynchronizationContext ?? new WindowsFormsSynchronizationContext();

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
/// <c>--renderer=auto</c> (the default: the GPU, else WARP), <c>gpu</c> or <c>warp</c>.
/// <c>--renderer=software</c> (or <c>--software</c>) uses the software renderer, which the surface
/// shows through its frame-sink path (Stage 046A).
/// </summary>
internal sealed class WinFormsPlayerEngineFactory(PlayerStartupOptions options) : IPlayerEngineFactory
{
    private IMediaControlPlatformFactory? _platformFactory;
    private Direct3D11DeviceSelection _deviceSelection;
    private bool _software;

    public IReadOnlyList<string> NativeLibraryPaths { get; } = ResolveNativePaths(options);

    public NativeDependencyReport? NativeDependenciesAfterFailure => _platformFactory?.NativeDependencies;

    public Task<PlayerRendererInfo> ResolveRendererAsync()
    {
        List<(MediaDiagnosticLevel, string)> lines = [];
        var requested = options.Renderer?.Trim().ToLowerInvariant() ?? (options.SoftwareRenderer ? "software" : "auto");
        _software = requested is "software" or "cpu";
        (_deviceSelection, VideoDecodingMethod decoding, var summary) = requested switch
        {
            "gpu" or "d3d11" or "direct3d11" or "hardware" => (Direct3D11DeviceSelection.Hardware, VideoDecodingMethod.Hardware, "Direct3D11 GPU (forced)"),
            "warp" => (Direct3D11DeviceSelection.Warp, VideoDecodingMethod.Software, "Direct3D11 WARP (forced)"),
            "software" or "cpu" => (Direct3D11DeviceSelection.Auto, VideoDecodingMethod.Software, "Software (forced)"),
            _ => (Direct3D11DeviceSelection.Auto, VideoDecodingMethod.Auto, "Direct3D11 (GPU, else WARP)"),
        };

        if (requested is not ("auto" or "gpu" or "d3d11" or "direct3d11" or "hardware" or "warp" or "software" or "cpu"))
            lines.Add((MediaDiagnosticLevel.Warning, $"Unknown --renderer value '{options.Renderer}' (use auto, gpu, warp or software); using auto."));

        lines.Add((MediaDiagnosticLevel.Info, _software
            ? "Software renderer forced (--renderer=software or --software): the surface shows it through its frame-sink path, with software decoding."
            : $"{summary}: Direct3D11 device selection {_deviceSelection}, {decoding} video decoding."));
        lines.Add((MediaDiagnosticLevel.Info, $"Native search paths: {(NativeLibraryPaths.Count == 0 ? "(none exist)" : string.Join("; ", NativeLibraryPaths))}"));
        return Task.FromResult(new PlayerRendererInfo(summary, decoding, RequiresFrameSinkSurface: _software, lines));
    }

    /// <summary>
    /// The platform for the renderer chosen at startup (also used by the lightweight-surfaces
    /// test window); a new one per call, as nothing is shared between players.
    /// </summary>
    public IMediaControlPlatformFactory CreatePlatformFactory() => _software
        ? new WindowsMediaControlPlatformFactory(new SoftwarePresentationCoordinatorFactory())
        : new WindowsDirect3D11MediaControlPlatformFactory(new Direct3D11PresentationOptions { DeviceSelection = _deviceSelection, HdrOutput = options.HdrOutput });

    public Task<MediaControlPlayer> CreatePlayerAsync(PlayerRendererInfo renderer, MediaPlayerConfiguration configuration)
    {
        IMediaControlPlatformFactory platform = CreatePlatformFactory();
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
            try
            {
                _ = Directory.CreateDirectory(nativeFolder);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Read-only install location: the folder is still searched if it appears later.
            }

            defaults = [nativeFolder];
        }

        return PlayerNativePaths.Combine(options.NativeDirectories, defaults);
    }
}
