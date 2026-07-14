namespace FatzBomb.MediaControl.Core.Graphics.Composition.D3D11;

/// <summary>
/// One target-level planning entry for the current compositor pass. Stage 10N
/// consumes these entries to drive active multi-target presentation fan-out.
/// </summary>
internal readonly record struct D3D11CompositionTargetUpdate(
    string Id,
    D3D11CompositionTargetKind Kind,
    int Width,
    int Height,
    string BackendName,
    long Revision,
    D3D11CompositionTargetDirtyFlags DirtyFlags,
    D3D11CompositionTargetUpdateAction Action,
    bool IsActive,
    long CaptureSequence)
{
    public bool RequiresWork => Action != D3D11CompositionTargetUpdateAction.None;

    public override string ToString()
        => $"{Id}/{Kind}/{BackendName} {Width}x{Height} rev={Revision} action={Action} dirty={DirtyFlags} active={IsActive} capture={CaptureSequence}";
}
