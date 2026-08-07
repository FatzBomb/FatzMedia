using Vortice.Direct3D11;

namespace FatzBomb.MediaControl.Core.Graphics.D3D.Presentation;

/// <summary>
/// Framework-neutral bridge used by the core presentation helpers to query and
/// manage the currently active presentation target. Framework adapters own how a
/// surface is obtained; the core presentation helpers own how frames are blitted
/// and cleaned up against the surface exposed here.
/// </summary>
internal interface ID3D11PresentationSurfaceProvider
{
    ID3D11Texture2D? TargetTexture { get; }

    bool TryGetActiveTargetSnapshot(
        bool applyPendingNativeResize,
        string? reason,
        out D3D11PresentationTargetSnapshot snapshot);

    bool OwnsTexture(ID3D11Texture2D? texture);

    void CleanupBackends();

    /// <summary>
    /// Disposes only the primary presentation backends, retaining secondary native HWND targets.
    /// </summary>
    void CleanupPrimaryBackends();

    void Reset();
}
