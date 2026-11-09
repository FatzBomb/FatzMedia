namespace FatzMedia.Video.Core.Graphics.Composition.Planning;

public sealed class CompositionTargetRegistry
{
    private readonly Dictionary<string, CompositionTargetState> _targets = [];
    private readonly List<string> _scratchActiveIds = [];
    private readonly List<CompositionTargetUpdate> _scratchTargetUpdates = [];

    public long CaptureSequence { get; private set; }
    public bool HasMultipleActiveTargets => ActiveTargetCount > 1;

    public int ActiveTargetCount
    {
        get
        {
            var count = 0;
            foreach (CompositionTargetState target in _targets.Values)
                if (target.IsActive)
                    count++;

            return count;
        }
    }

    public string LifecycleSummary
        => _targets.Count == 0
            ? "targets=none"
            : string.Join(",", _targets.Values
                .OrderBy(static target => target.Descriptor.Kind)
                .ThenBy(static target => target.Id, StringComparer.Ordinal)
                .Select(static target => target.LifecycleSummary));

    public void Capture(IEnumerable<CompositionTargetDescriptor> targets)
    {
        _scratchActiveIds.Clear();
        CaptureSequence++;

        foreach (CompositionTargetDescriptor target in targets)
        {
            if (!target.IsValid)
                continue;

            _scratchActiveIds.Add(target.Id);
            GetOrCreate(target.Id).Update(target, CaptureSequence);
        }

        // Per frame: no snapshot of the keys (ENG-026). Deactivate only changes the entry.
        foreach (KeyValuePair<string, CompositionTargetState> pair in _targets)
            if (!IsCapturedId(pair.Key))
                pair.Value.Deactivate(CaptureSequence);
    }

    private bool IsCapturedId(string id)
    {
        foreach (var capturedId in _scratchActiveIds)
            if (string.Equals(capturedId, id, StringComparison.Ordinal))
                return true;

        return false;
    }

    public CompositionTargetPlan CreatePlan()
    {
        _scratchTargetUpdates.Clear();
        foreach (CompositionTargetState target in _targets.Values)
        {
            if (target.IsActive || target.WasDeactivated)
                _scratchTargetUpdates.Add(target.CreateUpdatePlanEntry());
        }

        _scratchTargetUpdates.Sort(static (a, b) =>
        {
            var c = a.Kind.CompareTo(b.Kind);
            return c != 0 ? c : string.Compare(a.Id, b.Id, StringComparison.Ordinal);
        });

        return new CompositionTargetPlan(CaptureSequence, _scratchTargetUpdates);
    }


    public void MarkPlanConsumed()
    {
        foreach (KeyValuePair<string, CompositionTargetState> pair in _targets)
        {
            pair.Value.MarkPlanConsumed();
            // Removing the current entry while enumerating is supported by Dictionary.
            if (!pair.Value.IsActive && !pair.Value.WasDeactivated)
                _ = _targets.Remove(pair.Key);
        }
    }

    public void Reset()
    {
        _targets.Clear();
        _scratchActiveIds.Clear();
        _scratchTargetUpdates.Clear();
        CaptureSequence = 0;
    }

    private CompositionTargetState GetOrCreate(string id)
    {
        if (_targets.TryGetValue(id, out CompositionTargetState? state))
            return state;

        state = new CompositionTargetState(id);
        _targets.Add(id, state);
        return state;
    }
}
