namespace FatzBomb.MediaControl.Core.Graphics.Composition.D3D11;

/// <summary>
/// Target-side companion to the retained layer update plan. Stage 10N makes this
/// plan active: the compositor uses it to decide which live presentation targets
/// participate in the current fan-out present.
/// </summary>
internal sealed class D3D11CompositionTargetPlan(long captureSequence, IEnumerable<D3D11CompositionTargetUpdate> targets)
{
    private readonly D3D11CompositionTargetUpdate[] _targets = [.. targets];

    public long CaptureSequence { get; } = captureSequence;
    public int ActiveTargetCount => _targets.Count(static target => target.IsActive);
    public int DirtyTargetCount => _targets.Count(static target => target.RequiresWork);
    public int ResizeTargetCount => _targets.Count(static target => target.Action.HasFlag(D3D11CompositionTargetUpdateAction.Resize));
    public int PresentTargetCount => _targets.Count(static target => target.IsActive && target.Action.HasFlag(D3D11CompositionTargetUpdateAction.Present));
    public int MetadataWorkTargetCount => _targets.Count(static target => (target.Action & ~D3D11CompositionTargetUpdateAction.Present) != D3D11CompositionTargetUpdateAction.None);
    public int DeactivatedTargetCount => _targets.Count(static target => !target.IsActive && target.Action.HasFlag(D3D11CompositionTargetUpdateAction.Deactivate));
    public bool HasMultipleTargets => ActiveTargetCount > 1;
    public bool HasMetadataWork => MetadataWorkTargetCount > 0;

    public bool ShouldPresentTarget(string targetId) => _targets.Any(target =>
        target.IsActive
        && target.Action.HasFlag(D3D11CompositionTargetUpdateAction.Present)
        && string.Equals(target.Id, targetId, StringComparison.Ordinal));

    public string Summary
    {
        get
        {
            var targetSummary = _targets.Length == 0
                ? "none"
                : string.Join(",", _targets.Select(static target => target.ToString()));

            return $"targetCapture={CaptureSequence}; active={ActiveTargetCount}; present={PresentTargetCount}; dirty={DirtyTargetCount}; metadataWork={MetadataWorkTargetCount}; resize={ResizeTargetCount}; deactivated={DeactivatedTargetCount}; multiTarget={HasMultipleTargets}; targets={targetSummary}";
        }
    }
}
