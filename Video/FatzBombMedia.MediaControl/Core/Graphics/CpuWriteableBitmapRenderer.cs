using System.Buffers;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace FatzBombMedia.MediaControl.Core.Graphics;

/// <summary>
/// Owns the WriteableBitmap fallback presentation path for CPU BGRA frames.
/// </summary>
internal sealed class CpuWriteableBitmapRenderer
{
    public WriteableBitmap? CreateBitmap(int width, int height)
    {
        try
        {
            return new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[OptimizedRenderer] Failed to initialize CPU resources: {ex.Message}");
            return null;
        }
    }

    public void RenderBgraAsync(
        Dispatcher dispatcher,
        WriteableBitmap bitmap,
        int generation,
        ReadOnlySpan<byte> pixelData,
        int width,
        int height,
        int stride,
        Func<WriteableBitmap, int, int, int, bool> isCurrentSurface,
        Action framePresented)
    {
        var pixelArray = ArrayPool<byte>.Shared.Rent(pixelData.Length);
        pixelData.CopyTo(pixelArray);

        if (dispatcher.CheckAccess())
        {
            try
            {
                if (WriteFrame(bitmap, generation, pixelArray, width, height, stride, isCurrentSurface))
                {
                    framePresented();
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(pixelArray);
            }

            return;
        }

        try
        {
            dispatcher.BeginInvoke(() =>
            {
                try
                {
                    if (WriteFrame(bitmap, generation, pixelArray, width, height, stride, isCurrentSurface))
                    {
                        framePresented();
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(pixelArray);
                }
            }, DispatcherPriority.Render);
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(pixelArray);
            throw;
        }
    }

    public void Release(ref WriteableBitmap? bitmap)
    {
        if (bitmap == null)
        {
            return;
        }

        try
        {
            bitmap.Freeze();
            bitmap = null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[OptimizedRenderer] Cleanup error: {ex.Message}");
        }
    }

    private static bool WriteFrame(
        WriteableBitmap bitmap,
        int generation,
        byte[] pixelArray,
        int width,
        int height,
        int stride,
        Func<WriteableBitmap, int, int, int, bool> isCurrentSurface)
    {
        try
        {
            if (!isCurrentSurface(bitmap, generation, width, height))
            {
                return false;
            }

            bitmap.Lock();
            try
            {
                if (!isCurrentSurface(bitmap, generation, width, height))
                {
                    return false;
                }

                bitmap.WritePixels(new Int32Rect(0, 0, width, height), pixelArray, stride, 0);
                return true;
            }
            finally
            {
                bitmap.Unlock();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[OptimizedRenderer] CPU render failed: {ex.Message}");
            return false;
        }
    }

}
