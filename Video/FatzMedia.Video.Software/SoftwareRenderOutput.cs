namespace FatzMedia.Video.Software;

/// <summary>
/// Result of one software composition attempt. Outputs of the public
/// <see cref="SoftwareCompositionRenderer.Render(Core.Graphics.Composition.MediaCompositionFrame, int, int)"/>
/// own their pixels; the backend's internal reusable-buffer overload returns pixels that the
/// next render into the same buffer overwrites.
/// </summary>
public sealed class SoftwareRenderOutput
{
    private readonly byte[] _pixels;

    internal SoftwareRenderOutput(
        SoftwareRenderStatus status,
        byte[] pixels,
        int width,
        int height,
        int stride,
        string? message)
    {
        Status = status;
        _pixels = pixels;
        Width = width;
        Height = height;
        Stride = stride;
        Message = message;
    }

    public SoftwareRenderStatus Status { get; }
    public bool IsRendered => Status == SoftwareRenderStatus.Rendered;
    public ReadOnlyMemory<byte> Pixels => _pixels;
    public int Width { get; }
    public int Height { get; }
    public int Stride { get; }
    public string? Message { get; }

    internal static SoftwareRenderOutput Failure(
        SoftwareRenderStatus status,
        int width,
        int height,
        string message) => new(status, [], width, height, 0, message);
}
