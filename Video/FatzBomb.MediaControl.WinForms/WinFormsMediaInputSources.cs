using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace FatzBomb.MediaControl.WinForms;

/// <summary>
/// WinForms-specific source conversions (Stage 046A): the same shape as
/// <c>AvaloniaMediaInputSources.FromBitmap</c> and the WPF adapter's
/// <c>WpfMediaInputSources.FromImageSource</c>, so every adapter turns its framework's image into
/// a Core source the same way.
/// </summary>
public static class WinFormsMediaInputSources
{
    /// <summary>
    /// Converts a GDI+ bitmap into a Core BGRA image source (straight alpha, matching the WPF and
    /// Avalonia conversions). The pixels are copied, so the bitmap can be disposed afterwards. Like
    /// every GDI+ object, the bitmap must not be used on another thread meanwhile.
    /// </summary>
    public static MediaInputSource FromBitmap(Bitmap bitmap, string? displayName = null)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        var width = bitmap.Width;
        var height = bitmap.Height;
        var stride = checked(width * 4);
        var pixels = new byte[checked(stride * height)];

        // Format32bppArgb is BGRA in memory with straight alpha; GDI+ converts other formats.
        BitmapData data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (var y = 0; y < height; y++)
                Marshal.Copy(data.Scan0 + (y * data.Stride), pixels, y * stride, stride);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        return MediaInputSource.FromOwnedBgra32Image(pixels, width, height, stride, displayName ?? "WinForms bitmap");
    }
}
