namespace FatzBomb.MediaControl.Core.Graphics.Composition.D3D11;

/// <summary>
/// Target-level planning action. Stage 10N consumes Present actions to route the
/// current compositor pass to all active presentation targets.
/// </summary>
[Flags]
internal enum D3D11CompositionTargetUpdateAction
{
    None = 0,
    Activate = 1 << 0,
    Resize = 1 << 1,
    BackendChange = 1 << 2,
    Present = 1 << 3,
    Deactivate = 1 << 4
}
