namespace FatzBomb.MediaControl.Core.Graphics.Composition.Planning;

public sealed class CompositionTargetRegistry
{
    private readonly Dictionary<string, CompositionTargetState> _targets = [];
    private readonly List<string> _scratchActiveIds = [];
    private readonly List<CompositionTargetUpdate> _scratchTargetUpdates = [];

    public long CaptureSequence { get; private set; }
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

        foreach (var existingId in _targets.Keys.ToArray())
            if (!_scratchActiveIds.Contains(existingId, StringComparer.Ordinal))
                _targets[existingId].Deactivate(CaptureSequence);
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
        foreach (CompositionTargetState target in _targets.Values)
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
