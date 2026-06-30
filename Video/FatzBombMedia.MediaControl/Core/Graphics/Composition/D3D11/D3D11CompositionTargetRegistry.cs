namespace FatzBombMedia.MediaControl.Core.Graphics.Composition.D3D11;

/// <summary>
/// Retained registry of compositor output targets. Stage 10N consumes the registry
/// plan so primary and secondary swap-chain targets are driven by the same unified
/// compositor pass.
/// </summary>
internal sealed class D3D11CompositionTargetRegistry
{
    private readonly Dictionary<string, D3D11CompositionTargetState> _targets = [];
    private readonly List<string> _scratchActiveIds = [];

    public long CaptureSequence { get; private set; }
    public IReadOnlyCollection<D3D11CompositionTargetState> Targets => _targets.Values;
    public bool HasMultipleActiveTargets => _targets.Values.Count(static target => target.IsActive) > 1;
    public int ActiveTargetCount => _targets.Values.Count(static target => target.IsActive);
    public int PendingDeactivationCount => _targets.Values.Count(static target => target.WasDeactivated);

    public string LifecycleSummary
        => _targets.Count == 0
            ? "targets=none"
            : string.Join(",", _targets.Values
                .OrderBy(static target => target.Descriptor.Kind)
                .ThenBy(static target => target.Id, StringComparer.Ordinal)
                .Select(static target => target.LifecycleSummary));

    public void Capture(D3D11CompositionTargetDescriptor target)
    {
        if (target.IsValid)
        {
            Capture([target]);
            return;
        }

        Capture([]);
    }

    public void Capture(IEnumerable<D3D11CompositionTargetDescriptor> targets)
    {
        _scratchActiveIds.Clear();
        CaptureSequence++;

        foreach (D3D11CompositionTargetDescriptor target in targets)
        {
            if (!target.IsValid)
                continue;

            _scratchActiveIds.Add(target.Id);
            GetOrCreate(target.Id).Update(target, CaptureSequence);
        }

        foreach (var existingId in _targets.Keys.ToArray())
            if (!_scratchActiveIds.Contains(existingId, StringComparer.Ordinal))
                _targets[existingId].Deactivate(CaptureSequence);
    }

    public D3D11CompositionTargetPlan CreatePlan()
    {
        IEnumerable<D3D11CompositionTargetUpdate> updates = _targets.Values
            .Where(static target => target.IsActive || target.WasDeactivated)
            .OrderBy(static target => target.Descriptor.Kind)
            .ThenBy(static target => target.Id, StringComparer.Ordinal)
            .Select(static target => target.CreateUpdatePlanEntry());

        return new D3D11CompositionTargetPlan(CaptureSequence, updates);
    }


    public void MarkPlanConsumed()
    {
        foreach (D3D11CompositionTargetState target in _targets.Values)
            target.MarkPlanConsumed();

        foreach (var inactiveId in _targets
                     .Where(static pair => !pair.Value.IsActive && !pair.Value.WasDeactivated)
                     .Select(static pair => pair.Key)
                     .ToArray())
            _ = _targets.Remove(inactiveId);
    }

    public void Reset()
    {
        _targets.Clear();
        _scratchActiveIds.Clear();
        CaptureSequence = 0;
    }

    private D3D11CompositionTargetState GetOrCreate(string id)
    {
        if (_targets.TryGetValue(id, out D3D11CompositionTargetState? state))
            return state;

        state = new D3D11CompositionTargetState(id);
        _targets.Add(id, state);
        return state;
    }
}
