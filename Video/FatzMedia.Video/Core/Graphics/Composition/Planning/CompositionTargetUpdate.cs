namespace FatzMedia.Video.Core.Graphics.Composition.Planning;

public readonly record struct CompositionTargetUpdate(
    string Id,
    CompositionTargetKind Kind,
    int Width,
    int Height,
    string BackendName,
    long Revision,
    CompositionTargetChanges DirtyFlags,
    CompositionTargetUpdateAction Action,
    bool IsActive,
    long CaptureSequence)
{
    public bool RequiresWork => Action != CompositionTargetUpdateAction.None;

    public override string ToString()
        => $"{Id}/{Kind}/{BackendName} {Width}x{Height} rev={Revision} action={Action} dirty={DirtyFlags} active={IsActive} capture={CaptureSequence}";
}
