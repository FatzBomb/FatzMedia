using FatzMedia.Video.Core.Configuration;

namespace FatzMedia.Video.Tests;

public class SubtitleTextStyleTests
{
    [Fact]
    public void Normalize_UsesDefaultsForAnUninitializedStyle()
    {
        var style = default(SubtitleTextStyle).Normalize();

        Assert.Equal("Segoe UI", style.FontFamily);
        Assert.Equal(44, style.FontSize);
        Assert.Equal(0xFFFFFFFFu, style.TextArgb);
        Assert.Equal(0xC0000000u, style.OutlineArgb);
        Assert.Equal(1.25f, style.OutlineWidth);
        Assert.Equal(0x80000000u, style.ShadowArgb);
    }

    [Fact]
    public void Normalize_ClampsInvalidDimensionsWhilePreservingConfiguredColors()
    {
        var style = new SubtitleTextStyle
        {
            FontFamily = "",
            FontSize = 1000,
            TextArgb = 0xFF00FF00,
            OutlineWidth = -1,
            ShadowOffsetX = float.NaN,
            ShadowOffsetY = 999
        }.Normalize();

        Assert.Equal("Segoe UI", style.FontFamily);
        Assert.Equal(240, style.FontSize);
        Assert.Equal(0, style.OutlineWidth);
        Assert.Equal(2, style.ShadowOffsetX);
        Assert.Equal(128, style.ShadowOffsetY);
        Assert.Equal(0xFF00FF00u, style.TextArgb);
    }
}
