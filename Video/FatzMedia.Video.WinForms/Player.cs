using FatzMedia.Video.Core.Configuration;

namespace FatzMedia.Video.WinForms;

/// <summary>
/// WinForms adapter facade over the Core-owned media-control player, the same shape as the WPF
/// adapter's
/// <c>Player</c> (Stage 046A slice 3): <see cref="CreateAsync"/> creates it off the UI thread, and
/// the <see cref="Bitmap"/> overloads load images through <see
/// cref="WinFormsMediaInputSources.FromBitmap"/>. Core owns playback,
/// commands, events, composition and presentation.
/// </summary>
/// <remarks>
/// There is no default platform factory: the host chooses the platform package (and with it the
/// renderer), usually <c>WindowsMediaControlPlatformFactory</c> or
/// <c>WindowsDirect3D11MediaControlPlatformFactory</c>, which this adapter does not reference (it
/// depends on Core only). The WPF adapter has one only because it can present only on Windows.
/// </remarks>
public sealed class Player : MediaControlPlayer
{
    /// <summary>Creates the player on the calling thread; it loads native libraries and blocks, so UI code uses <see cref="CreateAsync"/>.</summary>
    public Player(
        MediaPlayerConfiguration configuration,
        IEnumerable<string> nativeLibraryPaths,
        IMediaControlPlatformFactory platformFactory)
        : base(platformFactory, configuration, nativeLibraryPaths)
    {
    }

    /// <summary>Creates the player on the thread pool (native loading blocks).</summary>
    public static Task<Player> CreateAsync(
        MediaPlayerConfiguration configuration,
        IEnumerable<string> nativeLibraryPaths,
        IMediaControlPlatformFactory platformFactory) =>
        Task.Run(() => new Player(configuration, nativeLibraryPaths, platformFactory));

    public bool Load(Bitmap source, bool ignoreSidecar = false) =>
        Load(WinFormsMediaInputSources.FromBitmap(source), ignoreSidecar);

    public bool Load(string path, Bitmap? backgroundSource, bool ignoreSidecar = false) =>
        Load(MediaInputSource.FromFile(path), backgroundSource == null ? null : WinFormsMediaInputSources.FromBitmap(backgroundSource), ignoreSidecar);

    public bool Load(Bitmap source, Bitmap? backgroundSource, bool ignoreSidecar = false) =>
        Load(WinFormsMediaInputSources.FromBitmap(source), backgroundSource == null ? null : WinFormsMediaInputSources.FromBitmap(backgroundSource), ignoreSidecar);

    public Task<bool> LoadAsync(Bitmap source, bool ignoreSidecar = false) =>
        LoadAsync(WinFormsMediaInputSources.FromBitmap(source), ignoreSidecar);

    public Task<bool> LoadAsync(string path, Bitmap? backgroundSource, bool ignoreSidecar = false) =>
        LoadAsync(MediaInputSource.FromFile(path), backgroundSource == null ? null : WinFormsMediaInputSources.FromBitmap(backgroundSource), ignoreSidecar);

    public Task<bool> LoadAsync(Bitmap source, Bitmap? backgroundSource, bool ignoreSidecar = false) =>
        LoadAsync(WinFormsMediaInputSources.FromBitmap(source), backgroundSource == null ? null : WinFormsMediaInputSources.FromBitmap(backgroundSource), ignoreSidecar);
}
