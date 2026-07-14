namespace FatzBomb.MediaControl.Core.Graphics.Composition;

/// <summary>
/// Describes the producer/source backing a retained composition layer descriptor.
/// Stage 10A only describes intent; later stages move each source into retained
/// GPU layer targets keyed by <see cref="MediaCompositionLayerId" />.
/// </summary>
public enum MediaCompositionLayerSourceKind
{
    None,
    Clear,
    BgraPixels,
    D3D11Texture,
    RetainedD3D11Texture,
    CdgSnapshot,
    SolidColor
}
