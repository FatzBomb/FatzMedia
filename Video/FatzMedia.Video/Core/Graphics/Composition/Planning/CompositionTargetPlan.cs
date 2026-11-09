namespace FatzMedia.Video.Core.Graphics.Composition.Planning;

public sealed class CompositionTargetPlan(long captureSequence, IEnumerable<CompositionTargetUpdate> targets)
{
    private readonly CompositionTargetUpdate[] _targets = [.. targets];

    public long CaptureSequence { get; } = captureSequence;
    public int ActiveTargetCount => _targets.Count(static target => target.IsActive);
    public int DirtyTargetCount => _targets.Count(static target => target.RequiresWork);
    public int ResizeTargetCount => _targets.Count(static target => target.Action.HasFlag(CompositionTargetUpdateAction.Resize));
    public int PresentTargetCount => _targets.Count(static target => target.IsActive && target.Action.HasFlag(CompositionTargetUpdateAction.Present));
    public int MetadataWorkTargetCount => _targets.Count(static target => (target.Action & ~CompositionTargetUpdateAction.Present) != CompositionTargetUpdateAction.None);
    public int DeactivatedTargetCount => _targets.Count(static target => !target.IsActive && target.Action.HasFlag(CompositionTargetUpdateAction.Deactivate));
    public bool HasMultipleTargets => ActiveTargetCount > 1;
    public bool HasMetadataWork => MetadataWorkTargetCount > 0;

    public bool ShouldPresentTarget(string targetId)
    {
        // Called per target per frame: a loop, not a capturing lambda (ENG-026).
        foreach (CompositionTargetUpdate target in _targets)
        {
            if (target.IsActive
                && target.Action.HasFlag(CompositionTargetUpdateAction.Present)
                && string.Equals(target.Id, targetId, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

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
