using FatzBombMedia.MediaControl.Core.Subtitle;

namespace FatzBombMedia.MediaControl.Tests;

public class AssParserTests
{
    [Fact]
    public void Parse_ReadsDialogueTextAndStripsOverrideTags()
    {
        const string ass = "[Events]\nDialogue: 0,0:00:01.25,0:00:03.50,Default,,0,0,0,,{\\an8}First\\NSecond\n";

        SubtitleCue cue = Assert.Single(AssParser.Parse(ass));

        Assert.Equal(TimeSpan.FromMilliseconds(1250), cue.Start);
        Assert.Equal(TimeSpan.FromMilliseconds(3500), cue.End);
        Assert.Equal("First\nSecond", cue.Text);
    }

    [Fact]
    public void Parse_SkipsMalformedOrEmptyDialogue()
    {
        const string ass = "Dialogue: 0,broken,0:00:03.00,Default,,0,0,0,,Ignored\nDialogue: 0,0:00:04.00,0:00:04.00,Default,,0,0,0,,Ignored\n";

        Assert.Empty(AssParser.Parse(ass));
    }

    [Fact]
    public void NormalizeDialogueText_StripsEmbeddedAssFormatting() => Assert.Equal("First\nSecond", AssParser.NormalizeDialogueText("{\\an8}First\\NSecond"));
}
