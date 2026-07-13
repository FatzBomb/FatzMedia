using FatzBombMedia.MediaControl.Core.Subtitle;

namespace FatzBombMedia.MediaControl.Tests;

public class SrtParserTests
{
    [Fact]
    public void Parse_ParsesSingleCueWithBom()
    {
        IReadOnlyList<SubtitleCue> cues = SrtParser.Parse("\uFEFF1\r\n00:00:01,250 --> 00:00:03,500\r\nHello\r\n");

        SubtitleCue cue = Assert.Single(cues);
        Assert.Equal(1, cue.Sequence);
        Assert.Equal(TimeSpan.FromMilliseconds(1250), cue.Start);
        Assert.Equal(TimeSpan.FromMilliseconds(3500), cue.End);
        Assert.Equal("Hello", cue.Text);
    }

    [Fact]
    public void Parse_AssignsDistinctFallbackSequencesAfterSkippedCue()
    {
        IReadOnlyList<SubtitleCue> cues = SrtParser.Parse("\uFEFF00:00:01,000 --> 00:00:02,000\nFirst\n\ninvalid\nIgnored\n\n00:00:03,000 --> 00:00:04,000\nSecond\n");

        Assert.Collection(
            cues,
            cue => Assert.Equal(1, cue.Sequence),
            cue => Assert.Equal(3, cue.Sequence));
    }

    [Fact]
    public void Parse_PreservesMultilineAndOverlappingCues()
    {
        IReadOnlyList<SubtitleCue> cues = SrtParser.Parse("1\n00:00:01.000 --> 00:00:04.000\nFirst line\nSecond line\n\n2\n00:00:03,000 --> 00:00:05,000\nOverlap\n");

        Assert.Collection(
            cues,
            cue =>
            {
                Assert.Equal("First line\nSecond line", cue.Text);
                Assert.Equal(TimeSpan.FromSeconds(1), cue.Start);
            },
            cue => Assert.Equal("Overlap", cue.Text));
    }

    [Fact]
    public void Parse_SkipsMalformedOrEmptyCues()
    {
        IReadOnlyList<SubtitleCue> cues = SrtParser.Parse("1\nnot a timestamp\nIgnored\n\n2\n00:00:03,000 --> 00:00:03,000\nZero duration\n\n3\n00:00:04,000 --> 00:00:05,000\nKept\n");

        SubtitleCue cue = Assert.Single(cues);
        Assert.Equal(3, cue.Sequence);
        Assert.Equal("Kept", cue.Text);
    }
}
