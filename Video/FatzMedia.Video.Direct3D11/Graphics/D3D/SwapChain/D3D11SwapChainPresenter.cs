using System.Diagnostics;
using FatzMedia.Video.Core.Graphics.D3D.Diagnostics;
using FatzMedia.Video.Core.Graphics.Rendering;
using FatzMedia.Video.Direct3D11;
using Vortice.DXGI;

namespace FatzMedia.Video.Core.Graphics.D3D.SwapChain;

/// <summary>
/// Owns DXGI Present policy for native swap-chain targets.  The renderer supplies
/// the active mode and resize state; this class owns sync-interval selection,
/// diagnostics, and error logging.
/// </summary>
static class D3D11SwapChainPresenter
{
    public static bool Present(
        IDXGISwapChain? swapChain,
        NativeSwapChainPresentMode presentMode,
        int liveResizeFlag,
        bool syncImmediate,
        D3D11PresentationDiagnostics diagnostics,
        ref bool deviceLostReported,
        RendererDiagnosticLatch presentFailure)
    {
        if (swapChain == null)
        {
            return false;
        }

        try
        {
            // During WPF live resize, a vsync-blocking Present(1) can make the top-level
            // resize loop feel like it is dragging through mud.  Keep video frames live,
            // but present them in immediate mode while the user is actively resizing.
            var syncInterval = GetSyncInterval(presentMode, liveResizeFlag, syncImmediate);

            var presentStart = Stopwatch.GetTimestamp();
            var result = swapChain.Present(syncInterval, (int)PresentFlags.None);
            diagnostics.RecordNativePresent(D3D11PresentationDiagnostics.ElapsedMilliseconds(presentStart), (int)syncInterval);
            if (result.Failure)
            {
                if (D3D11DeviceLostClassifier.IsDeviceLost(result))
                {
                    // Latch owned by the caller: report once per loss episode so a failing
                    // present loop does not spam an identical diagnostic every frame.
                    if (!deviceLostReported)
                    {
                        deviceLostReported = true;
                        DiagnosticRouter.Raise(MediaDiagnosticLevel.Error, "D3D11.DeviceLost", $"Swap-chain Present detected GPU device lost: {D3D11DeviceLostClassifier.Describe(result, swapChain)}");
                    }
                }
                else
                {
                    // Repeats every frame while it lasts: one report per episode (ENG-039 G5).
                    _ = presentFailure.Raise(MediaDiagnosticLevel.Warning, "D3D11", $"Swap-chain Present failed: {result}");
                }

                return false;
            }

            deviceLostReported = false;
            presentFailure.Reset();
            return true;
        }
        catch (Exception ex)
        {
            _ = presentFailure.Raise(MediaDiagnosticLevel.Warning, "D3D11", $"Swap-chain Present failed: {ex.Message}", ex);
            return false;
        }
    }

    internal static uint GetSyncInterval(
        NativeSwapChainPresentMode presentMode,
        int liveResizeFlag,
        bool syncImmediate) =>
        syncImmediate
        || Volatile.Read(ref liveResizeFlag) != 0
        || presentMode == NativeSwapChainPresentMode.Immediate
            ? 0u
            : 1u;
}
