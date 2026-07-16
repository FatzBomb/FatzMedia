namespace FatzBomb.MediaControl.Core.Graphics.Composition.D3D11;

/// <summary>
/// Describes what the compositor would need to do for a retained layer during the
/// current capture. Stage 10H/10I uses this as planning metadata only; draw order
/// and final presentation behavior remain unchanged.
/// </summary>
[Flags]
internal enum D3D11CompositionLayerUpdateAction
{
    None = 0,
    Upload = 1 << 0,
    Render = 1 << 1,
    AllocateResources = 1 << 2,
    ReleaseResources = 1 << 3
}
