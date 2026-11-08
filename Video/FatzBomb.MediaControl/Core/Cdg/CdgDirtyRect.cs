namespace FatzBomb.MediaControl.Core.Cdg;

/// <summary>
/// Dirty rectangle in CDG source-pixel coordinates.
/// CDG renderers can use these rectangles to update only changed tile/scroll regions.
/// </summary>
public readonly record struct CdgDirtyRect(int X, int Y, int Width, int Height)
{
    public static CdgDirtyRect Empty { get; } = new(0, 0, 0, 0);

    public static CdgDirtyRect FullFrame { get; } = new(
        0,
        0,
        CdgConstants.FrameWidth,
        CdgConstants.FrameHeight);

    public bool IsEmpty => Width <= 0 || Height <= 0;

    public CdgDirtyRect ClampToFrame()
    {
        if (IsEmpty)
        {
            return Empty;
        }

        var x = Math.Clamp(X, 0, CdgConstants.FrameWidth);
        var y = Math.Clamp(Y, 0, CdgConstants.FrameHeight);
        var right = Math.Clamp(X + Width, 0, CdgConstants.FrameWidth);
        var bottom = Math.Clamp(Y + Height, 0, CdgConstants.FrameHeight);
        return right <= x || bottom <= y ? Empty : new CdgDirtyRect(x, y, right - x, bottom - y);
    }
}
