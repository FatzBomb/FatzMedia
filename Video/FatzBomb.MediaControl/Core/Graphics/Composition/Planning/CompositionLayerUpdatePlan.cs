namespace FatzBomb.MediaControl.Core.Graphics.Composition.Planning;

public sealed class CompositionLayerUpdatePlan(
    long captureSequence,
    bool isClearOnly,
    bool deactivatedLayers,
    IEnumerable<CompositionLayerUpdate> updates)
{
    private readonly CompositionLayerUpdate[] _updates = [.. updates];

    public long CaptureSequence { get; } = captureSequence;
    public bool IsClearOnly { get; } = isClearOnly;
    public bool DeactivatedLayers { get; } = deactivatedLayers;
    public IReadOnlyList<CompositionLayerUpdate> Updates => _updates;
    public int LayerCount => _updates.Length;
    public int WorkItemCount => _updates.Count(static update => update.RequiresWork);
    public int ReusableLayerCount => _updates.Count(static update => update.CanReuseRetainedOutput);
    public bool HasWork => IsClearOnly || WorkItemCount > 0;
    public bool HasSourceRateLayers => _updates.Any(static update => update.Schedule.Cadence == CompositionLayerUpdateCadence.SourceRate);
    public bool HasReusableRetainedOutputs => _updates.Any(static update => update.CanReuseRetainedOutput);
    public bool HasFutureOffscreenTargets => _updates.Any(static update => update.Schedule.TargetKind == CompositionLayerRenderTargetKind.RetainedOffscreenTexture);

    public string Summary
    {
        get
        {
            if (IsClearOnly && _updates.Length == 0)
            {
                return $"capture={CaptureSequence}; clear-only";
            }

            var layerSummary = _updates.Length == 0
                ? "none"
                : string.Join(",", _updates.Select(static update => update.ToString()));

            return $"capture={CaptureSequence}; clearOnly={IsClearOnly}; layers={LayerCount}; work={WorkItemCount}; reusableCount={ReusableLayerCount}; deactivated={DeactivatedLayers}; sourceRate={HasSourceRateLayers}; reusable={HasReusableRetainedOutputs}; futureOffscreen={HasFutureOffscreenTargets}; updates={layerSummary}";
        }
    }
}
