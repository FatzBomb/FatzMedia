namespace FatzMedia.Video.Core.Graphics.Composition.Planning;

[Flags]
public enum CompositionTargetUpdateAction
{
    None = 0,
    Activate = 1 << 0,
    Resize = 1 << 1,
    BackendChange = 1 << 2,
    Present = 1 << 3,
    Deactivate = 1 << 4
}
