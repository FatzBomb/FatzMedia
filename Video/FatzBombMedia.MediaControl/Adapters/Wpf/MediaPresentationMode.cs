namespace FatzBombMedia.MediaControl.Adapters.Wpf;

/// <summary>
/// Presentation modes exposed by <see cref="FatzBombMediaSurface"/>.
/// </summary>
public enum MediaPresentationMode
{
    /// <summary>
    /// Default WPF-friendly path. Frames are presented through WPF D3DImage when D3D11 is available,
    /// with WriteableBitmap reserved for fallback content. This avoids WPF airspace problems.
    /// </summary>
    D3DImage,

    /// <summary>
    /// Native child-HWND/swap-chain target. This bypasses WPF D3DImage/D3D9 interop so it can be used
    /// as a strict diagnostic/pro presentation path.
    /// </summary>
    NativeHwnd,

    /// <summary>
    /// Native child-HWND/swap-chain target plus a transparent owned WPF overlay window.
    /// This keeps native video presentation smooth while allowing hosts to provide overlay XAML through
    /// <see cref="FatzBombMediaSurface.OverlayContent"/>.
    /// </summary>
    NativeHwndOverlay
}
