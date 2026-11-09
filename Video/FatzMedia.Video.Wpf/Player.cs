using System.Windows.Media;
using FatzMedia.Video.Controllers;
using FatzMedia.Video.Core.Configuration;

namespace FatzMedia.Video.Wpf;

/// <summary>
/// WPF adapter facade over the Core-owned media-control player.
/// Core owns playback, commands, events, composition, and presentation; this type adds only WPF input conversions.
/// </summary>
public sealed class Player : PlayerCore
{
    public Player(
        MediaPlayerConfiguration configuration,
        IEnumerable<string> nativeLibraryPaths,
        IMediaPresentationCoordinatorFactory presentationFactory)
        : this(
            configuration,
            nativeLibraryPaths,
            new WindowsPlatformFactory(presentationFactory))
    {
    }

    public Player(
        MediaPlayerConfiguration configuration,
        IEnumerable<string> nativeLibraryPaths,
        IPlatformFactory platformFactory)
        : base(platformFactory, configuration, nativeLibraryPaths)
    {
    }

    public static Task<Player> CreateAsync(
        MediaPlayerConfiguration configuration,
        IEnumerable<string> nativeLibraryPaths,
        IMediaPresentationCoordinatorFactory presentationFactory) =>
        Task.Run(() => new Player(configuration, nativeLibraryPaths, presentationFactory));

    public static Task<Player> CreateAsync(
        MediaPlayerConfiguration configuration,
        IEnumerable<string> nativeLibraryPaths,
        IPlatformFactory platformFactory) =>
        Task.Run(() => new Player(configuration, nativeLibraryPaths, platformFactory));

    public bool Load(ImageSource source, bool ignoreSidecar = false) =>
        Load(ToMediaInputSource(source), ignoreSidecar);

    public bool Load(string path, ImageSource? backgroundSource, bool ignoreSidecar = false) =>
        Load(MediaInputSource.FromFile(path),
            backgroundSource == null ? null : ToMediaInputSource(backgroundSource),
            ignoreSidecar);

    public bool Load(ImageSource source, ImageSource? backgroundSource, bool ignoreSidecar = false) =>
        Load(ToMediaInputSource(source),
            backgroundSource == null ? null : ToMediaInputSource(backgroundSource),
            ignoreSidecar);

    public Task<bool> LoadAsync(ImageSource source, bool ignoreSidecar = false) =>
        LoadAsync(ToMediaInputSource(source), ignoreSidecar);

    public Task<bool> LoadAsync(string path, ImageSource? backgroundSource, bool ignoreSidecar = false) =>
        LoadAsync(MediaInputSource.FromFile(path),
            backgroundSource == null ? null : ToMediaInputSource(backgroundSource),
            ignoreSidecar);

    public Task<bool> LoadAsync(ImageSource source, ImageSource? backgroundSource, bool ignoreSidecar = false) =>
        LoadAsync(ToMediaInputSource(source),
            backgroundSource == null ? null : ToMediaInputSource(backgroundSource),
            ignoreSidecar);

    static MediaInputSource ToMediaInputSource(ImageSource source) => WpfMediaInputSources.FromImageSource(source);
}
