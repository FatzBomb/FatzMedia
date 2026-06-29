namespace FatzBombMedia.MediaControl.Core.Graphics.Composition.D3D11;

/// <summary>
/// Metadata describing why a compositor target changed between captures. The
/// current compositor still presents to one active target; these flags prepare
/// future multi-target scheduling and diagnostics.
/// </summary>
[Flags]
internal enum D3D11CompositionTargetDirtyFlags
{
    None = 0,
    Activated = 1 << 0,
    Deactivated = 1 << 1,
    SizeChanged = 1 << 2,
    BackendChanged = 1 << 3,
    RevisionChanged = 1 << 4
}
