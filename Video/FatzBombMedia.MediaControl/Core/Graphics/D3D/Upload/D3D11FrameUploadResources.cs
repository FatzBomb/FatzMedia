using FatzBombMedia.MediaControl.Core.Graphics.D3D.Diagnostics;

namespace FatzBombMedia.MediaControl.Core.Graphics.D3D.Upload;

/// <summary>
/// Owns reusable CPU upload resources and upload diagnostics for presentation frame paths.
/// Keeping both together prevents the coordinator renderer from having to carry upload-cache
/// lifetime and presentation-size diagnostic plumbing.
/// </summary>
internal sealed class D3D11FrameUploadResources : IDisposable
{
    private readonly D3D11PresentationDiagnostics _diagnostics;
    private readonly Func<int> _getPresentationWidth;
    private readonly Func<int> _getPresentationHeight;

    public D3D11FrameUploadResources(
        D3D11PresentationDiagnostics diagnostics,
        Func<int> getPresentationWidth,
        Func<int> getPresentationHeight)
    {
        _diagnostics = diagnostics;
        _getPresentationWidth = getPresentationWidth;
        _getPresentationHeight = getPresentationHeight;
    }

    public D3D11TextureUploadCache TextureCache { get; } = new();

    public void RecordUpload(
        long startTimestamp,
        int sourceWidth,
        int sourceHeight,
        int presentationWidth,
        int presentationHeight)
    {
        _diagnostics.RecordUpload(
            D3D11PresentationDiagnostics.ElapsedMilliseconds(startTimestamp),
            sourceWidth,
            sourceHeight,
            presentationWidth,
            presentationHeight);
    }

    public void RecordUploadWithCurrentTarget(double uploadMs, int sourceWidth, int sourceHeight)
    {
        _diagnostics.RecordUpload(
            uploadMs,
            sourceWidth,
            sourceHeight,
            _getPresentationWidth(),
            _getPresentationHeight());
    }

    public void ReleaseCache()
    {
        TextureCache.Dispose();
    }

    public void Dispose()
    {
        ReleaseCache();
    }
}
