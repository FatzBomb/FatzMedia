#pragma warning disable IDE0130
namespace FatzMedia.Video;

/// <summary>
/// Window placement shared by the adapters' second-screen windows (WPF, Avalonia, WinForms).
/// </summary>
public static class PresentationPlacement
{
    /// <summary>
    /// Gets the top-left position that centres a <paramref name="width"/> x <paramref name="height"/>
    /// window in an area (all in pixels). A window larger than the area is clamped to it, so the
    /// position never leaves the area.
    /// </summary>
    public static (int X, int Y) Center(int areaX, int areaY, int areaWidth, int areaHeight, int width, int height)
    {
        width = Math.Min(areaWidth, width);
        height = Math.Min(areaHeight, height);
        return (areaX + ((areaWidth - width) / 2), areaY + ((areaHeight - height) / 2));
    }
}
