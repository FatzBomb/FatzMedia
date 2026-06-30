namespace FatzBombMedia.MediaControl.Core.Graphics.Composition.D3D11;

/// <summary>
/// Captures retained-layer update intent for the current compositor pass. Stage
/// 10H/10I does not introduce a scheduler yet; Stage 10K adds cadence metadata
/// while the existing compositor still performs the same immediate upload/draw/
/// present sequence.
/// </summary>
internal sealed class D3D11CompositionLayerUpdatePlan(
    long captureSequence,
    bool isClearOnly,
    bool deactivatedLayers,
    IEnumerable<D3D11CompositionLayerUpdate> updates)
{
    private readonly D3D11CompositionLayerUpdate[] _updates = [.. updates];

    public long CaptureSequence { get; } = captureSequence;
    public bool IsClearOnly { get; } = isClearOnly;
    public bool DeactivatedLayers { get; } = deactivatedLayers;
    public IReadOnlyList<D3D11CompositionLayerUpdate> Updates => _updates;
    public int LayerCount => _updates.Length;
    public int WorkItemCount => _updates.Count(static update => update.RequiresWork);
    public int ReusableLayerCount => _updates.Count(static update => update.CanReuseRetainedOutput);
    public bool HasWork => IsClearOnly || WorkItemCount > 0;
    public bool HasSourceRateLayers => _updates.Any(static update => update.Schedule.Cadence == D3D11CompositionLayerUpdateCadence.SourceRate);
    public bool HasReusableRetainedOutputs => _updates.Any(static update => update.CanReuseRetainedOutput);
    public bool HasFutureOffscreenTargets => _updates.Any(static update => update.Schedule.TargetKind == D3D11CompositionLayerRenderTargetKind.RetainedOffscreenTexture);

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
