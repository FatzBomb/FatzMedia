namespace FatzMedia.Video.Core.Graphics.Composition.Planning;

[Flags]
public enum CompositionLayerUpdateAction
{
    None = 0,
    Upload = 1 << 0,
    Render = 1 << 1,
    AllocateResources = 1 << 2,
    ReleaseResources = 1 << 3
}
