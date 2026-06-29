namespace FatzBombMedia.MediaControl;

/// <summary>
/// Describes the framework-neutral kind of presentation surface an adapter supplies to the media-control engine.
/// </summary>
public enum MediaPresentationSurfaceKind
{
    /// <summary>No presentation surface is available.</summary>
    None = 0,

    /// <summary>A native HWND that the media-control engine can use for compositor-owned swap-chain presentation.</summary>
    NativeHwnd = 1
}
