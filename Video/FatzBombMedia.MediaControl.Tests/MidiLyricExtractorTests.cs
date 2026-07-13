using FatzBombMedia.MediaControl.Core.Audio;
using FatzBombMedia.MediaControl.Core.Subtitle;

namespace FatzBombMedia.MediaControl.Tests;

public sealed class MidiLyricExtractorTests
{
    [Theory]
    [InlineData("song.kar")]
    [InlineData("song.mid")]
    [InlineData("song.midi")]
    [InlineData("song.rmi")]
    public void IsMidiFile_RecognizesSupportedMidiExtensions(string path) => Assert.True(MidiLyricExtractor.IsMidiFile(path));

    [Fact]
    public void BuildCues_SoftKaraokeFragments_ShowCompleteCurrentAndNextLines()
    {
        MidiLyricExtractor.MidiLyricFragment[] fragments =
        [
            new(TimeSpan.Zero, "\\Twin"),
            new(TimeSpan.FromMilliseconds(300), "kle "),
            new(TimeSpan.FromMilliseconds(700), "twin"),
            new(TimeSpan.FromMilliseconds(1000), "kle "),
            new(TimeSpan.FromSeconds(2), "/Little "),
            new(TimeSpan.FromSeconds(2.4), "star"),
            new(TimeSpan.FromSeconds(4), "/How I "),
            new(TimeSpan.FromSeconds(4.5), "wonder"),
        ];

        List<SubtitleCue> cues = MidiLyricExtractor.BuildCues(fragments, TimeSpan.FromSeconds(6));

        Assert.Collection(
            cues,
            cue =>
            {
                Assert.Equal("Twinkle twinkle\nLittle star\nHow I wonder", cue.Text);
                Assert.Equal(TimeSpan.Zero, cue.Start);
                Assert.Equal(TimeSpan.FromSeconds(1), cue.End);
                KaraokeCueTiming timing = Assert.IsType<KaraokeCueTiming>(cue.KaraokeTiming);
                Assert.Equal(4, timing.Lines[0].GetHighlightedLength(TimeSpan.Zero));
                Assert.Equal(7, timing.Lines[0].GetHighlightedLength(TimeSpan.FromMilliseconds(300)));
                Assert.Equal(12, timing.Lines[0].GetHighlightedLength(TimeSpan.FromMilliseconds(700)));
                Assert.Equal(0, timing.Lines[1].GetHighlightedLength(TimeSpan.FromSeconds(1.4)));
            },
            cue =>
            {
                Assert.Equal("Twinkle twinkle\nLittle star\nHow I wonder", cue.Text);
                Assert.Equal(TimeSpan.FromSeconds(1), cue.Start);
                Assert.Equal(TimeSpan.FromSeconds(2.4), cue.End);
                KaraokeCueTiming timing = Assert.IsType<KaraokeCueTiming>(cue.KaraokeTiming);
                Assert.Equal(1, timing.ActiveLineIndex);
                Assert.Equal(0, timing.GetTransitionProgress(TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(450)));
                Assert.Equal(0.5f, timing.GetTransitionProgress(TimeSpan.FromSeconds(1.225), TimeSpan.FromMilliseconds(450)), 3);
                Assert.Equal(1, timing.GetTransitionProgress(TimeSpan.FromSeconds(1.45), TimeSpan.FromMilliseconds(450)));
            },
            cue =>
            {
                Assert.Equal("Twinkle twinkle\nLittle star\nHow I wonder", cue.Text);
                Assert.Equal(TimeSpan.FromSeconds(2.4), cue.Start);
                Assert.Equal(TimeSpan.FromSeconds(6), cue.End);
            });
    }

