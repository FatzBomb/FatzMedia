using FatzBomb.MediaControl.Core.Cdg;
using FatzBomb.MediaControl.Core.Graphics;

namespace FatzBomb.MediaControl.Core.Graphics.Composition;

/// <summary>
/// Retained-layer intent produced by <see cref="MediaCompositionFrame" />.
/// This is intentionally descriptive in Stage 10A: the existing Stage 9 draw path
/// still consumes the compatibility fields, while later Stage 10 patches can move
/// upload/render ownership into per-layer targets without changing media producers.
/// </summary>
public sealed record MediaCompositionLayer
{
    public MediaCompositionLayerId Id { get; init; }
    public MediaCompositionLayerKind Kind { get; init; }
    public MediaCompositionLayerSourceKind SourceKind { get; init; }
    public int Order { get; init; }
    public long Revision { get; init; } = -1;
    public int Width { get; init; }
    public int Height { get; init; }
    public int Stride { get; init; }
    public int CanvasLeft { get; init; }
    public int CanvasTop { get; init; }
    public int CanvasWidth { get; init; }
    public int CanvasHeight { get; init; }
    public MediaCompositionStretchMode StretchMode { get; init; } = MediaCompositionStretchMode.UniformFit;
    public double Opacity { get; init; } = 1.0d;
    public uint ColorArgb { get; init; }

    /// <summary>
    /// Optional BGRA source payload for the transitional software/background path.
    /// Kept here so the layer descriptor contains enough intent for Stage 10B to
    /// move the upload into a retained Layer 0 target.
    /// </summary>
    public ReadOnlyMemory<byte> BgraPixels { get; init; }

    /// <summary>
    /// Optional producer-owned native video resource for transient hardware Layer 0 input.
    /// </summary>
    public NativeVideoFrameResource? NativeResource { get; init; }
    public double FramesPerSecond { get; init; }

    /// <summary>
    /// Optional CDG indexed/palette snapshot for the overlay layer.
    /// </summary>
    public CdgRenderSnapshot? CdgSnapshot { get; init; }

    public bool IsVisible => SourceKind != MediaCompositionLayerSourceKind.None
        && SourceKind != MediaCompositionLayerSourceKind.Clear
        && Opacity > 0d;

    /// <summary>
    /// Returns descriptor metadata suitable for retention without holding large transient
    /// pixel buffers or snapshot payloads alive after the current present call.
    /// </summary>
    public MediaCompositionLayer WithoutTransientPayload()
        => this with
        {
            BgraPixels = ReadOnlyMemory<byte>.Empty,
            CdgSnapshot = null
        };

    public override string ToString()
        => $"{Order}:{Id}/{SourceKind} rev={Revision} size={Width}x{Height} opacity={Opacity:0.###}";
}
