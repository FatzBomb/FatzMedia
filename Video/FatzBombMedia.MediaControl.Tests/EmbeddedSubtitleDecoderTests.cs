using FatzBombMedia.MediaControl.Core.Subtitle;
using FFmpeg.AutoGen;

namespace FatzBombMedia.MediaControl.Tests;

public class EmbeddedSubtitleDecoderTests
{
    [Fact]
    public void NormalizeTimestampSeconds_UsesPlaybackTimelineOrigin()
    {
        var timeBase = new AVRational { num = 1, den = 1000 };

        var position = EmbeddedSubtitleDecoder.NormalizeTimestampSeconds(12_500, timeBase, 10);

        Assert.Equal(2.5, position, 6);
    }

    [Fact]
    public void ResolveCueEnds_UsesNextCueWhenPacketDurationIsUnavailable()
    {
        SubtitleCue[] cues =
        [
            new SubtitleCue(2, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), "Second"),
            new SubtitleCue(1, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), "First")
        ];

        IReadOnlyList<SubtitleCue> resolved = EmbeddedSubtitleDecoder.ResolveCueEnds(cues);

        Assert.Collection(
            resolved,
            cue => Assert.Equal((TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), "First"), (cue.Start, cue.End, cue.Text)),
            cue => Assert.Equal((TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(8), "Second"), (cue.Start, cue.End, cue.Text)));
    }
}
