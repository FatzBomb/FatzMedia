using FatzBomb.MediaControl.Core.Audio;
using FatzBomb.MediaControl.Core.Subtitle;

namespace FatzBomb.MediaControl.Tests;

public sealed class KaraokeMetadataOverlayTests
{
    [Theory]
    [InlineData("0 0", "C")]
    [InlineData("2 0", "D")]
    [InlineData("-3 1", "Cm")]
    [InlineData("7 1", "A#m")]
    public void FormatKeySignature_ConvertsMidiAccidentals(string value, string expected) =>
        Assert.Equal(expected, MidiMetadataExtractor.FormatKeySignature(value));

    [Fact]
    public void IntroMetadata_LateLyrics_UsesCenteredCardTimeline()
    {
        KaraokeCueTiming timing = CreateTiming(CreateMetadata(), firstPresentationSeconds: 12, durationSeconds: 20);

        Assert.False(timing.UseSplitMetadataBands);
        Assert.Equal(0, timing.GetIntroMetadataOpacity(TimeSpan.FromMilliseconds(500)));
        Assert.Equal(0.5f, timing.GetIntroMetadataOpacity(TimeSpan.FromMilliseconds(1500)), 3);
        Assert.Equal(1, timing.GetIntroMetadataOpacity(TimeSpan.FromSeconds(4)));
        Assert.Equal(0.5f, timing.GetIntroMetadataOpacity(TimeSpan.FromMilliseconds(8500)), 3);
        Assert.Equal(0, timing.GetIntroMetadataOpacity(TimeSpan.FromSeconds(9)));
    }

    [Fact]
    public void IntroMetadata_EarlyLyrics_UsesBandsThroughEightSeconds()
    {
        KaraokeCueTiming timing = CreateTiming(CreateMetadata(), firstPresentationSeconds: 5, durationSeconds: 20);

        Assert.True(timing.UseSplitMetadataBands);
        Assert.Equal(1, timing.GetIntroMetadataOpacity(TimeSpan.FromSeconds(3)));
        Assert.Equal(0.5f, timing.GetIntroMetadataOpacity(TimeSpan.FromMilliseconds(7500)), 3);
        Assert.Equal(0, timing.GetIntroMetadataOpacity(TimeSpan.FromSeconds(8)));
    }

    [Fact]
    public void EndMetadata_RequiresTimeAndEmbeddedMetadata()
    {
        KaraokeCueTiming timing = CreateTiming(CreateMetadata(), firstPresentationSeconds: 12, durationSeconds: 15) with
        {
            EndLyricFadeEnd = TimeSpan.FromSeconds(10),
        };

        Assert.Equal(0, timing.GetEndMetadataOpacity(TimeSpan.FromSeconds(10)));
        Assert.Equal(0.5f, timing.GetEndMetadataOpacity(TimeSpan.FromMilliseconds(10375)), 3);
        Assert.Equal(1, timing.GetEndMetadataOpacity(TimeSpan.FromSeconds(12)));
        Assert.Equal(0.5f, timing.GetEndMetadataOpacity(TimeSpan.FromMilliseconds(14625)), 3);
        Assert.Equal(0, timing.GetEndMetadataOpacity(TimeSpan.FromSeconds(15)));

        KaraokeCueTiming fallback = timing with { Metadata = CreateMetadata(isFallback: true) };
        Assert.Equal(0, fallback.GetEndMetadataOpacity(TimeSpan.FromSeconds(12)));
        KaraokeCueTiming tooShort = timing with { MediaDuration = TimeSpan.FromSeconds(12) };
        Assert.Equal(0, tooShort.GetEndMetadataOpacity(TimeSpan.FromSeconds(11)));
    }

    private static KaraokeCueTiming CreateTiming(
        KaraokeMetadata metadata,
        double firstPresentationSeconds,
        double durationSeconds)
    {
        var line = new KaraokeLineTiming(
            "Line",
            [new KaraokeTextSegment(0, 4, TimeSpan.FromSeconds(firstPresentationSeconds + 5))]);
        return new KaraokeCueTiming([line], 0, TimeSpan.Zero, TimeSpan.FromSeconds(durationSeconds))
        {
            Metadata = metadata,
            FirstPresentationStart = TimeSpan.FromSeconds(firstPresentationSeconds),
            FirstLyricStart = TimeSpan.FromSeconds(firstPresentationSeconds + 5),
            MediaDuration = TimeSpan.FromSeconds(durationSeconds),
        };
    }

    private static KaraokeMetadata CreateMetadata(bool isFallback = false) => new(
        "Title",
        "Artist",
        "Writer",
        "Lyricist",
        "2026",
        "Copyright",
        "C",
        "120 BPM",
        "4/4",
        TimeSpan.FromMinutes(4),
        isFallback,
        []);
}
