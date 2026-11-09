using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace FatzMedia.Video.Avalonia;

/// <summary>
/// Avalonia-specific source conversions: the only media-facing code the adapter owns.
/// </summary>
public static class AvaloniaMediaInputSources
{
    /// <summary>
    /// Converts an Avalonia bitmap into a Core BGRA image source (straight alpha, matching the
    /// WPF adapter's <c>ImageSource</c> conversion).
    /// </summary>
    public static unsafe MediaInputSource FromBitmap(Bitmap bitmap, string? displayName = null)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        var size = bitmap.PixelSize;
        var stride = size.Width * 4;
        var pixels = new byte[stride * size.Height];
        using (var converted = new WriteableBitmap(size, bitmap.Dpi, PixelFormat.Bgra8888, AlphaFormat.Unpremul))
        {
            using var framebuffer = converted.Lock();
            bitmap.CopyPixels(framebuffer);
            var source = new ReadOnlySpan<byte>((void*)framebuffer.Address, framebuffer.RowBytes * size.Height);
            for (var y = 0; y < size.Height; y++)
                source.Slice(y * framebuffer.RowBytes, stride).CopyTo(pixels.AsSpan(y * stride, stride));
        }

        return MediaInputSource.FromOwnedBgra32Image(pixels, size.Width, size.Height, stride, displayName ?? "Avalonia bitmap");
    }
}
