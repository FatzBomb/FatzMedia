namespace FatzBomb.MediaControl.Core.Graphics.Composition;

/// <summary>
/// Describes why a retained composition layer needs attention. Stage 10E records
/// these flags as scheduling metadata only; the current compositor still uses the
/// established upload/draw flow and single final present path.
/// </summary>
[Flags]
public enum MediaCompositionLayerDirtyFlags
{
    None = 0,
    Activated = 1 << 0,
    Deactivated = 1 << 1,
    SourceKindChanged = 1 << 2,
    ContentChanged = 1 << 3,
    SizeChanged = 1 << 4,
    StrideChanged = 1 << 5,
    StretchChanged = 1 << 6,
    OpacityChanged = 1 << 7,
    ColorChanged = 1 << 8,
    ResourceAllocationChanged = 1 << 9,
    UploadRequired = 1 << 10,
    RenderRequired = 1 << 11,
    FrameRateChanged = 1 << 12
}
