namespace FatzMedia.Video.Core.Graphics.Composition.Planning;

[Flags]
public enum CompositionTargetChanges
{
    None = 0,
    Activated = 1 << 0,
    Deactivated = 1 << 1,
    SizeChanged = 1 << 2,
    BackendChanged = 1 << 3,
    RevisionChanged = 1 << 4
}
