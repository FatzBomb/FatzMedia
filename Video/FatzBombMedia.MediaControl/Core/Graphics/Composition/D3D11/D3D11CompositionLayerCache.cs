namespace FatzBombMedia.MediaControl.Core.Graphics.Composition.D3D11;

/// <summary>
/// Retained-layer cache keyed by stable layer id. Layer 0 owns retained BGRA/D3D11
/// background texture state, Layer 1 owns wash/color state, and Layer 2 owns DXCDG
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
    private long _captureSequence;

    public IReadOnlyCollection<D3D11CompositionLayerState> Layers => _layers.Values;
    public long CaptureSequence => _captureSequence;
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
        _captureSequence++;

        foreach (MediaCompositionLayer layer in frame.EnumerateLayers())
        {
            _scratchActiveIds.Add(layer.Id);
            GetOrCreate(layer.Id).Update(layer, _captureSequence);
        }

        LastCaptureHadVisibleLayers = _scratchActiveIds.Count > 0;

        foreach (MediaCompositionLayerId existingId in _layers.Keys.ToArray())
        {
            if (!_scratchActiveIds.Contains(existingId))
            {
                D3D11CompositionLayerState state = _layers[existingId];
                var wasActive = state.IsActive || state.HasRetainedResources;
                state.Clear(_captureSequence);
                if (wasActive)
                {
                    _scratchDeactivatedIds.Add(existingId);
                }
            }
        }
    }

    public D3D11CompositionLayerState? TryGet(MediaCompositionLayerId id)
        => _layers.TryGetValue(id, out D3D11CompositionLayerState? state) && state.IsActive ? state : null;

    public D3D11CompositionLayerUpdatePlan CreateUpdatePlan()
    {
        IEnumerable<D3D11CompositionLayerUpdate> updates = _layers.Values
            .Where(static layer => layer.IsActive || layer.WasDeactivated)
            .OrderBy(static layer => layer.Descriptor?.Order ?? int.MaxValue)
            .ThenBy(static layer => layer.Id)
            .Select(static layer => layer.CreateUpdatePlanEntry());

        return new D3D11CompositionLayerUpdatePlan(
            _captureSequence,
            LastCaptureIsClearOnly,
            LastCaptureDeactivatedLayers,
            updates);
    }


    public void MarkPlanConsumed()
    {
        foreach (D3D11CompositionLayerState layer in _layers.Values)
        {
            layer.MarkPlanConsumed();
        }

        foreach (MediaCompositionLayerId inactiveId in _layers
                     .Where(static pair => !pair.Value.IsActive && !pair.Value.HasRetainedResources && !pair.Value.WasDeactivated)
                     .Select(static pair => pair.Key)
                     .ToArray())
        {
            _layers.Remove(inactiveId);
        }

        _scratchDeactivatedIds.RemoveAll(id => !_layers.TryGetValue(id, out D3D11CompositionLayerState? state) || !state.WasDeactivated);
    }

    public string ActiveLayerSummary
        => string.Join(",", _layers.Values
            .Where(static layer => layer.IsActive)
            .OrderBy(static layer => layer.Descriptor?.Order ?? int.MaxValue)
            .Select(static layer => layer.Descriptor!.ToString()));

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

            if (string.IsNullOrWhiteSpace(active) && string.IsNullOrWhiteSpace(deactivated))
            {
                return LastCaptureHadVisibleLayers ? "active=none" : "active=none; clear-only";
            }

            if (string.IsNullOrWhiteSpace(deactivated))
            {
                return $"active={active}";
            }

            if (string.IsNullOrWhiteSpace(active))
            {
                return $"active=none; deactivated={deactivated}";
            }

            return $"active={active}; deactivated={deactivated}";
        }
    }

    public void Reset()
    {
        foreach (D3D11CompositionLayerState layer in _layers.Values)
        {
            layer.Clear();
        }

        _scratchDeactivatedIds.Clear();
        LastCaptureHadVisibleLayers = false;
        _captureSequence = 0;
    }

    public void Dispose()
    {
        foreach (D3D11CompositionLayerState layer in _layers.Values)
        {
            layer.Dispose();
        }

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
