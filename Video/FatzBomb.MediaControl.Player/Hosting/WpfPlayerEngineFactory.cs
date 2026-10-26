using FatzBomb.MediaControl.Core.Configuration;
using FatzBomb.MediaControl.Player.Configuration;
using FatzBomb.MediaControl.Player.Shared.Hosting;

using FatzMediaPlayer = FatzBomb.MediaControl.Wpf.Player;

namespace FatzBomb.MediaControl.Player.Hosting;

/// <summary>
/// Creates the WPF <see cref="FatzMediaPlayer"/> with the renderer chosen at startup
/// (<see cref="App.RendererSelection"/>, Stage 036A; a presentation factory per player) and the
/// Windows platform package.
/// </summary>
internal sealed class WpfPlayerEngineFactory : IPlayerEngineFactory
{
    private PlayerRendererSelection? _selection;
    private IMediaControlPlatformFactory? _platformFactory;

    // --native= directories first, then the Windows defaults (or the app's native/ drop folder).
    public IReadOnlyList<string> NativeLibraryPaths { get; } =
        PlayerNativePaths.Combine(App.StartupOptions.NativeDirectories, PlayerNativeLibraries.ResolveSearchPaths());

    public NativeDependencyReport? NativeDependenciesAfterFailure => _platformFactory?.NativeDependencies;

    public async Task<PlayerRendererInfo> ResolveRendererAsync()
    {
        PlayerRendererSelection selection = await App.RendererSelection.ConfigureAwait(false);
        _selection = selection;
        // --decoding=software forces software decoding for CPU comparisons (Stage 046A slice 3).
        return selection.ToRendererInfo(App.StartupOptions);
    }

    public async Task<MediaControlPlayer> CreatePlayerAsync(PlayerRendererInfo renderer, MediaPlayerConfiguration configuration)
    {
        PlayerRendererSelection selection = _selection
            ?? throw new InvalidOperationException("ResolveRendererAsync must complete before CreatePlayerAsync.");
        _platformFactory = new WindowsMediaControlPlatformFactory(selection.CreateFactory());
        return await FatzMediaPlayer.CreateAsync(configuration, NativeLibraryPaths, _platformFactory).ConfigureAwait(false);
    }
}
