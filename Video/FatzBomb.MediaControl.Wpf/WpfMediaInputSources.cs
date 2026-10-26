using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FatzBomb.MediaControl.Wpf;

/// <summary>
/// WPF-specific source conversions (Stage 046A: the same shape as
/// <c>AvaloniaMediaInputSources.FromBitmap</c> and <c>WinFormsMediaInputSources.FromBitmap</c>).
/// <see cref="Player"/>'s <see cref="ImageSource"/> overloads and <see cref="BackgroundSurface"/>
/// use it.
/// </summary>
public static class WpfMediaInputSources
{
    /// <summary>
    /// Converts a WPF image into a Core BGRA image source (straight alpha). Call on the image's
    /// thread unless it is frozen. Only <see cref="BitmapSource"/>-based images convert.
    /// </summary>
    /// <exception cref="NotSupportedException">The image is not a <see cref="BitmapSource"/> (for example a <c>DrawingImage</c>).</exception>
    public static MediaInputSource FromImageSource(ImageSource source, string? displayName = null)
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
        return MediaInputSource.FromOwnedBgra32Image(pixels, width, height, stride, displayName ?? "WPF ImageSource");
    }
}
