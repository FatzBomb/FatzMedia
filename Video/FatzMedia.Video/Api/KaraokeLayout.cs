#pragma warning disable IDE0130
namespace FatzMedia.Video;

/// <summary>
/// Where an adapter's <c>KaraokeSurface</c> places its inner media surface (shared by the WPF,
/// Avalonia and WinForms controls): the largest 4:3 rectangle that fits the control, centred, the
/// rest showing the canvas colour.
/// </summary>
public static class KaraokeLayout
{
    /// <summary>
    /// Gets the 4:3 rectangle centred in a <paramref name="width"/> x <paramref name="height"/>
    /// area (in the adapter's units). Never negative.
    /// </summary>
    public static (double X, double Y, double Width, double Height) Fit(double width, double height)
    {
        var fitWidth = width;
        var fitHeight = width * 0.75;
        if (fitHeight > height)
        {
            fitHeight = height;
            fitWidth = height * (4.0 / 3.0);
        }

        return (
            Math.Max(0, (width - fitWidth) / 2),
            Math.Max(0, (height - fitHeight) / 2),
            Math.Max(0, fitWidth),
            Math.Max(0, fitHeight));
    }
}
