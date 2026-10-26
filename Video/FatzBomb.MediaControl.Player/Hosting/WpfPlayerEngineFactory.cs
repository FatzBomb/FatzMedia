using FatzBomb.MediaControl.Core.Configuration;
using FatzBomb.MediaControl.Player.Configuration;
using FatzBomb.MediaControl.Player.Shared.Hosting;

using FatzMediaPlayer = FatzBomb.MediaControl.Wpf.Player;

namespace FatzBomb.MediaControl.Player.Hosting;

/// <summary>
/// Creates the WPF <see cref="FatzMediaPlayer"/> with the renderer chosen at startup
/// (<see cref="App.RendererSelection"/>, Stage 036A) and the Windows platform package.
/// </summary>
internal sealed class WpfPlayerEngineFactory : IPlayerEngineFactory
{
    private PlayerRendererSelection? _selection;
    private IMediaControlPlatformFactory? _platformFactory;

    public IReadOnlyList<string> NativeLibraryPaths { get; } = PlayerNativeLibraries.ResolveSearchPaths();

    public NativeDependencyReport? NativeDependenciesAfterFailure => _platformFactory?.NativeDependencies;

    public async Task<PlayerRendererInfo> ResolveRendererAsync()
    {
        PlayerRendererSelection selection = await App.RendererSelection.ConfigureAwait(false);
        _selection = selection;
        return new PlayerRendererInfo(
            selection.Summary,
            selection.UsesSoftwareDecoding ? VideoDecodingMethod.Software : VideoDecodingMethod.Hardware,
            selection.UsesSoftwareRenderer,
            selection.LogLines);
    }

    public async Task<MediaControlPlayer> CreatePlayerAsync(PlayerRendererInfo renderer, MediaPlayerConfiguration configuration)
    {
        PlayerRendererSelection selection = _selection
            ?? throw new InvalidOperationException("ResolveRendererAsync must complete before CreatePlayerAsync.");
        _platformFactory = new WindowsMediaControlPlatformFactory(selection.Factory);
        return await FatzMediaPlayer.CreateAsync(configuration, NativeLibraryPaths, _platformFactory).ConfigureAwait(false);
    }
}
