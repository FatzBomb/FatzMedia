namespace FatzBomb.MediaControl.Rendering.Software;

/// <summary>Immutable result of one software composition attempt.</summary>
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
