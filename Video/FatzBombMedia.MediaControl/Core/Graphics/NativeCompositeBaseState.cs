namespace FatzBombMedia.MediaControl.Core.Graphics;

/// <summary>
/// Retains the native compositor base visual state for still images and background video metadata.
/// The pixel buffer is only required for static/fallback composition; video-driven presentation keeps
/// the current video frame on the caller side and stores sizing/layering metadata here.
/// </summary>
internal sealed class NativeCompositeBaseState
{
    public byte[]? Bgra { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }
    public int Stride { get; private set; }
    public bool IsVideo { get; private set; }
    public bool UseCdgLayering { get; private set; }
    public bool ForceZoomToFill { get; private set; }

    public bool HasStaticCompositeContent(bool hasCdgFrame)
        => !IsVideo && (Bgra != null || hasCdgFrame);

    public bool SetCdgLayeringActive(bool active)
    {
        if (UseCdgLayering == active)
        {
            return false;
        }

        UseCdgLayering = active;
        return true;
    }

    public bool UpdateVideoMetadata(int width, int height, int stride, bool forceZoomToFill)
    {
        var sizeChanged = Width != width || Height != height;
        Width = width;
        Height = height;
        Stride = stride;
        IsVideo = true;
        ForceZoomToFill = forceZoomToFill;
        return sizeChanged;
    }

    public bool SetStaticPixels(ReadOnlySpan<byte> bgraPixels, int width, int height, int stride, int requiredLength, bool forceZoomToFill)
    {
        var sizeChanged = Width != width || Height != height;
        Width = width;
        Height = height;
        Stride = stride;
        IsVideo = false;
        ForceZoomToFill = forceZoomToFill;

        if (Bgra == null || Bgra.Length != requiredLength)
        {
            Bgra = new byte[requiredLength];
        }

        bgraPixels[..requiredLength].CopyTo(Bgra);
        return sizeChanged;
    }

    public bool SetFallbackVideoPixels(ReadOnlySpan<byte> bgraPixels, int width, int height, int stride, int requiredLength)
    {
        var sizeChanged = Width != width || Height != height;
        if (Bgra == null || Bgra.Length != requiredLength)
        {
            Bgra = new byte[requiredLength];
        }

        bgraPixels[..requiredLength].CopyTo(Bgra);
        Width = width;
        Height = height;
        Stride = stride;
        IsVideo = true;
        return sizeChanged;
    }

    public void ClearBase()
    {
        Bgra = null;
        Width = 0;
        Height = 0;
        Stride = 0;
        IsVideo = false;
        ForceZoomToFill = false;
    }

    public void ClearAll()
    {
        ClearBase();
        UseCdgLayering = false;
    }
}
