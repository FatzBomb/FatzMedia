namespace FatzBomb.MediaControl.Core.Cdg;

/// <summary>
/// Optional CDG snapshot source. Renderers use the indexed/palette snapshot it
/// produces to draw the CDG overlay layer.
/// </summary>
public interface ICdgSnapshotSource
{
    bool TryCreateCdgSnapshot(bool consumeDirtyRegions, out CdgRenderSnapshot snapshot);
}
