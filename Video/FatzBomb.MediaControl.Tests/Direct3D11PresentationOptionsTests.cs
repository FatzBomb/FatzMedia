using FatzBomb.MediaControl.Core.Graphics.D3D.SwapChain;
using FatzBomb.MediaControl.Rendering.Direct3D11;

namespace FatzBomb.MediaControl.Tests;

public sealed class Direct3D11PresentationOptionsTests
{
    [Fact]
    public void DefaultOptions_UseVSync()
    {
        var options = new Direct3D11PresentationOptions();

        Assert.Equal(NativeSwapChainPresentMode.VSync, options.NativeSwapChainPresentMode);
    }

    [Theory]
    [InlineData(NativeSwapChainPresentMode.VSync, 0, false, 1u)]
    [InlineData(NativeSwapChainPresentMode.Immediate, 0, false, 0u)]
    [InlineData(NativeSwapChainPresentMode.VSync, 1, false, 0u)]
    [InlineData(NativeSwapChainPresentMode.VSync, 0, true, 0u)]
    public void GetSyncInterval_PreservesPresentPolicy(
        NativeSwapChainPresentMode presentMode,
        int liveResizeFlag,
        bool syncImmediate,
        uint expected)
    {
        var actual = D3D11SwapChainPresenter.GetSyncInterval(
            presentMode,
            liveResizeFlag,
            syncImmediate);

        Assert.Equal(expected, actual);
    }
}
