namespace FatzBomb.MediaControl.Core.Graphics.Composition.D3D11;

/// <summary>
/// Describes where a retained layer is expected to render. Stage 10J records this
/// as planning metadata only; the compositor still draws every layer directly into
/// the active final target until the later multi-target stages.
/// </summary>
internal enum D3D11CompositionLayerRenderTargetKind
{
    /// <summary>No render target is needed for this layer.</summary>
    None,

    /// <summary>The current Stage 9/10 behavior: draw directly into the active final target.</summary>
    ImmediateFinalTarget,

    /// <summary>A future retained offscreen texture can hold this layer's rendered output.</summary>
    RetainedOffscreenTexture
}
