using FatzBomb.MediaControl.Core.Graphics.Composition.Planning;
using FatzBomb.MediaControl.Core.Video.Hdr;
using Vortice.Direct3D11;

namespace FatzBomb.MediaControl.Core.Graphics.D3D.Presentation;

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
    long revision,
    CompositionTargetKind compositionTargetKind,
    D3D11TargetViewCache? targetViews = null,
    HdrOutputMode outputMode = HdrOutputMode.Sdr)
{
    public string TargetId { get; } = targetId;

    public ID3D11Texture2D Texture { get; } = texture;

    public int Width { get; } = width;

    public int Height { get; } = height;

    public IntPtr NativeHwnd { get; } = nativeHwnd;

    public D3D11PresentationBackendKind BackendKind { get; } = backendKind;

    public string BackendName { get; } = backendName;

    public long Revision { get; } = revision;

    public CompositionTargetKind CompositionTargetKind { get; } = compositionTargetKind;

    /// <summary>
    /// The owning backend's cached render-target view of <see cref="Texture"/> (ENG-026), or null
    /// when the view is created for the draw.
    /// </summary>
    public D3D11TargetViewCache? TargetViews { get; } = targetViews;

    /// <summary>What <see cref="Texture"/> holds: SDR (8-bit sRGB) or FP16 scRGB (Stage 042A slice 2).</summary>
    public HdrOutputMode OutputMode { get; } = outputMode;

    public bool IsValid => !string.IsNullOrWhiteSpace(TargetId)
        && Texture != null
        && Width > 0
        && Height > 0
        && BackendKind != D3D11PresentationBackendKind.None
        && CompositionTargetKind != CompositionTargetKind.None;

    public CompositionTargetDescriptor ToCompositionTargetDescriptor() => new(
        TargetId,
        CompositionTargetKind,
        Width,
        Height,
        BackendName,
        Revision);
}
