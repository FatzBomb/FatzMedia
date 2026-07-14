namespace FatzBomb.MediaControl.Core.Graphics.Composition.D3D11;

/// <summary>
/// One retained-layer planning entry. It intentionally references retained state
/// rather than transient frame payloads so future schedulers can reason about the
/// layer without holding pixel buffers or CDG snapshots alive.
/// </summary>
internal readonly record struct D3D11CompositionLayerUpdate(
    MediaCompositionLayerId Id,
    MediaCompositionLayerSourceKind SourceKind,
    int Order,
    long Revision,
    MediaCompositionLayerDirtyFlags DirtyFlags,
    D3D11CompositionLayerUpdateAction Action,
    D3D11CompositionLayerSchedule Schedule,
    bool HasRetainedResources,
    long CaptureSequence)
{
    public bool RequiresWork => Action != D3D11CompositionLayerUpdateAction.None;
    public bool CanReuseRetainedOutput => Schedule.CanReuseRetainedOutput && HasRetainedResources;

    public override string ToString()
        => $"{Order}:{Id}/{SourceKind} rev={Revision} action={Action} dirty={DirtyFlags} schedule={Schedule} retained={HasRetainedResources} capture={CaptureSequence}";
}
