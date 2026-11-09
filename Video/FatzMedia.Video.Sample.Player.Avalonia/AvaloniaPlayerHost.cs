using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using FatzMedia.Video.Audio;
using FatzMedia.Video.Core.Configuration;
using FatzMedia.Video.Sample.Player.Shared;
using FatzMedia.Video.Sample.Player.Shared.Hosting;

namespace FatzMedia.Video.Sample.Player.Avalonia;

/// <summary>Avalonia's UI thread for the shared view model.</summary>
internal sealed class AvaloniaPlayerDispatcher : IPlayerDispatcher
{
    public static AvaloniaPlayerDispatcher Instance { get; } = new();

    public bool CheckAccess() => Dispatcher.UIThread.CheckAccess();

    public void Post(Action action) => Dispatcher.UIThread.Post(action);

    public IPlayerTimer CreateTimer(TimeSpan interval, Action tick) => new Timer(interval, tick);

    private sealed class Timer : IPlayerTimer
    {
        private readonly DispatcherTimer _timer;
        private readonly Action _tick;

        public Timer(TimeSpan interval, Action tick)
        {
            _tick = tick;
            _timer = new DispatcherTimer { Interval = interval };
            _timer.Tick += OnTick;
        }

        public void Start() => _timer.Start();

        public void Stop() => _timer.Stop();

        public void Dispose()
        {
            _timer.Stop();
            _timer.Tick -= OnTick;
        }

        private void OnTick(object? sender, EventArgs e) => _tick();
    }
}

/// <summary>The clipboard and the file picker of the window that holds <paramref name="owner"/>.</summary>
internal sealed class AvaloniaPlayerShell(Control owner) : IPlayerClipboard, IPlayerFilePicker
{
    public async Task SetTextAsync(string text)
    {
        if (TopLevel.GetTopLevel(owner)?.Clipboard is not { } clipboard)
            throw new InvalidOperationException("No clipboard is available.");

        await clipboard.SetTextAsync(text);
    }

    public async Task<string?> PickFileAsync(string title, string filter)
    {
        if (TopLevel.GetTopLevel(owner) is not { } topLevel)
            return null;

        IReadOnlyList<IStorageFile> files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = ToFileTypes(filter)
        });

        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    /// <summary>Converts the engine's "Name|*.a;*.b|Name2|*.c" dialog filter into picker file types.</summary>
    private static List<FilePickerFileType> ToFileTypes(string filter)
    {
        List<FilePickerFileType> types = [];
        var parts = filter.Split('|');
        for (var i = 0; i + 1 < parts.Length; i += 2)
        {
            types.Add(new FilePickerFileType(parts[i])
            {
                Patterns = [.. parts[i + 1].Split(';', StringSplitOptions.RemoveEmptyEntries).Select(static pattern => pattern == "*.*" ? "*" : pattern)]
            });
        }

        return types;
    }
}

/// <summary>
/// One player's engine: the renderer picked in the toolbar, the platform package of this build
/// (<see cref="PlatformComposition"/>), and the decoding that renderer can present
/// (<see cref="RendererOption.Decoding"/>, Stage 045A: hardware for Direct3D11 on a GPU, software
/// otherwise; <c>--decoding=software</c> forces software for CPU comparisons). The engine is the
/// adapter's <see cref="FatzMedia.Video.Avalonia.Player"/> (Stage 046A slice 3).
/// </summary>
/// <param name="startupWarnings">Lines logged as warnings before the engine starts (the first build's command-line problems).</param>
internal sealed class AvaloniaPlayerEngineFactory(RendererOption renderer, IReadOnlyList<string> nativePaths, string label, IReadOnlyList<string> startupWarnings) : IPlayerEngineFactory
{
    private IMediaControlPlatformFactory? _platformFactory;

    public IReadOnlyList<string> NativeLibraryPaths { get; } = nativePaths;

    public NativeDependencyReport? NativeDependenciesAfterFailure => _platformFactory?.NativeDependencies;

    public Task<PlayerRendererInfo> ResolveRendererAsync()
    {
        (VideoDecodingMethod decoding, var forced) = StartupOptions.Current.ApplyDecoding(renderer.Decoding);
        List<(MediaDiagnosticLevel, string)> lines = [.. startupWarnings.Select(static warning => (MediaDiagnosticLevel.Warning, warning))];
        lines.Add((MediaDiagnosticLevel.Info, $"{label}: {PlatformComposition.Name} platform, renderer {renderer.Kind}: {renderer.Detail}, {forced ?? renderer.DecodingDetail}"));
        lines.Add((MediaDiagnosticLevel.Info, $"Native search paths: {(NativeLibraryPaths.Count == 0 ? "(none exist)" : string.Join("; ", NativeLibraryPaths))}"));

        return Task.FromResult(new PlayerRendererInfo(
            $"{renderer.Kind} ({PlatformComposition.Name})",
            decoding,
            RequiresFrameSinkSurface: renderer.Kind == RendererKind.Software,
            lines));
    }

    public async Task<MediaControlPlayer> CreatePlayerAsync(PlayerRendererInfo info, MediaPlayerConfiguration configuration)
    {
        IMediaControlPlatformFactory platform = PlatformComposition.Create(Renderers.Create(renderer.Kind));
        _platformFactory = platform;
        // --audio=fatz (037A slice 2): FatzAudio plays audio files; BASS stays the default.
        if (StartupOptions.Current.AudioEngine == PlayerAudioEngine.FatzAudio)
            configuration = configuration with { AudioEngineFactory = new FatzAudioEngineFactory() };
        // Native loading blocks: CreateAsync runs it off the UI thread.
        return await global::FatzMedia.Video.Avalonia.Player.CreateAsync(configuration, NativeLibraryPaths, platform).ConfigureAwait(false);
    }
}
