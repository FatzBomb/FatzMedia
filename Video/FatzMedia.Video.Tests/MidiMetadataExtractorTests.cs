using FatzMedia.Video.Core.Audio;

namespace FatzMedia.Video.Tests;

public sealed class MidiMetadataExtractorTests
{
    [Fact]
    public void ParseMarkerMetadata_UsesSoftKaraokeTitleFallbacks()
    {
        var metadata = MidiMetadataExtractor.ParseMarkerMetadata(
            ["@TMy Song", "@TThe Artist", "@T(c) 2026 Some Label"]);

        Assert.Equal("My Song", metadata.Title);
        Assert.Equal("The Artist", metadata.Artist);
        Assert.Equal("(c) 2026 Some Label", metadata.Copyright);
        Assert.Empty(metadata.AdditionalInfo);
    }

    [Fact]
    public void ParseMarkerMetadata_ParsesLabeledInformation()
    {
        var metadata = MidiMetadataExtractor.ParseMarkerMetadata(
        [
            "@I title: My Song",
            "@I artist: The Artist",
            "@I written by: J. Writer",
            "@I lyricist: L. Lyricist",
            "@I year: 2026",
            "@I copyright: Label",
            "@I key: 2 0",
            "@I tempo: 120",
            "@I timing: 4/4"
        ]);

        Assert.Equal("My Song", metadata.Title);
        Assert.Equal("The Artist", metadata.Artist);
        Assert.Equal("J. Writer", metadata.Writer);
        Assert.Equal("L. Lyricist", metadata.Lyricist);
        Assert.Equal("2026", metadata.Year);
        Assert.Equal("Label", metadata.Copyright);
        Assert.Equal("2 0", metadata.KeySignature);
        Assert.Equal("120 BPM", metadata.Tempo);
        Assert.Equal("4/4", metadata.TimeSignature);
    }

    [Fact]
    public void ParseMarkerMetadata_DeduplicatesAdditionalInformation()
    {
        var metadata = MidiMetadataExtractor.ParseMarkerMetadata(
            ["@IRandom note", "@irandom note", "@I2025", "plain lyric", "@T"]);

        Assert.Equal("2025", metadata.Year);
        Assert.Equal(["Random note"], metadata.AdditionalInfo);
    }

    [Fact]
    public void ParseMarkerMetadata_UsesThirdTitleAsAdditionalInformationWhenNotCopyright()
    {
        var metadata = MidiMetadataExtractor.ParseMarkerMetadata(
            ["@TFirst", "@TSecond", "@TThird"]);

        Assert.Equal("First", metadata.Title);
        Assert.Equal("Second", metadata.Artist);
        Assert.Equal(["Third"], metadata.AdditionalInfo);
    }
}
