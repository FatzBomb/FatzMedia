using FatzBombMedia.MediaControl.Core.Graphics.Composition.D3D11;
using Vortice.Direct3D11;

namespace FatzBombMedia.MediaControl.Core.Graphics.D3D.Presentation;

/// <summary>
/// Immutable view of a presentation target at the point a frame is about to be
/// uploaded or presented.  This keeps target texture, dimensions, backend identity,
/// stable target id, and revision together so resize/adoption changes do not get
/// mixed with stale frame state.
/// </summary>
internal readonly struct D3D11PresentationTargetSnapshot(
    string targetId,
    ID3D11Texture2D texture,
    int width,
    int height,
    IntPtr nativeHwnd,
    D3D11PresentationBackendKind backendKind,
    string backendName,
    long revision)
{
    public string TargetId { get; } = targetId;

    public ID3D11Texture2D Texture { get; } = texture;

    public int Width { get; } = width;

    public int Height { get; } = height;

    public IntPtr NativeHwnd { get; } = nativeHwnd;

    public D3D11PresentationBackendKind BackendKind { get; } = backendKind;

    public string BackendName { get; } = backendName;

    public long Revision { get; } = revision;

    public bool IsValid => !string.IsNullOrWhiteSpace(TargetId)
        && Texture != null
        && Width > 0
        && Height > 0
        && BackendKind != D3D11PresentationBackendKind.None;

    public D3D11CompositionTargetDescriptor ToCompositionTargetDescriptor() => new(
        TargetId,
        D3D11CompositionTargetKind.FinalPresentation,
        Width,
        Height,
        BackendName,
        Revision);
}
