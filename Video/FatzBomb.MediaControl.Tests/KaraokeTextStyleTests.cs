using FatzBomb.MediaControl.Core.Configuration;

namespace FatzBomb.MediaControl.Tests;

public sealed class KaraokeTextStyleTests
{
    [Fact]
    public void Normalize_UsesIndependentKaraokeDefaults()
    {
        KaraokeTextStyle style = default(KaraokeTextStyle).Normalize();

        Assert.Equal("Segoe UI", style.FontFamily);
        Assert.Equal(72, style.FontSize);
        Assert.Equal(0xFFFFFFFFu, style.TextArgb);
        Assert.Equal(0xFFFFFF00u, style.WipeTextArgb);
        Assert.Equal(0xE0000000u, style.OutlineArgb);
        Assert.Equal(0xE0000000u, style.WipeOutlineArgb);
        Assert.Equal(1.75f, style.OutlineWidth);
        Assert.Equal(1.75f, style.WipeOutlineWidth);
        Assert.Equal(1000, style.LineTransitionMilliseconds);
        Assert.Equal(600, style.WipeTransitionMilliseconds);
        Assert.Equal(0.65f, style.NextLineOpacity);
    }

    [Fact]
    public void Normalize_ClampsDimensionsAndPreservesKaraokeColors()
    {
        KaraokeTextStyle style = new KaraokeTextStyle
        {
            FontFamily = "",
            FontSize = 1000,
            TextArgb = 0xFF101010,
            WipeTextArgb = 0xFFFF00FF,
            OutlineWidth = -2,
            WipeOutlineWidth = 100,
            ShadowOffsetX = float.NaN,
            ShadowOffsetY = -999,
            LineTransitionMilliseconds = 5000,
            WipeTransitionMilliseconds = 5000,
            NextLineOpacity = -1,
        }.Normalize();

        Assert.Equal("Segoe UI", style.FontFamily);
        Assert.Equal(240, style.FontSize);
        Assert.Equal(0, style.OutlineWidth);
        Assert.Equal(32, style.WipeOutlineWidth);
        Assert.Equal(2, style.ShadowOffsetX);
        Assert.Equal(-128, style.ShadowOffsetY);
        Assert.Equal(4000, style.LineTransitionMilliseconds);
        Assert.Equal(2000, style.WipeTransitionMilliseconds);
        Assert.Equal(0, style.NextLineOpacity);
        Assert.Equal(0xFF101010u, style.TextArgb);
        Assert.Equal(0xFFFF00FFu, style.WipeTextArgb);
    }
}