    [Fact]
    public void BuildCues_Tune1000Controls_PreserveWordAndParagraphStructure()
    {
        MidiLyricExtractor.MidiLyricFragment[] fragments =
        [
            new(TimeSpan.Zero, "This "),
            new(TimeSpan.FromMilliseconds(300), "is "),
            new(TimeSpan.FromMilliseconds(600), "a line"),
            new(TimeSpan.FromSeconds(2), "\r"),
            new(TimeSpan.FromSeconds(2.1), "Next line"),
            new(TimeSpan.FromSeconds(4), "\n"),
            new(TimeSpan.FromSeconds(4.1), "New paragraph"),
        ];

        List<SubtitleCue> cues = MidiLyricExtractor.BuildCues(fragments, TimeSpan.FromSeconds(7));

        Assert.Equal("This is a line\nNext line", cues[0].Text);
        Assert.Equal(TimeSpan.FromSeconds(0.6), cues[0].End);
        Assert.Equal(TimeSpan.FromSeconds(0.6), cues[1].Start);
        Assert.Equal("This is a line\nNext line", cues[1].Text);
        Assert.Equal(TimeSpan.FromSeconds(2.15), cues[2].Start);
        Assert.Equal("New paragraph", cues[2].Text);
        KaraokeCueTiming pageTiming = Assert.IsType<KaraokeCueTiming>(cues[2].KaraokeTiming);
        Assert.True(pageTiming.ClearsPage);
        Assert.Equal(2, pageTiming.OutgoingPageLines.Count);
        Assert.Equal(1, pageTiming.OutgoingActiveLineIndex);
        Assert.Equal(0.5f, pageTiming.GetPageFadeOutProgress(TimeSpan.FromSeconds(2.375)), 3);
        Assert.Equal(0.5f, pageTiming.GetPageFadeInProgress(
            TimeSpan.FromSeconds(2.825),
            TimeSpan.FromMilliseconds(450)), 3);
        Assert.Equal(1, pageTiming.GetPageFadeInProgress(
            TimeSpan.FromSeconds(3.05),
            TimeSpan.FromMilliseconds(450)));
        Assert.Equal(0, pageTiming.Lines[0].GetHighlightedLength(TimeSpan.FromSeconds(3.05)));
    }

    [Fact]
    public void BuildCues_SoltonLineMarkers_CreateCompleteLines()
    {
        MidiLyricExtractor.MidiLyricFragment[] fragments =
        [
            new(TimeSpan.Zero, "<THIS IS AN EXAMPLE"),
            new(TimeSpan.FromSeconds(3), "<THE END"),
        ];

        List<SubtitleCue> cues = MidiLyricExtractor.BuildCues(fragments, TimeSpan.FromSeconds(5));

        Assert.Equal("THIS IS AN EXAMPLE\nTHE END", cues[0].Text);
        Assert.Equal("THIS IS AN EXAMPLE\nTHE END", cues[1].Text);
        Assert.Equal(TimeSpan.FromSeconds(1.05), cues[0].End);
        Assert.Equal(TimeSpan.FromSeconds(1.05), cues[1].Start);
        KaraokeCueTiming timing = Assert.IsType<KaraokeCueTiming>(cues[1].KaraokeTiming);
        Assert.Equal(TimeSpan.FromSeconds(1.5), timing.TransitionComplete);
        Assert.Equal(TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(3) - timing.TransitionComplete);
    }

    [Fact]
    public void BuildCues_ShortLine_ClampsTransitionToCueDuration()
    {
        MidiLyricExtractor.MidiLyricFragment[] fragments =
        [
            new(TimeSpan.Zero, "/First"),
            new(TimeSpan.FromMilliseconds(200), "/Second"),
        ];

        List<SubtitleCue> cues = MidiLyricExtractor.BuildCues(fragments, TimeSpan.FromSeconds(1));
        KaraokeCueTiming timing = Assert.IsType<KaraokeCueTiming>(cues[1].KaraokeTiming);

        Assert.Equal(0.5f, timing.GetTransitionProgress(
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromMilliseconds(450)), 3);
    }

    [Fact]
    public void BuildCues_UsesFiveLineWindowAroundActiveLine()
    {
        MidiLyricExtractor.MidiLyricFragment[] fragments =
        [
            new(TimeSpan.Zero, "/Line 1"),
            new(TimeSpan.FromSeconds(3), "/Line 2"),
            new(TimeSpan.FromSeconds(6), "/Line 3"),
            new(TimeSpan.FromSeconds(9), "/Line 4"),
            new(TimeSpan.FromSeconds(12), "/Line 5"),
            new(TimeSpan.FromSeconds(15), "/Line 6"),
        ];

        List<SubtitleCue> cues = MidiLyricExtractor.BuildCues(fragments, TimeSpan.FromSeconds(18));
        KaraokeCueTiming timing = Assert.IsType<KaraokeCueTiming>(cues[2].KaraokeTiming);

        Assert.Equal(5, timing.Lines.Count);
        Assert.Equal(2, timing.ActiveLineIndex);
        Assert.Equal(TimeSpan.FromSeconds(4.05), cues[2].Start);
        Assert.Equal("Line 1\nLine 2\nLine 3\nLine 4\nLine 5", cues[2].Text);
    }

