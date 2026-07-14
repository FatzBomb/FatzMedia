namespace FatzBomb.MediaControl.Core.Configuration;

/// <summary>
/// Visual style used when Core rasterizes text subtitles. Colors are ARGB values.
/// </summary>
public readonly record struct SubtitleTextStyle
{
    public string FontFamily { get; init; }
    public float FontSize { get; init; }
    public uint TextArgb { get; init; }
    public uint OutlineArgb { get; init; }
    public float OutlineWidth { get; init; }
    public uint ShadowArgb { get; init; }
    public float ShadowOffsetX { get; init; }
    public float ShadowOffsetY { get; init; }

    public SubtitleTextStyle()
    {
        FontFamily = "Segoe UI";
        FontSize = 44;
        TextArgb = 0xFFFFFFFF;
        OutlineArgb = 0xC0000000;
        OutlineWidth = 1.25f;
        ShadowArgb = 0x80000000;
        ShadowOffsetX = 2;
        ShadowOffsetY = 2;
    }

    internal SubtitleTextStyle Normalize() => string.IsNullOrWhiteSpace(FontFamily)
            && FontSize <= 0
            && TextArgb == 0
            && OutlineArgb == 0
            && ShadowArgb == 0
                ? new SubtitleTextStyle()
                : (this with
                {
                    FontFamily = string.IsNullOrWhiteSpace(FontFamily) ? "Segoe UI" : FontFamily,
                    FontSize = float.IsFinite(FontSize) ? Math.Clamp(FontSize, 6, 240) : 44,
                    OutlineWidth = float.IsFinite(OutlineWidth) ? Math.Clamp(OutlineWidth, 0, 32) : 0,
                    ShadowOffsetX = float.IsFinite(ShadowOffsetX) ? Math.Clamp(ShadowOffsetX, -128, 128) : 2,
                    ShadowOffsetY = float.IsFinite(ShadowOffsetY) ? Math.Clamp(ShadowOffsetY, -128, 128) : 2
                });
}
