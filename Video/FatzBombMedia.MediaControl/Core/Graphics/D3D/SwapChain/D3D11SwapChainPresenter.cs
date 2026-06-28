using System.Diagnostics;
using FatzBombMedia.MediaControl.Core.Configuration;
using Vortice.DXGI;
using FatzBombMedia.MediaControl.Core.Graphics.D3D.Diagnostics;
using SharpGen.Runtime;

namespace FatzBombMedia.MediaControl.Core.Graphics.D3D.SwapChain;

/// <summary>
/// Owns DXGI Present policy for native swap-chain targets.  The renderer supplies
/// the active mode and resize state; this class owns sync-interval selection,
/// diagnostics, and error logging.
/// </summary>
internal static class D3D11SwapChainPresenter
{
    public static void Present(
        IDXGISwapChain? swapChain,
        MediaPlayerConfiguration configuration,
        int liveResizeFlag,
        bool syncImmediate,
        D3D11PresentationDiagnostics diagnostics)
    {
        if (swapChain == null)
        {
            return;
        }

        try
        {
            // During WPF live resize, a vsync-blocking Present(1) can make the top-level
            // resize loop feel like it is dragging through mud.  Keep video frames live,
            // but present them in immediate mode while the user is actively resizing.
            var liveResize = Volatile.Read(ref liveResizeFlag) != 0;
            var syncInterval = syncImmediate
                || liveResize
                || configuration.NativeSwapChainPresentMode == NativeSwapChainPresentMode.Immediate
                    ? 0u
                    : 1u;

            var presentStart = Stopwatch.GetTimestamp();
            Result result = swapChain.Present(syncInterval, (int)PresentFlags.None);
            diagnostics.RecordNativePresent(D3D11PresentationDiagnostics.ElapsedMilliseconds(presentStart), (int)syncInterval);
            if (result.Failure)
            {
                Debug.WriteLine($"[VorticeD3D11] Native HWND swap-chain Present failed: {result}");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VorticeD3D11] Native HWND swap-chain Present failed: {ex.Message}");
        }
    }
}
