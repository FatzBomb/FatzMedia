namespace FatzBombMedia.MediaControl.Core.Cdg;

/// <summary>
/// Optional side-by-side CDG snapshot source. This does not affect the legacy
/// bitmap CDG output; adapters/renderers can use it to feed experimental CDG
/// presenters while the existing CDG path remains authoritative.
/// </summary>
public interface ICdgSnapshotSource
{
    bool TryCreateCdgSnapshot(bool consumeDirtyRegions, out CdgRenderSnapshot snapshot);
}
