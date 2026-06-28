namespace FatzBombMedia.MediaControl.Core.DxCdg;

/// <summary>
/// Optional side-by-side DXCDG snapshot source. This does not affect the legacy
/// bitmap CDG output; adapters/renderers can use it to feed experimental DXCDG
/// presenters while the existing CDG path remains authoritative.
/// </summary>
public interface IDxCdgSnapshotSource
{
    bool TryCreateDxCdgSnapshot(bool consumeDirtyRegions, out DxCdgRenderSnapshot snapshot);
}
