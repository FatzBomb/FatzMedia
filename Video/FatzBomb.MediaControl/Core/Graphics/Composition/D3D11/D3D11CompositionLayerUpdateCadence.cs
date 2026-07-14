namespace FatzBomb.MediaControl.Core.Graphics.Composition.D3D11;

/// <summary>
/// Future scheduling cadence for retained layers. Stage 10K exposes this metadata
/// without changing the compositor's current immediate upload/draw/present flow.
/// </summary>
internal enum D3D11CompositionLayerUpdateCadence
{
    /// <summary>No update cadence is needed because the layer is inactive or clear-only.</summary>
    None,

    /// <summary>Update only when the layer descriptor/revision changes.</summary>
    OnDemand,

    /// <summary>Update according to the source media cadence, such as video frame rate.</summary>
    SourceRate,

    /// <summary>Layer content is retained until explicitly invalidated.</summary>
    Static
}
