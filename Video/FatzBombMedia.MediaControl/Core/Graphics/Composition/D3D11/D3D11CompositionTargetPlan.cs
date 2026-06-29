namespace FatzBombMedia.MediaControl.Core.Graphics.Composition.D3D11;

/// <summary>
/// Target-side companion to the retained layer update plan. Stage 10N makes this
/// plan active: the compositor uses it to decide which live presentation targets
/// participate in the current fan-out present.
/// </summary>
internal sealed class D3D11CompositionTargetPlan
{
    private readonly D3D11CompositionTargetUpdate[] _targets;

    public D3D11CompositionTargetPlan(long captureSequence, IEnumerable<D3D11CompositionTargetUpdate> targets)
    {
        CaptureSequence = captureSequence;
        _targets = targets.ToArray();
    }

    public long CaptureSequence { get; }
    public IReadOnlyList<D3D11CompositionTargetUpdate> Targets => _targets;
    public bool HasTargets => _targets.Length > 0;
    public int ActiveTargetCount => _targets.Count(static target => target.IsActive);
    public int DirtyTargetCount => _targets.Count(static target => target.RequiresWork);
    public int DeactivatedTargetCount => _targets.Count(static target => !target.IsActive && target.Action.HasFlag(D3D11CompositionTargetUpdateAction.Deactivate));
    public bool HasMultipleTargets => ActiveTargetCount > 1;
    public bool HasWork => _targets.Any(static target => target.RequiresWork);

    public bool ShouldPresentTarget(string targetId) => _targets.Any(target =>
        target.IsActive
        && target.Action.HasFlag(D3D11CompositionTargetUpdateAction.Present)
        && string.Equals(target.Id, targetId, StringComparison.Ordinal));

    public IReadOnlyList<string> ActivePresentationTargetIds => _targets
        .Where(static target => target.IsActive && target.Action.HasFlag(D3D11CompositionTargetUpdateAction.Present))
        .Select(static target => target.Id)
        .ToArray();

    public string Summary
    {
        get
        {
            var targetSummary = _targets.Length == 0
                ? "none"
                : string.Join(",", _targets.Select(static target => target.ToString()));

            return $"targetCapture={CaptureSequence}; active={ActiveTargetCount}; dirty={DirtyTargetCount}; deactivated={DeactivatedTargetCount}; multiTarget={HasMultipleTargets}; targets={targetSummary}";
        }
    }
}
