namespace FatzMedia.Video.Core.Graphics.D3D.Presentation;

/// <summary>
/// Identifies the active low-level presentation backend.  The renderer should use this
/// as routing metadata only; backend-specific resource ownership belongs in backend classes.
/// </summary>
enum D3D11PresentationBackendKind
{
    None = 0,
    NativeHwndSwapChain = 2,
    DirectCompositionSingleTarget = 3,
    ManagedFrameSink = 4,
    SharedTexture = 5
}
