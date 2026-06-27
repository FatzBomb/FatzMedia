namespace FatzBombMedia.MediaControl.Core.Graphics.D3D.Presentation;

/// <summary>
/// Identifies the active low-level presentation backend.  The renderer should use this
/// as routing metadata only; backend-specific resource ownership belongs in backend classes.
/// </summary>
internal enum D3D11PresentationBackendKind
{
    None = 0,
    D3DImage = 1,
    NativeHwndSwapChain = 2,
    DirectCompositionSingleTarget = 3
}
