namespace FatzMedia.Video.Core.Subtitle;

public readonly record struct SubtitleRasterResult(
    ReadOnlyMemory<byte> Pixels,
    int Width,
    int Height,
    int Stride,
    int CanvasLeft,
    int CanvasTop,
    int CanvasWidth,
    int CanvasHeight);
