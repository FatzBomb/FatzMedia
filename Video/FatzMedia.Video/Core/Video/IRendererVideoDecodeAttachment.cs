using FatzMedia.Video.Core.Graphics.Composition;

namespace FatzMedia.Video.Core.Video;

/// <summary>
/// A media engine whose decoders can decode on the presentation renderer's device (Stage 045A
/// slice 2). The presentation coordinator attaches its renderer through this seam instead of
/// knowing the engine's concrete type.
/// </summary>
internal interface IRendererVideoDecodeAttachment
{
    /// <summary>
    /// Makes every decoder of this engine decode on <paramref name="device"/> when the renderer
    /// offers one; null returns to the platform's hardware devices. Decoders opened afterwards
    /// follow it.
    /// </summary>
    void AttachRendererVideoDecodeDevice(IMediaRendererVideoDecodeDevice? device);

    /// <summary>Detaches <paramref name="device"/> if it is still the attached renderer device.</summary>
    void DetachRendererVideoDecodeDevice(IMediaRendererVideoDecodeDevice device);
}