    [Fact]
    public void BuildCues_ZeroGapLines_DoNotOverlap()
    {
        MidiLyricExtractor.MidiLyricFragment[] fragments =
        [
            new(TimeSpan.Zero, "/First"),
            new(TimeSpan.Zero, "/Second"),
        ];

        List<SubtitleCue> cues = MidiLyricExtractor.BuildCues(fragments, TimeSpan.FromSeconds(1));

        Assert.Equal(cues[0].End, cues[1].Start);
        Assert.True(cues[0].Start < cues[0].End);
    }

    [Fact]
    public void BuildCues_PageWithLongGap_HoldsAndPreviewsAtPreferredTimes()
    {
        MidiLyricExtractor.MidiLyricFragment[] fragments =
        [
            new(TimeSpan.Zero, "Last lyric"),
            new(TimeSpan.FromSeconds(8), "\nNew page"),
        ];

        List<SubtitleCue> cues = MidiLyricExtractor.BuildCues(fragments, TimeSpan.FromSeconds(12));
        KaraokeCueTiming timing = Assert.IsType<KaraokeCueTiming>(cues[1].KaraokeTiming);

        Assert.True(timing.ClearsPage);
        Assert.Equal(TimeSpan.FromSeconds(2), timing.PageFadeOutStart);
        Assert.Equal(TimeSpan.FromSeconds(2.45), timing.PageFadeOutEnd);
        Assert.Equal(TimeSpan.FromSeconds(5.55), timing.PageFadeInStart);
        Assert.Equal(TimeSpan.FromSeconds(6), timing.PageFadeInEnd);
        Assert.Equal(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(8) - timing.PageFadeInEnd);
    }

    [Fact]
    public void BuildCues_FirstPageFadeCompletesThreeSecondsBeforeFirstWipe()
    {
        MidiLyricExtractor.MidiLyricFragment[] fragments =
        [
            new(TimeSpan.FromSeconds(5), "/First lyric"),
            new(TimeSpan.FromSeconds(10), "/Second lyric"),
        ];

        List<SubtitleCue> cues = MidiLyricExtractor.BuildCues(fragments, TimeSpan.FromSeconds(12));
        KaraokeCueTiming timing = Assert.IsType<KaraokeCueTiming>(cues[0].KaraokeTiming);

        Assert.Equal(TimeSpan.FromSeconds(1.55), cues[0].Start);
        Assert.Equal(TimeSpan.FromSeconds(2), timing.TransitionComplete);
        Assert.Equal(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(5) - timing.TransitionComplete);
        Assert.Equal(1, timing.GetTransitionProgress(
            TimeSpan.FromSeconds(2),
            TimeSpan.FromMilliseconds(450)));
        Assert.Equal(0, timing.Lines[0].GetHighlightedLength(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void BuildCues_ShortInitialLeadPrioritizesEarliestCompletedFade()
    {
        MidiLyricExtractor.MidiLyricFragment[] fragments =
        [
            new(TimeSpan.FromSeconds(2), "/First lyric"),
        ];

        SubtitleCue cue = Assert.Single(MidiLyricExtractor.BuildCues(fragments, TimeSpan.FromSeconds(4)));
        KaraokeCueTiming timing = Assert.IsType<KaraokeCueTiming>(cue.KaraokeTiming);

        Assert.Equal(TimeSpan.Zero, cue.Start);
        Assert.Equal(TimeSpan.FromMilliseconds(450), timing.TransitionComplete);
        Assert.Equal(TimeSpan.FromSeconds(1.55), TimeSpan.FromSeconds(2) - timing.TransitionComplete);
        Assert.Equal(0, timing.Lines[0].GetHighlightedLength(timing.TransitionComplete));
    }

    [Fact]
    public void BuildCues_FirstWipeAtZero_ShowsImmediatelyWithoutFade()
    {
        MidiLyricExtractor.MidiLyricFragment[] fragments =
        [
            new(TimeSpan.Zero, "/Instant lyric"),
        ];

        SubtitleCue cue = Assert.Single(MidiLyricExtractor.BuildCues(fragments, TimeSpan.FromSeconds(2)));
        KaraokeCueTiming timing = Assert.IsType<KaraokeCueTiming>(cue.KaraokeTiming);

        Assert.Equal(TimeSpan.Zero, cue.Start);
        Assert.Equal(TimeSpan.Zero, timing.TransitionComplete);
        Assert.Equal(1, timing.GetTransitionProgress(
            TimeSpan.Zero,
            TimeSpan.FromMilliseconds(450)));
    }
}
