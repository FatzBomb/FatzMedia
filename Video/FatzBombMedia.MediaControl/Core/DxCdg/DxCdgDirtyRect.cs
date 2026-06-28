namespace FatzBombMedia.MediaControl.Core.DxCdg;

/// <summary>
/// Dirty rectangle in CDG source-pixel coordinates.
/// DXCDG renderers can use these rectangles to update only changed tile/scroll regions.
/// </summary>
public readonly record struct DxCdgDirtyRect(int X, int Y, int Width, int Height)
{
    public static DxCdgDirtyRect Empty { get; } = new(0, 0, 0, 0);

    public static DxCdgDirtyRect FullFrame { get; } = new(
        0,
        0,
        DxCdgConstants.FrameWidth,
        DxCdgConstants.FrameHeight);

    public bool IsEmpty => Width <= 0 || Height <= 0;

    public DxCdgDirtyRect ClampToFrame()
    {
        if (IsEmpty)
        {
            return Empty;
        }

        var x = Math.Clamp(X, 0, DxCdgConstants.FrameWidth);
        var y = Math.Clamp(Y, 0, DxCdgConstants.FrameHeight);
        var right = Math.Clamp(X + Width, 0, DxCdgConstants.FrameWidth);
        var bottom = Math.Clamp(Y + Height, 0, DxCdgConstants.FrameHeight);
        return right <= x || bottom <= y ? Empty : new DxCdgDirtyRect(x, y, right - x, bottom - y);
    }
}
