namespace FatzBomb.MediaControl.Rendering.Direct3D11;

/// <summary>Controls DXGI swap-chain presentation synchronization.</summary>
public enum NativeSwapChainPresentMode
{
    /// <summary>Present with sync interval one so DXGI/DWM paces frames to the monitor refresh.</summary>
    VSync,

    /// <summary>Present with sync interval zero. Intended primarily for presentation diagnostics.</summary>
    Immediate
}

/// <summary>Immutable creation-time options for the Direct3D11 presentation backend.</summary>
public sealed record Direct3D11PresentationOptions
{
    /// <summary>Gets the DXGI swap-chain presentation mode. VSync is the normal playback default.</summary>
    public NativeSwapChainPresentMode NativeSwapChainPresentMode { get; init; } = NativeSwapChainPresentMode.VSync;
}
