namespace FatzBombMedia.MediaControl.Core.Graphics.Composition.D3D11;

/// <summary>
/// Describes the role of a compositor output target. Stage 10L records target
/// intent only; it does not introduce independent media presentation paths.
/// </summary>
internal enum D3D11CompositionTargetKind
{
    None = 0,
    FinalPresentation = 1,
    RetainedOffscreenLayer = 2,
    FutureSwapChain = 3
}
