namespace FatzMedia.Video.Core.Graphics.Geometry;

/// <summary>
/// Small renderer-neutral rectangle used for viewport/aspect calculations.
/// Keep this free of DirectX/WPF presentation types so the same geometry
/// math can be reused by D3D, WPF fallback, background renderers, and future overlays.
/// </summary>
public readonly record struct RectD(double X, double Y, double Width, double Height)
{
    public double Left => X;
    public double Top => Y;
    public double Right => X + Width;
    public double Bottom => Y + Height;
}
