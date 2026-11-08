namespace FatzBomb.MediaControl.Core.Configuration;

/// <summary>
/// Visual style used for MIDI/KAR karaoke lyrics. Colors are ARGB values. The text settings
/// describe unsung lyrics; wipe settings describe lyrics whose MIDI marker has been reached.
/// </summary>
public readonly record struct KaraokeTextStyle
{
    public string FontFamily { get; init; }
    public float FontSize { get; init; }
    public uint TextArgb { get; init; }
    public uint WipeTextArgb { get; init; }
    public uint OutlineArgb { get; init; }
    public uint WipeOutlineArgb { get; init; }
    public float OutlineWidth { get; init; }
    public float WipeOutlineWidth { get; init; }
    public uint ShadowArgb { get; init; }
    public uint WipeShadowArgb { get; init; }
    public float ShadowOffsetX { get; init; }
    public float ShadowOffsetY { get; init; }
    public float LineTransitionMilliseconds { get; init; }
    public float WipeTransitionMilliseconds { get; init; }
    public float NextLineOpacity { get; init; }

    public KaraokeTextStyle()
    {
        FontFamily = "Segoe UI";
        FontSize = 72;
        TextArgb = 0xFFFFFFFF;
        WipeTextArgb = 0xFFFFFF00;
        OutlineArgb = 0xE0000000;
        WipeOutlineArgb = 0xE0000000;
        OutlineWidth = 1.75f;
        WipeOutlineWidth = 1.75f;
        ShadowArgb = 0xA0000000;
        WipeShadowArgb = 0xA0000000;
        ShadowOffsetX = 2;
        ShadowOffsetY = 2;
        LineTransitionMilliseconds = 1000;
        WipeTransitionMilliseconds = 600;
        NextLineOpacity = 0.65f;
    }

    public KaraokeTextStyle Normalize() =>
        string.IsNullOrWhiteSpace(FontFamily)
            && FontSize <= 0
            && TextArgb == 0
            && WipeTextArgb == 0
            && OutlineArgb == 0
            && WipeOutlineArgb == 0
            && ShadowArgb == 0
            && WipeShadowArgb == 0
                ? new KaraokeTextStyle()
                : (this with
                {
                    FontFamily = string.IsNullOrWhiteSpace(FontFamily) ? "Segoe UI" : FontFamily,
                    FontSize = float.IsFinite(FontSize) ? Math.Clamp(FontSize, 6, 240) : 72,
                    OutlineWidth = float.IsFinite(OutlineWidth) ? Math.Clamp(OutlineWidth, 0, 32) : 0,
                    WipeOutlineWidth = float.IsFinite(WipeOutlineWidth) ? Math.Clamp(WipeOutlineWidth, 0, 32) : 0,
                    ShadowOffsetX = float.IsFinite(ShadowOffsetX) ? Math.Clamp(ShadowOffsetX, -128, 128) : 2,
                    ShadowOffsetY = float.IsFinite(ShadowOffsetY) ? Math.Clamp(ShadowOffsetY, -128, 128) : 2,
                    LineTransitionMilliseconds = float.IsFinite(LineTransitionMilliseconds)
                    ? Math.Clamp(LineTransitionMilliseconds, 0, 4000)
                    : 1000,
                    WipeTransitionMilliseconds = float.IsFinite(WipeTransitionMilliseconds)
                    ? Math.Clamp(WipeTransitionMilliseconds, 0, 2000)
                    : 600,
                    NextLineOpacity = float.IsFinite(NextLineOpacity) ? Math.Clamp(NextLineOpacity, 0, 1) : 0.65f
                });
}
