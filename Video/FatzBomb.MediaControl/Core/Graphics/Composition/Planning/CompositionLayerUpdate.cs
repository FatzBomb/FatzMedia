namespace FatzBomb.MediaControl.Core.Graphics.Composition.Planning;

public readonly record struct CompositionLayerUpdate(
    MediaCompositionLayerId Id,
    MediaCompositionLayerSourceKind SourceKind,
    int Order,
    long Revision,
    MediaCompositionLayerDirtyFlags DirtyFlags,
    CompositionLayerUpdateAction Action,
    CompositionLayerSchedule Schedule,
    bool HasRetainedResources,
    long CaptureSequence)
{
    public bool RequiresWork => Action != CompositionLayerUpdateAction.None;
    public bool CanReuseRetainedOutput => Schedule.CanReuseRetainedOutput && HasRetainedResources;

    public override string ToString()
        => $"{Order}:{Id}/{SourceKind} rev={Revision} action={Action} dirty={DirtyFlags} schedule={Schedule} retained={HasRetainedResources} capture={CaptureSequence}";
}
