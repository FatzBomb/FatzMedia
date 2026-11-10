using FatzMedia.Video.Audio;
using FatzMedia.Video.Core.Configuration;
using FatzMedia.Video.Sample.Player.Configuration;
using FatzMedia.Video.Sample.Player.Shared;
using FatzMedia.Video.Sample.Player.Shared.Hosting;

using FatzMediaPlayer = FatzMedia.Video.Wpf.Player;

namespace FatzMedia.Video.Sample.Player.Hosting;

/// <summary>
/// Creates the WPF <see cref="FatzMediaPlayer"/> with the renderer chosen at startup
/// (<see cref="App.RendererSelection"/>, Stage 036A; a presentation factory per player) and the
/// Windows platform package.
/// </summary>
sealed class WpfPlayerEngineFactory : IPlayerEngineFactory
{
    PlayerRendererSelection? _selection;
    IPlatformFactory? _platformFactory;

    // --native= directories first, then the Windows defaults (or the app's native/ drop folder).
    public IReadOnlyList<string> NativeLibraryPaths { get; } =
        PlayerNativePaths.Combine(App.StartupOptions.NativeDirectories, PlayerNativeLibraries.ResolveSearchPaths());

    public NativeDependencyReport? NativeDependenciesAfterFailure => _platformFactory?.NativeDependencies;

    public async Task<PlayerRendererInfo> ResolveRendererAsync()
    {
        var selection = await App.RendererSelection.ConfigureAwait(false);
        _selection = selection;
        // --decoding=software forces software decoding for CPU comparisons (Stage 046A slice 3).
        return selection.ToRendererInfo(App.StartupOptions);
    }

    public async Task<PlayerCore> CreatePlayerAsync(PlayerRendererInfo renderer, MediaPlayerConfiguration configuration)
    {
        var selection = _selection
            ?? throw new InvalidOperationException("ResolveRendererAsync must complete before CreatePlayerAsync.");
        _platformFactory = new WindowsPlatformFactory(selection.CreateFactory());
        // --audio=fatz (037A): FatzAudio plays everything, on --audio-device= when given, CD+G scaled with --cdg-scale;
        // BASS stays the default.
        if (App.StartupOptions.AudioEngine == PlayerAudioEngine.FatzAudio)
            configuration = configuration with
            {
                AudioEngineFactory = new FatzAudioEngineFactory(),
                AudioOutputDeviceId = App.StartupOptions.AudioDevice,
                CdgScaling = App.StartupOptions.CdgScaling,
                AudioEngineDecodesSoundtracks = App.StartupOptions.AudioEngineDecodesSoundtracks
            };
        return await FatzMediaPlayer.CreateAsync(configuration, NativeLibraryPaths, _platformFactory).ConfigureAwait(false);
    }
}
