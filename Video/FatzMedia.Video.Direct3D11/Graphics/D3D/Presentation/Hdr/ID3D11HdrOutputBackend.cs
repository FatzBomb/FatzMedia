using FatzMedia.Video.Core.Video.Hdr;

namespace FatzMedia.Video.Core.Graphics.D3D.Presentation.Hdr;

/// <summary>
/// A presentation backend whose swap chain can switch between SDR and FP16 scRGB output (Stage
/// 042A slice 2): the native HWND swap chain and the DirectComposition base swap chain. Offscreen
/// backends (managed frame sink, shared textures) do not implement it and stay SDR.
/// </summary>
interface ID3D11HdrOutputBackend
{
    /// <summary>The window whose monitor decides the output.</summary>
    IntPtr Hwnd { get; }

    /// <summary>What the target texture holds now, read from its format: scRGB when it is FP16.</summary>
    HdrOutputMode OutputMode { get; }

    /// <summary>
    /// Why this swap chain cannot present scRGB (a failed or unsupported change), or null. Kept
    /// until the swap chain is created again (a new surface, device-loss recovery).
    /// </summary>
    string? HdrOutputFailure { get; }

    /// <summary>
    /// Switches the swap chain to <paramref name="mode"/> at its current size (releasing every
    /// back-buffer reference first, as a resize does) and sets <paramref name="metadata"/> for
    /// scRGB. A no-op when the mode and metadata are unchanged. False, with
    /// <see cref="HdrOutputFailure"/> set, when the change failed; the target is then whatever the
    /// swap chain holds, which <see cref="OutputMode"/> reports.
    /// </summary>
    bool TrySetOutputMode(HdrOutputMode mode, Hdr10MetadataValues? metadata);
}
