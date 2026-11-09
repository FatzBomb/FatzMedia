namespace FatzMedia.Video.Core.Graphics.Composition.Planning;

public sealed class CompositionTargetState(string id)
{
    public string Id { get; } = id;
    public CompositionTargetDescriptor Descriptor { get; private set; }
    public CompositionTargetChanges DirtyFlags { get; private set; } = CompositionTargetChanges.None;
    public long LastCapturedSequence { get; private set; } = -1;
    public bool IsActive { get; private set; }
    public bool WasDeactivated => DirtyFlags.HasFlag(CompositionTargetChanges.Deactivated);

    public void Update(CompositionTargetDescriptor descriptor, long captureSequence)
    {
        DirtyFlags = ComputeDirtyFlags(Descriptor, descriptor, IsActive);
        Descriptor = descriptor;
        LastCapturedSequence = captureSequence;
        IsActive = descriptor.IsValid;
    }

    public void Deactivate(long captureSequence)
    {
        if (!IsActive)
        {
            DirtyFlags = CompositionTargetChanges.None;
            LastCapturedSequence = captureSequence;
            return;
        }

        DirtyFlags = CompositionTargetChanges.Deactivated;
        LastCapturedSequence = captureSequence;
        IsActive = false;
    }


    public string LifecycleSummary
        => IsActive
            ? $"{Id}/{Descriptor.Kind}/{Descriptor.BackendName} {Descriptor.Width}x{Descriptor.Height} rev={Descriptor.Revision} dirty={DirtyFlags} capture={LastCapturedSequence}"
            : WasDeactivated
                ? $"{Id}; deactivated dirty={DirtyFlags} capture={LastCapturedSequence}"
                : $"{Id}; inactive capture={LastCapturedSequence}";

    public void MarkPlanConsumed() => DirtyFlags = CompositionTargetChanges.None;

    public CompositionTargetUpdate CreateUpdatePlanEntry()
    {
        var action = CompositionTargetUpdateAction.None;
        if (DirtyFlags.HasFlag(CompositionTargetChanges.Deactivated))
        {
            action |= CompositionTargetUpdateAction.Deactivate;
        }
        else if (IsActive)
        {
            if (DirtyFlags.HasFlag(CompositionTargetChanges.Activated))
            {
                action |= CompositionTargetUpdateAction.Activate;
            }

            if (DirtyFlags.HasFlag(CompositionTargetChanges.SizeChanged))
            {
                action |= CompositionTargetUpdateAction.Resize;
            }

            if (DirtyFlags.HasFlag(CompositionTargetChanges.BackendChanged))
            {
                action |= CompositionTargetUpdateAction.BackendChange;
            }

            action |= CompositionTargetUpdateAction.Present;
        }

        return new CompositionTargetUpdate(
            Id,
            Descriptor.Kind,
            Descriptor.Width,
            Descriptor.Height,
            Descriptor.BackendName,
            Descriptor.Revision,
            DirtyFlags,
            action,
            IsActive,
            LastCapturedSequence);
    }

    static CompositionTargetChanges ComputeDirtyFlags(
        CompositionTargetDescriptor previous,
        CompositionTargetDescriptor current,
        bool wasActive)
    {
        if (!current.IsValid)
            return wasActive ? CompositionTargetChanges.Deactivated : CompositionTargetChanges.None;

        if (!wasActive || !previous.IsValid)
            return CompositionTargetChanges.Activated
                | CompositionTargetChanges.SizeChanged
                | CompositionTargetChanges.BackendChanged
                | CompositionTargetChanges.RevisionChanged;

        var flags = CompositionTargetChanges.None;
        if (previous.Width != current.Width || previous.Height != current.Height)
            flags |= CompositionTargetChanges.SizeChanged;

        if (previous.Kind != current.Kind || !string.Equals(previous.BackendName, current.BackendName, StringComparison.Ordinal))
            flags |= CompositionTargetChanges.BackendChanged;

        if (previous.Revision != current.Revision)
            flags |= CompositionTargetChanges.RevisionChanged;

        return flags;
    }
}
