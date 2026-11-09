using FatzMedia.Video.Core.Configuration;
using Avalonia.Media.Imaging;

namespace FatzMedia.Video.Avalonia;

/// <summary>
/// Avalonia adapter facade over the Core-owned media-control player, the same shape as the WPF
/// adapter's
/// <c>Player</c> (Stage 046A slice 3): <see cref="CreateAsync"/> creates it off the UI thread, and
/// the <see cref="Bitmap"/> overloads load images through <see
/// cref="AvaloniaMediaInputSources.FromBitmap"/>. Core owns playback,
/// commands, events, composition and presentation.
/// </summary>
/// <remarks>
/// There is no default platform factory: the host chooses the platform package (and with it the
/// renderer): the Windows or the Linux platform package, which this net10.0 adapter does not
/// reference. The WPF adapter has one only because it can present only on Windows.
/// </remarks>
public sealed class Player : MediaControlPlayer
{
    /// <summary>Creates the player on the calling thread; it loads native libraries and blocks, so UI code uses <see cref="CreateAsync"/>.</summary>
    public Player(
        MediaPlayerConfiguration configuration,
        IEnumerable<string> nativeLibraryPaths,
        IPlatformFactory platformFactory)
        : base(platformFactory, configuration, nativeLibraryPaths)
    {
    }

    /// <summary>Creates the player on the thread pool (native loading blocks).</summary>
    public static Task<Player> CreateAsync(
        MediaPlayerConfiguration configuration,
        IEnumerable<string> nativeLibraryPaths,
        IPlatformFactory platformFactory) =>
        Task.Run(() => new Player(configuration, nativeLibraryPaths, platformFactory));

    public bool Load(Bitmap source, bool ignoreSidecar = false) =>
        Load(AvaloniaMediaInputSources.FromBitmap(source), ignoreSidecar);

    public bool Load(string path, Bitmap? backgroundSource, bool ignoreSidecar = false) =>
        Load(MediaInputSource.FromFile(path), backgroundSource == null ? null : AvaloniaMediaInputSources.FromBitmap(backgroundSource), ignoreSidecar);

    public bool Load(Bitmap source, Bitmap? backgroundSource, bool ignoreSidecar = false) =>
        Load(AvaloniaMediaInputSources.FromBitmap(source), backgroundSource == null ? null : AvaloniaMediaInputSources.FromBitmap(backgroundSource), ignoreSidecar);

    public Task<bool> LoadAsync(Bitmap source, bool ignoreSidecar = false) =>
        LoadAsync(AvaloniaMediaInputSources.FromBitmap(source), ignoreSidecar);

    public Task<bool> LoadAsync(string path, Bitmap? backgroundSource, bool ignoreSidecar = false) =>
        LoadAsync(MediaInputSource.FromFile(path), backgroundSource == null ? null : AvaloniaMediaInputSources.FromBitmap(backgroundSource), ignoreSidecar);

    public Task<bool> LoadAsync(Bitmap source, Bitmap? backgroundSource, bool ignoreSidecar = false) =>
        LoadAsync(AvaloniaMediaInputSources.FromBitmap(source), backgroundSource == null ? null : AvaloniaMediaInputSources.FromBitmap(backgroundSource), ignoreSidecar);
}
