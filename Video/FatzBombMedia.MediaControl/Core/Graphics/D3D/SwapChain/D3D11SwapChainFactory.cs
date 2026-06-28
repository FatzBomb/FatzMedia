using System.Diagnostics;
using FatzBombMedia.MediaControl.Core.Configuration;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace FatzBombMedia.MediaControl.Core.Graphics.D3D.SwapChain;

/// <summary>
/// Centralizes DXGI swap-chain descriptions and fallback policy for native presentation targets.
/// </summary>
internal static class D3D11SwapChainFactory
{
    public const int DirectCompositionBufferCount = 3;

    public static SwapChainDescription1 CreateCompositionSwapChainDescription(int width, int height, AlphaMode alphaMode) => new()
    {
        Width = (uint)width,
        Height = (uint)height,
        Format = Format.B8G8R8A8_UNorm,
        Stereo = false,
        SampleDescription = new SampleDescription(1, 0),
        BufferUsage = Usage.RenderTargetOutput,
        BufferCount = DirectCompositionBufferCount,
        Scaling = Scaling.Stretch,
        SwapEffect = SwapEffect.FlipSequential,
        AlphaMode = alphaMode,
        Flags = SwapChainFlags.None
    };

    public static IDXGISwapChain? CreateBestNativeSwapChain(
        IDXGIFactory factory,
        ID3D11Device device,
        IntPtr hwnd,
        int width,
        int height,
        MediaPlayerConfiguration configuration)
    {
        // Prefer a double-buffered flip model. That is closer to modern video renderers and usually
        // gives DXGI/DWM a cleaner cadence than the legacy one-buffer discard chain. Some wrapper or
        // driver combinations reject flip model through this older CreateSwapChain path, so fall back
        // to the legacy chain instead of disabling native mode entirely.
        SwapChainDescription flipDescription = CreateNativeSwapChainDescription(hwnd, width, height, bufferCount: 2, SwapEffect.FlipSequential);
        try
        {
            IDXGISwapChain swapChain = factory.CreateSwapChain(device, flipDescription);
            if (configuration.EnableLogging) Debug.WriteLine("[VorticeD3D11] Native HWND swap-chain mode: FlipSequential, buffers=2");
            return swapChain;
        }
        catch (Exception ex)
        {
            if (configuration.EnableLogging) Debug.WriteLine($"[VorticeD3D11] FlipSequential swap-chain unavailable; falling back to legacy Discard. Reason: {ex.Message}");
        }

        SwapChainDescription legacyDescription = CreateNativeSwapChainDescription(hwnd, width, height, bufferCount: 1, SwapEffect.Discard);
        IDXGISwapChain legacySwapChain = factory.CreateSwapChain(device, legacyDescription);
        if (configuration.EnableLogging) Debug.WriteLine("[VorticeD3D11] Native HWND swap-chain mode: Discard, buffers=1");
        return legacySwapChain;
    }

    private static SwapChainDescription CreateNativeSwapChainDescription(IntPtr hwnd, int width, int height, int bufferCount, SwapEffect swapEffect) => new()
    {
        BufferCount = (uint)bufferCount,
        BufferDescription = new ModeDescription(
                (uint)width,
                (uint)height,
                new Rational(60, 1),
                Format.B8G8R8A8_UNorm),
        BufferUsage = Usage.RenderTargetOutput,
        OutputWindow = hwnd,
        SampleDescription = new SampleDescription(1, 0),
        Windowed = true,
        SwapEffect = swapEffect
    };
}
