using FatzBombMedia.MediaControl.Core.Graphics.D3D.Diagnostics;

namespace FatzBombMedia.MediaControl.Core.Graphics.D3D.Upload;

/// <summary>
/// Owns upload diagnostics for presentation frame paths.
/// </summary>
internal sealed class D3D11FrameUploadResources(
    D3D11PresentationDiagnostics diagnostics)
{
    public void RecordUpload(
        long startTimestamp,
        int sourceWidth,
        int sourceHeight,
        int presentationWidth,
        int presentationHeight) => diagnostics.RecordUpload(
            D3D11PresentationDiagnostics.ElapsedMilliseconds(startTimestamp),
            sourceWidth,
            sourceHeight,
            presentationWidth,
            presentationHeight);
}
