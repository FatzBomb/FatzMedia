namespace FatzBombMedia.MediaControl.Core.Graphics.Composition.D3D11;

/// <summary>
/// Retained metadata for a compositor output target. Texture/swap-chain lifetime
/// remains owned by the presentation backend; this state only records target
/// identity, size, backend, and revision changes for future multi-target planning.
/// </summary>
internal sealed class D3D11CompositionTargetState
{
    public D3D11CompositionTargetState(string id)
    {
        Id = id;
    }

    public string Id { get; }
    public D3D11CompositionTargetDescriptor Descriptor { get; private set; }
    public D3D11CompositionTargetDirtyFlags DirtyFlags { get; private set; } = D3D11CompositionTargetDirtyFlags.None;
    public long LastCapturedSequence { get; private set; } = -1;
    public bool IsActive { get; private set; }
    public bool WasDeactivated => DirtyFlags.HasFlag(D3D11CompositionTargetDirtyFlags.Deactivated);

    public void Update(D3D11CompositionTargetDescriptor descriptor, long captureSequence)
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
            DirtyFlags = D3D11CompositionTargetDirtyFlags.None;
            LastCapturedSequence = captureSequence;
            return;
        }

        DirtyFlags = D3D11CompositionTargetDirtyFlags.Deactivated;
        LastCapturedSequence = captureSequence;
        IsActive = false;
    }


    public string LifecycleSummary
        => IsActive
            ? $"{Id}/{Descriptor.Kind}/{Descriptor.BackendName} {Descriptor.Width}x{Descriptor.Height} rev={Descriptor.Revision} dirty={DirtyFlags} capture={LastCapturedSequence}"
            : WasDeactivated
                ? $"{Id}; deactivated dirty={DirtyFlags} capture={LastCapturedSequence}"
                : $"{Id}; inactive capture={LastCapturedSequence}";

    public void MarkPlanConsumed()
    {
        DirtyFlags = D3D11CompositionTargetDirtyFlags.None;
    }

    public D3D11CompositionTargetUpdate CreateUpdatePlanEntry()
    {
        var action = D3D11CompositionTargetUpdateAction.None;
        if (DirtyFlags.HasFlag(D3D11CompositionTargetDirtyFlags.Deactivated))
        {
            action |= D3D11CompositionTargetUpdateAction.Deactivate;
        }
        else if (IsActive)
        {
            if (DirtyFlags.HasFlag(D3D11CompositionTargetDirtyFlags.Activated))
            {
                action |= D3D11CompositionTargetUpdateAction.Activate;
            }

            if (DirtyFlags.HasFlag(D3D11CompositionTargetDirtyFlags.SizeChanged))
            {
                action |= D3D11CompositionTargetUpdateAction.Resize;
            }

            if (DirtyFlags.HasFlag(D3D11CompositionTargetDirtyFlags.BackendChanged))
            {
                action |= D3D11CompositionTargetUpdateAction.BackendChange;
            }

            // A valid active target participates in the current final present even
            // when target metadata did not change.
            action |= D3D11CompositionTargetUpdateAction.Present;
        }

        return new D3D11CompositionTargetUpdate(
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

    private static D3D11CompositionTargetDirtyFlags ComputeDirtyFlags(
        D3D11CompositionTargetDescriptor previous,
        D3D11CompositionTargetDescriptor current,
        bool wasActive)
    {
        if (!current.IsValid)
        {
            return wasActive ? D3D11CompositionTargetDirtyFlags.Deactivated : D3D11CompositionTargetDirtyFlags.None;
        }

        if (!wasActive || !previous.IsValid)
        {
            return D3D11CompositionTargetDirtyFlags.Activated
                | D3D11CompositionTargetDirtyFlags.SizeChanged
                | D3D11CompositionTargetDirtyFlags.BackendChanged
                | D3D11CompositionTargetDirtyFlags.RevisionChanged;
        }

        var flags = D3D11CompositionTargetDirtyFlags.None;
        if (previous.Width != current.Width || previous.Height != current.Height)
        {
            flags |= D3D11CompositionTargetDirtyFlags.SizeChanged;
        }

        if (previous.Kind != current.Kind
            || !string.Equals(previous.BackendName, current.BackendName, StringComparison.Ordinal))
        {
            flags |= D3D11CompositionTargetDirtyFlags.BackendChanged;
        }

        if (previous.Revision != current.Revision)
        {
            flags |= D3D11CompositionTargetDirtyFlags.RevisionChanged;
        }

        return flags;
    }
}
