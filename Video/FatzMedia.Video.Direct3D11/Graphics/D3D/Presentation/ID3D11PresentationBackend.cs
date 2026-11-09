using Vortice.Direct3D11;

namespace FatzMedia.Video.Core.Graphics.D3D.Presentation;

/// <summary>
/// Minimal common contract for D3D11 presentation backends.  Backend implementations own
/// target texture lifetime, presentation policy, and cleanup for their specific surface type;
/// VorticeD3D11Renderer should coordinate rendering and ask the active backend to present.
/// </summary>
internal interface ID3D11PresentationBackend : IDisposable
{
    D3D11PresentationBackendKind Kind { get; }
    bool IsActive { get; }
    int Width { get; }
    int Height { get; }
    ID3D11Texture2D? TargetTexture { get; }

    /// <summary>
    /// The cached render-target view of <see cref="TargetTexture"/> (ENG-026). The backend releases
    /// it whenever it disposes or resizes that texture.
    /// </summary>
    D3D11TargetViewCache TargetViews { get; }

    /// <summary>
    /// Presents the current frame to this backend's target. Returns true when the frame was
    /// successfully presented; false when presentation failed (including GPU device loss) so
    /// callers do not raise frame-presented notifications for frames that never reached the target.
    /// </summary>
    bool Present(int liveResizeFlag, bool syncImmediate);
    void Cleanup();
}
