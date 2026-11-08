namespace FatzBomb.MediaControl.Core.Graphics.Composition.Planning;

public sealed class CompositionTargetState(string id)
{
    public string Id { get; } = id;
    public CompositionTargetDescriptor Descriptor { get; private set; }
    public CompositionTargetDirtyFlags DirtyFlags { get; private set; } = CompositionTargetDirtyFlags.None;
    public long LastCapturedSequence { get; private set; } = -1;
    public bool IsActive { get; private set; }
    public bool WasDeactivated => DirtyFlags.HasFlag(CompositionTargetDirtyFlags.Deactivated);

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
            DirtyFlags = CompositionTargetDirtyFlags.None;
            LastCapturedSequence = captureSequence;
            return;
        }

        DirtyFlags = CompositionTargetDirtyFlags.Deactivated;
        LastCapturedSequence = captureSequence;
        IsActive = false;
    }


    public string LifecycleSummary
        => IsActive
            ? $"{Id}/{Descriptor.Kind}/{Descriptor.BackendName} {Descriptor.Width}x{Descriptor.Height} rev={Descriptor.Revision} dirty={DirtyFlags} capture={LastCapturedSequence}"
            : WasDeactivated
                ? $"{Id}; deactivated dirty={DirtyFlags} capture={LastCapturedSequence}"
                : $"{Id}; inactive capture={LastCapturedSequence}";

    public void MarkPlanConsumed() => DirtyFlags = CompositionTargetDirtyFlags.None;

    public CompositionTargetUpdate CreateUpdatePlanEntry()
    {
        CompositionTargetUpdateAction action = CompositionTargetUpdateAction.None;
        if (DirtyFlags.HasFlag(CompositionTargetDirtyFlags.Deactivated))
        {
            action |= CompositionTargetUpdateAction.Deactivate;
        }
        else if (IsActive)
        {
            if (DirtyFlags.HasFlag(CompositionTargetDirtyFlags.Activated))
            {
                action |= CompositionTargetUpdateAction.Activate;
            }

            if (DirtyFlags.HasFlag(CompositionTargetDirtyFlags.SizeChanged))
            {
                action |= CompositionTargetUpdateAction.Resize;
            }

            if (DirtyFlags.HasFlag(CompositionTargetDirtyFlags.BackendChanged))
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

    private static CompositionTargetDirtyFlags ComputeDirtyFlags(
        CompositionTargetDescriptor previous,
        CompositionTargetDescriptor current,
        bool wasActive)
    {
        if (!current.IsValid)
            return wasActive ? CompositionTargetDirtyFlags.Deactivated : CompositionTargetDirtyFlags.None;

        if (!wasActive || !previous.IsValid)
            return CompositionTargetDirtyFlags.Activated
                | CompositionTargetDirtyFlags.SizeChanged
                | CompositionTargetDirtyFlags.BackendChanged
                | CompositionTargetDirtyFlags.RevisionChanged;

        CompositionTargetDirtyFlags flags = CompositionTargetDirtyFlags.None;
        if (previous.Width != current.Width || previous.Height != current.Height)
            flags |= CompositionTargetDirtyFlags.SizeChanged;

        if (previous.Kind != current.Kind || !string.Equals(previous.BackendName, current.BackendName, StringComparison.Ordinal))
            flags |= CompositionTargetDirtyFlags.BackendChanged;

        if (previous.Revision != current.Revision)
            flags |= CompositionTargetDirtyFlags.RevisionChanged;

        return flags;
    }
}
