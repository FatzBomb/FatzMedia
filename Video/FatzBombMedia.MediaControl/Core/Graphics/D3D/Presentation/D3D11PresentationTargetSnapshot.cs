using Vortice.Direct3D11;

namespace FatzBombMedia.MediaControl.Core.Graphics.D3D.Presentation;

/// <summary>
/// Immutable view of the active presentation target at the point a frame is about to be
/// uploaded or presented.  This keeps target texture, dimensions, backend identity, and
/// revision together so resize/adoption changes do not get mixed with stale frame state.
/// </summary>
internal readonly struct D3D11PresentationTargetSnapshot
{
    public D3D11PresentationTargetSnapshot(
        ID3D11Texture2D texture,
        int width,
        int height,
        IntPtr nativeHwnd,
        D3D11PresentationBackendKind backendKind,
        string backendName,
        long revision)
    {
        Texture = texture;
        Width = width;
        Height = height;
        NativeHwnd = nativeHwnd;
        BackendKind = backendKind;
        BackendName = backendName;
        Revision = revision;
    }

    public ID3D11Texture2D Texture { get; }

    public int Width { get; }

    public int Height { get; }

    public IntPtr NativeHwnd { get; }

    public D3D11PresentationBackendKind BackendKind { get; }

    public string BackendName { get; }

    public long Revision { get; }

    public bool IsValid => Texture != null && Width > 0 && Height > 0 && BackendKind != D3D11PresentationBackendKind.None;
}
