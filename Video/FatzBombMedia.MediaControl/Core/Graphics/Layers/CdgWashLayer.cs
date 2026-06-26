namespace FatzBombMedia.MediaControl.Core.Graphics.Layers;

/// <summary>
/// Retains the full-viewport CDG background wash state.
/// Layer 2 is intentionally separate from the CDG lyric texture: color/opacity
/// changes update this tiny cached value, while lyric packet changes update Layer 3.
/// </summary>
internal sealed class CdgWashLayer
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
