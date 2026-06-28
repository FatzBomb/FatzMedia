using FatzBombMedia.MediaControl.Core.Graphics.D3D.Diagnostics;

namespace FatzBombMedia.MediaControl.Core.Graphics.D3D.Upload;

/// <summary>
/// Owns reusable CPU upload resources and upload diagnostics for presentation frame paths.
/// Keeping both together prevents the coordinator renderer from having to carry upload-cache
/// lifetime and presentation-size diagnostic plumbing.
/// </summary>
internal sealed class D3D11FrameUploadResources(
    D3D11PresentationDiagnostics diagnostics,
    Func<int> getPresentationWidth,
    Func<int> getPresentationHeight) : IDisposable
{
    public D3D11TextureUploadCache TextureCache { get; } = new();

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

    public void RecordUploadWithCurrentTarget(double uploadMs, int sourceWidth, int sourceHeight) => diagnostics.RecordUpload(
            uploadMs,
            sourceWidth,
            sourceHeight,
            getPresentationWidth(),
            getPresentationHeight());

    public void ReleaseCache() => TextureCache.Dispose();

    public void Dispose() => ReleaseCache();
}
