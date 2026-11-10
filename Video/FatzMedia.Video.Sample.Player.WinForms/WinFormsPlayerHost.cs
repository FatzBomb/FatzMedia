using FatzMedia.Video.Core.Configuration;
using FatzMedia.Video.Sample.Player.Configuration;
using FatzMedia.Video.Sample.Player.Shared;
using FatzMedia.Video.Sample.Player.Shared.Hosting;

namespace FatzMedia.Video.Sample.Player.WinForms;

/// <summary>
/// The WinForms UI thread: the <see cref="WindowsFormsSynchronizationContext"/> of the thread that
/// creates the dispatcher.
/// </summary>
sealed class WinFormsPlayerDispatcher : IPlayerDispatcher
{
    readonly SynchronizationContext _context;
    readonly Thread _uiThread = Thread.CurrentThread;

    /// <summary>Call on the UI thread (normally from the main form's constructor).</summary>
    public WinFormsPlayerDispatcher() =>
        _context = SynchronizationContext.Current as WindowsFormsSynchronizationContext ?? new WindowsFormsSynchronizationContext();

    public bool CheckAccess() => Thread.CurrentThread == _uiThread;

    public void Post(Action action) => _context.Post(static state => ((Action)state!)(), action);

    public IPlayerTimer CreateTimer(TimeSpan interval, Action tick) => new Timer(interval, tick);

    sealed class Timer : IPlayerTimer
    {
        readonly System.Windows.Forms.Timer _timer;
        readonly Action _tick;

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

        void OnTick(object? sender, EventArgs e) => _tick();
    }
}

/// <summary>The WinForms clipboard and open-file dialog, owned by the main form.</summary>
sealed class WinFormsPlayerShell(IWin32Window owner) : IPlayerClipboard, IPlayerFilePicker
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
/// One WinForms player's engine: the renderer for new players (<see cref="Program.RendererSelection"/>,
/// the Windows renderer choice shared with the WPF player: <c>--renderer=auto</c> is Direct3D11 on
/// the GPU, then WARP, then software), a presentation factory and a Windows platform package per
/// player, and the adapter's <see cref="FatzMedia.Video.WinForms.Player"/>. A frame-sink renderer
/// (software, Vulkan) is shown through the surface's frame-sink path (Stage 046A).
/// </summary>
sealed class WinFormsPlayerEngineFactory(PlayerStartupOptions options) : IPlayerEngineFactory
{
    IPlatformFactory? _platformFactory;
    PlayerRendererSelection? _selection;

    public IReadOnlyList<string> NativeLibraryPaths { get; } = ResolveNativePaths(options);

    public NativeDependencyReport? NativeDependenciesAfterFailure => _platformFactory?.NativeDependencies;

    public async Task<PlayerRendererInfo> ResolveRendererAsync()
    {
        var selection = await Program.RendererSelection.ConfigureAwait(false);
        _selection = selection;
        // --decoding=software forces software decoding for CPU comparisons (Stage 046A slice 3).
        return selection.ToRendererInfo(
            options,
            [(MediaDiagnosticLevel.Info, $"Native search paths: {(NativeLibraryPaths.Count == 0 ? "(none exist)" : string.Join("; ", NativeLibraryPaths))}")]);
    }

    /// <summary>
    /// The platform for the renderer chosen at startup (also used by the lightweight-surfaces
    /// test window); a new one per call, as nothing is shared between players.
    /// </summary>
    public static IPlatformFactory CreatePlatformFactory(PlayerRendererSelection selection) =>
        new WindowsPlatformFactory(selection.CreateFactory());

    public async Task<PlayerCore> CreatePlayerAsync(PlayerRendererInfo renderer, MediaPlayerConfiguration configuration)
    {
        var selection = _selection
            ?? throw new InvalidOperationException("ResolveRendererAsync must complete before CreatePlayerAsync.");
        var platform = CreatePlatformFactory(selection);
        _platformFactory = platform;
        configuration = options.ApplyAudioOptions(configuration);
        // Loading FFmpeg and FatzAudio blocks: CreateAsync runs it off the UI thread.
        return await global::FatzMedia.Video.WinForms.Player.CreateAsync(configuration, NativeLibraryPaths, platform).ConfigureAwait(false);
    }

    internal static IReadOnlyList<string> ResolveNativePaths(PlayerStartupOptions options) =>
        PlayerNativePaths.Combine(options.NativeDirectories, PlayerNativeLibraries.ResolveSearchPaths());
}
