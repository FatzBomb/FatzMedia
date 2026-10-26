namespace FatzBomb.MediaControl.Rendering.Direct3D11;

/// <summary>Controls DXGI swap-chain presentation synchronization.</summary>
public enum NativeSwapChainPresentMode
{
    /// <summary>Present with sync interval one so DXGI/DWM paces frames to the monitor refresh.</summary>
    VSync,

    /// <summary>Present with sync interval zero. Intended primarily for presentation diagnostics.</summary>
    Immediate
}

/// <summary>Which Direct3D11 device the renderer creates (Stage 036A).</summary>
public enum Direct3D11DeviceSelection
{
    /// <summary>The GPU, then WARP (Windows' software Direct3D driver) when no GPU device can be created.</summary>
    Auto,

    /// <summary>The GPU only.</summary>
    Hardware,

    /// <summary>
    /// WARP only: for testing, or for a machine whose GPU driver is known to be broken. Pair it
    /// with software decoding; hardware-decoded frames bring their GPU device, which the renderer
    /// adopts for direct presentation.
    /// </summary>
    Warp
}

/// <summary>Immutable creation-time options for the Direct3D11 presentation backend.</summary>
public sealed record Direct3D11PresentationOptions
{
    /// <summary>Gets the DXGI swap-chain presentation mode. VSync is the normal playback default.</summary>
    public NativeSwapChainPresentMode NativeSwapChainPresentMode { get; init; } = NativeSwapChainPresentMode.VSync;

    /// <summary>Gets which device to create. <see cref="Direct3D11DeviceSelection.Auto"/> is the default.</summary>
    public Direct3D11DeviceSelection DeviceSelection { get; init; } = Direct3D11DeviceSelection.Auto;

    /// <summary>
    /// Gets whether HDR video is presented as HDR on a monitor in Windows HDR mode (Stage 042A
    /// slice 2), instead of being tone mapped to SDR. Default false: output is SDR, as before.
    /// When on, the primary window's swap chain switches to FP16 scRGB while hardware-decoded HDR10
    /// or HLG video plays on a monitor with HDR on, and back to SDR otherwise. Secondary windows,
    /// the WPF composited mode (managed frame sink) and shared-texture surfaces (Avalonia) stay SDR.
    /// Anything unsupported falls back to SDR tone mapping with one <c>D3D11.HdrOutput</c> diagnostic.
    /// </summary>
    public bool HdrOutput { get; init; }
}
