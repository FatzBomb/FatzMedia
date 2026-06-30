using FatzBombMedia.MediaControl.Core.Configuration;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FatzBombMedia.MediaControl.Wpf;

/// <summary>
/// WPF adapter facade over the Core-owned media-control player.
/// Core owns playback, commands, events, composition, and presentation; this type adds only WPF input conversions.
/// </summary>
public sealed class Player(MediaPlayerConfiguration configuration, IEnumerable<string> nativeLibraryPaths) : MediaControlPlayer(configuration, nativeLibraryPaths)
{
    public static Task<Player> CreateAsync(MediaPlayerConfiguration configuration, IEnumerable<string> nativeLibraryPaths) =>
        Task.Run(() => new Player(configuration, nativeLibraryPaths));

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

    private static MediaInputSource ToMediaInputSource(ImageSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (source is not BitmapSource bitmapSource)
            throw new NotSupportedException($"ImageSource type {source.GetType().Name} cannot be converted to a Core media input source. Use a BitmapSource-compatible image or pass MediaInputSource directly.");

        BitmapSource bgraSource = bitmapSource.Format == PixelFormats.Bgra32
            ? bitmapSource
            : new FormatConvertedBitmap(bitmapSource, PixelFormats.Bgra32, null, 0);

        if (bgraSource.CanFreeze && !bgraSource.IsFrozen)
            bgraSource.Freeze();

        var width = bgraSource.PixelWidth;
        var height = bgraSource.PixelHeight;
        var stride = checked(width * 4);
        var pixels = new byte[checked(stride * height)];
        bgraSource.CopyPixels(pixels, stride, 0);
        return MediaInputSource.FromOwnedBgra32Image(pixels, width, height, stride, "WPF ImageSource");
    }
}
