using System.Diagnostics;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using WpfD3DImage = System.Windows.Interop.D3DImage;

namespace FatzBombMedia.MediaControl.Core.Graphics.D3D.Presentation.D3DImage;

/// <summary>
/// Owns D3DImage dirty-rect notification and surface-generation checks.  This keeps
/// WPF dispatcher details out of the D3D11 renderer coordinator.
/// </summary>
internal sealed class D3DImagePresentationNotifier
{
    private int _surfaceGeneration;

    public void RetireSurfaces()
    {
        Interlocked.Increment(ref _surfaceGeneration);
    }

    public void NotifyDirty(
        WpfD3DImage? d3dImage,
        int width,
        int height,
        Func<bool> isDisposed,
        Func<WpfD3DImage, bool> isCurrentImage,
        Action framePresented)
    {
        if (d3dImage == null)
        {
            return;
        }

        var generation = Volatile.Read(ref _surfaceGeneration);

        void NotifyIfCurrent()
        {
            if (isDisposed()
                || generation != Volatile.Read(ref _surfaceGeneration)
                || !isCurrentImage(d3dImage))
            {
                return;
            }

            MarkSurfaceDirty(d3dImage, width, height);
            framePresented();
        }

        if (d3dImage.Dispatcher.CheckAccess())
        {
            NotifyIfCurrent();
            return;
        }

        d3dImage.Dispatcher.BeginInvoke(NotifyIfCurrent, System.Windows.Threading.DispatcherPriority.Render);
    }

    private static void MarkSurfaceDirty(WpfD3DImage d3dImage, int width, int height)
    {
        if (!d3dImage.IsFrontBufferAvailable)
        {
            return;
        }

        try
        {
            d3dImage.Lock();
            try
            {
                d3dImage.AddDirtyRect(new Int32Rect(0, 0, width, height));
            }
            finally
            {
                d3dImage.Unlock();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VorticeD3D11] D3DImage update failed: {ex.Message}");
        }
    }
}
