namespace FatzBombMedia.MediaControl.Core.Graphics.Layers;

/// <summary>
/// Retains the full-viewport color wash state for overlay sessions.
/// Layer 2 is intentionally separate from the overlay graphics texture: color/opacity
/// changes update this tiny cached value, while foreground overlay changes update Layer 3.
/// CDG currently supplies this wash from its background color plus user opacity.
/// </summary>
internal sealed class WashLayer
{
    public uint WashArgb { get; private set; }

    public bool IsVisible => ((WashArgb >> 24) & 0xFF) != 0;

    public bool SetWash(uint washArgb)
    {
        if (WashArgb == washArgb)
        {
            return false;
        }

        WashArgb = washArgb;
        return true;
    }

    public void Clear() => WashArgb = 0;
}
