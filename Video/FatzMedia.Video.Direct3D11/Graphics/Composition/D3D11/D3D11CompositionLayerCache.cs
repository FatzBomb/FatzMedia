using FatzMedia.Video.Core.Graphics.Composition.Planning;

namespace FatzMedia.Video.Core.Graphics.Composition.D3D11;

/// <summary>
/// Retained-layer cache keyed by stable layer id. Layer 0 owns retained BGRA/D3D11
/// background texture state, Layer 1 owns wash/color state, and Layer 2 owns CDG
/// index/palette texture state while the compositor still performs one final draw/present.
/// Stage 10E adds capture sequencing and dirty metadata for future layer scheduling;
/// Stage 10G adds lifecycle/deactivation summaries used by verbose diagnostics;
/// Stage 10H/10I adds clear-only guardrails and update-planner metadata.
/// </summary>
internal sealed class D3D11CompositionLayerCache : IDisposable
{
    private readonly Dictionary<MediaCompositionLayerId, D3D11CompositionLayerState> _layers = [];
    private readonly List<MediaCompositionLayerId> _scratchActiveIds = [];
    private readonly List<MediaCompositionLayerId> _scratchDeactivatedIds = [];
    private readonly List<CompositionLayerUpdate> _scratchUpdates = [];

    public long CaptureSequence { get; private set; }
    public bool LastCaptureHadVisibleLayers { get; private set; }
    public bool LastCaptureIsClearOnly => !LastCaptureHadVisibleLayers;
    public bool LastCaptureDeactivatedLayers => _scratchDeactivatedIds.Count > 0;
    public bool HasRetainedResources => _layers.Values.Any(static layer => layer.HasRetainedResources);

    public string RetainedResourceSummary
        => string.Join(",", _layers.Values
            .Where(static layer => layer.HasRetainedResources)
            .OrderBy(static layer => layer.Descriptor?.Order ?? int.MaxValue)
            .Select(static layer => layer.LifecycleSummary));

    public void Capture(MediaCompositionFrame frame)
    {
        _scratchActiveIds.Clear();
        _scratchDeactivatedIds.Clear();
        CaptureSequence++;

        foreach (MediaCompositionLayer layer in frame.EnumerateLayers())
        {
            _scratchActiveIds.Add(layer.Id);
            GetOrCreate(layer.Id).Update(layer, CaptureSequence);
        }

        LastCaptureHadVisibleLayers = _scratchActiveIds.Count > 0;

        foreach (MediaCompositionLayerId existingId in _layers.Keys)
        {
            if (!_scratchActiveIds.Contains(existingId))
            {
                D3D11CompositionLayerState state = _layers[existingId];
                var wasActive = state.IsActive || state.HasRetainedResources;
                state.Clear(CaptureSequence);
                if (wasActive)
                    _scratchDeactivatedIds.Add(existingId);
            }
        }
    }

    public D3D11CompositionLayerState? TryGet(MediaCompositionLayerId id)
        => _layers.TryGetValue(id, out D3D11CompositionLayerState? state) && state.IsActive ? state : null;

    public CompositionLayerUpdatePlan CreateUpdatePlan()
    {
        _scratchUpdates.Clear();
        foreach (D3D11CompositionLayerState layer in _layers.Values)
        {
            if (layer.IsActive || layer.WasDeactivated)
                _scratchUpdates.Add(layer.CreateUpdatePlanEntry());
        }

        _scratchUpdates.Sort(static (a, b) =>
        {
            var c = a.Order.CompareTo(b.Order);
            return c != 0 ? c : a.Id.CompareTo(b.Id);
        });

        return new CompositionLayerUpdatePlan(
            CaptureSequence,
            LastCaptureIsClearOnly,
            LastCaptureDeactivatedLayers,
            _scratchUpdates);
    }


    public void MarkPlanConsumed()
    {
        foreach (D3D11CompositionLayerState layer in _layers.Values)
            layer.MarkPlanConsumed();

        foreach (MediaCompositionLayerId inactiveId in _layers
            .Where(static pair => !pair.Value.IsActive && !pair.Value.HasRetainedResources && !pair.Value.WasDeactivated)
            .Select(static pair => pair.Key)
            .ToArray())
            _ = _layers.Remove(inactiveId);

        _ = _scratchDeactivatedIds.RemoveAll(id => !_layers.TryGetValue(id, out D3D11CompositionLayerState? state) || !state.WasDeactivated);
    }

    public string ActiveLayerStateSummary
        => string.Join(",", _layers.Values
            .Where(static layer => layer.IsActive)
            .OrderBy(static layer => layer.Descriptor?.Order ?? int.MaxValue)
            .Select(static layer => layer.LifecycleSummary));

    public string LastDeactivatedLayerSummary
        => string.Join(",", _scratchDeactivatedIds
            .Select(id => _layers.TryGetValue(id, out D3D11CompositionLayerState? state) ? state.LifecycleSummary : id.ToString()));

    public string CaptureLifecycleSummary
    {
        get
        {
            var active = ActiveLayerStateSummary;
            var deactivated = LastDeactivatedLayerSummary;

            return string.IsNullOrWhiteSpace(active) && string.IsNullOrWhiteSpace(deactivated)
                ? LastCaptureHadVisibleLayers ? "active=none" : "active=none; clear-only"
                : string.IsNullOrWhiteSpace(deactivated)
                    ? $"active={active}"
                    : string.IsNullOrWhiteSpace(active) ? $"active=none; deactivated={deactivated}" : $"active={active}; deactivated={deactivated}";
        }
    }

    public void Reset()
    {
        foreach (D3D11CompositionLayerState layer in _layers.Values)
            layer.Clear();

        _scratchDeactivatedIds.Clear();
        _scratchUpdates.Clear();
        LastCaptureHadVisibleLayers = false;
        CaptureSequence = 0;
    }

    public void Dispose()
    {
        foreach (D3D11CompositionLayerState layer in _layers.Values)
            layer.Dispose();

        _layers.Clear();
        _scratchActiveIds.Clear();
        _scratchDeactivatedIds.Clear();
    }

    public D3D11CompositionLayerState GetOrCreate(MediaCompositionLayerId id)
    {
        if (_layers.TryGetValue(id, out D3D11CompositionLayerState? state))
        {
            return state;
        }

        state = new D3D11CompositionLayerState(id);
        _layers.Add(id, state);
        return state;
    }
}
