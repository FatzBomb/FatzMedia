using FatzMedia.Video.Core.Audio;
using FatzMedia.Video.Core.Subtitle;

namespace FatzMedia.Video.Tests;

public sealed class MidiLyricCueBuilderTests
{
    [Theory]
    [InlineData("song.kar")]
    [InlineData("song.mid")]
    [InlineData("song.midi")]
    [InlineData("song.rmi")]
    public void IsMidiFile_RecognizesSupportedMidiExtensions(string path) => Assert.True(MidiLyricCueBuilder.IsMidiFile(path));

    [Fact]
    public void BuildCues_SoftKaraokeFragments_ShowCompleteCurrentAndNextLines()
    {
        MidiLyricCueBuilder.MidiLyricFragment[] fragments =
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

        List<SubtitleCue> cues = MidiLyricCueBuilder.BuildCues(fragments, TimeSpan.FromSeconds(6));

        Assert.Collection(
            cues,
            cue =>
            {
                Assert.Equal("Twinkle twinkle\nLittle star\nHow I wonder", cue.Text);
                Assert.Equal(TimeSpan.Zero, cue.Start);
                Assert.Equal(TimeSpan.FromSeconds(0.75), cue.End);
                KaraokeCueTiming timing = Assert.IsType<KaraokeCueTiming>(cue.KaraokeTiming);
                Assert.Equal(4, timing.Lines[0].GetHighlightedLength(TimeSpan.Zero));
                Assert.Equal(7, timing.Lines[0].GetHighlightedLength(TimeSpan.FromMilliseconds(300)));
                Assert.Equal(12, timing.Lines[0].GetHighlightedLength(TimeSpan.FromMilliseconds(700)));
                Assert.Equal(0, timing.Lines[1].GetHighlightedLength(TimeSpan.FromMilliseconds(900)));
            },
            cue =>
            {
                Assert.Equal("Twinkle twinkle\nLittle star\nHow I wonder", cue.Text);
                Assert.Equal(TimeSpan.FromSeconds(0.75), cue.Start);
                Assert.Equal(TimeSpan.FromSeconds(2.5), cue.End);
                KaraokeCueTiming timing = Assert.IsType<KaraokeCueTiming>(cue.KaraokeTiming);
                Assert.Equal(1, timing.ActiveLineIndex);
                TimeSpan midpoint = timing.TransitionStart
                    + TimeSpan.FromTicks((timing.TransitionComplete - timing.TransitionStart).Ticks / 2);
                Assert.Equal(0.5f, timing.GetTransitionProgress(midpoint), 3);
            },
            cue =>
            {
                Assert.Equal("Twinkle twinkle\nLittle star\nHow I wonder", cue.Text);
                Assert.Equal(TimeSpan.FromSeconds(2.5), cue.Start);
                Assert.Equal(TimeSpan.FromSeconds(6), cue.End);
            });
    }

    [Fact]
    public void BuildCues_Tune1000Controls_PreserveWordAndParagraphStructure()
    {
        MidiLyricCueBuilder.MidiLyricFragment[] fragments =
        [
            new(TimeSpan.Zero, "This "),
            new(TimeSpan.FromMilliseconds(300), "is "),
            new(TimeSpan.FromMilliseconds(600), "a line"),
            new(TimeSpan.FromSeconds(2), "\r"),
            new(TimeSpan.FromSeconds(2.1), "Next line"),
            new(TimeSpan.FromSeconds(4), "\n"),
            new(TimeSpan.FromSeconds(4.1), "New paragraph"),
        ];

        List<SubtitleCue> cues = MidiLyricCueBuilder.BuildCues(fragments, TimeSpan.FromSeconds(7));

        Assert.Equal("This is a line\nNext line", cues[0].Text);
        Assert.Equal(TimeSpan.FromSeconds(0.6), cues[0].End);
        Assert.Equal(TimeSpan.FromSeconds(0.6), cues[1].Start);
        Assert.Equal("This is a line\nNext line", cues[1].Text);
        Assert.Equal(TimeSpan.FromSeconds(2.1), cues[2].Start);
        Assert.Equal("New paragraph", cues[2].Text);
        KaraokeCueTiming pageTiming = Assert.IsType<KaraokeCueTiming>(cues[2].KaraokeTiming);
        Assert.True(pageTiming.ClearsPage);
        Assert.Equal(2, pageTiming.OutgoingPageLines.Count);
        Assert.Equal(1, pageTiming.OutgoingActiveLineIndex);
        Assert.Equal(TimeSpan.FromSeconds(3.1), pageTiming.PageFadeOutEnd);
        Assert.Equal(pageTiming.PageFadeOutEnd, pageTiming.PageFadeInStart);
        Assert.Equal(1, pageTiming.GetPageFadeOutProgress(TimeSpan.FromSeconds(3.1)));
        TimeSpan fadeInStart = pageTiming.PageFadeInStart;
        TimeSpan fadeInEnd = pageTiming.PageFadeInEnd;
        TimeSpan fadeInMid = fadeInStart + TimeSpan.FromTicks((fadeInEnd - fadeInStart).Ticks / 2);
        Assert.Equal(0.5f, pageTiming.GetPageFadeInProgress(
            fadeInMid,
            TimeSpan.FromMilliseconds(450)), 2);
        Assert.Equal(1, pageTiming.GetPageFadeInProgress(
            fadeInEnd,
            TimeSpan.FromMilliseconds(450)));
    }

    [Fact]
    public void BuildCues_SoltonLineMarkers_CreateCompleteLines()
    {
        MidiLyricCueBuilder.MidiLyricFragment[] fragments =
        [
            new(TimeSpan.Zero, "<THIS IS AN EXAMPLE"),
            new(TimeSpan.FromSeconds(3), "<THE END"),
        ];

        List<SubtitleCue> cues = MidiLyricCueBuilder.BuildCues(fragments, TimeSpan.FromSeconds(5));

        Assert.Equal("THIS IS AN EXAMPLE\nTHE END", cues[0].Text);
        Assert.Equal("THIS IS AN EXAMPLE\nTHE END", cues[1].Text);
        Assert.Equal(TimeSpan.FromSeconds(1.5), cues[0].End);
        Assert.Equal(TimeSpan.FromSeconds(1.5), cues[1].Start);
        KaraokeCueTiming timing = Assert.IsType<KaraokeCueTiming>(cues[1].KaraokeTiming);
        Assert.Equal(TimeSpan.FromSeconds(3), timing.TransitionComplete);
        Assert.Equal(TimeSpan.Zero, TimeSpan.FromSeconds(3) - timing.TransitionComplete);
    }

    [Fact]
    public void BuildCues_ShortLine_ClampsTransitionToCueDuration()
    {
        MidiLyricCueBuilder.MidiLyricFragment[] fragments =
        [
            new(TimeSpan.Zero, "/First"),
            new(TimeSpan.FromMilliseconds(200), "/Second"),
        ];

        List<SubtitleCue> cues = MidiLyricCueBuilder.BuildCues(fragments, TimeSpan.FromSeconds(3));
        KaraokeCueTiming timing = Assert.IsType<KaraokeCueTiming>(cues[1].KaraokeTiming);

        TimeSpan midpoint = timing.TransitionStart + TimeSpan.FromTicks((timing.TransitionComplete - timing.TransitionStart).Ticks / 2);
        Assert.True((timing.TransitionComplete - timing.TransitionStart - TimeSpan.FromSeconds(1.5)).Duration() < TimeSpan.FromMilliseconds(1));
        Assert.Equal(0.5f, timing.GetTransitionProgress(midpoint), 3);
    }

    [Fact]
    public void BuildCues_UsesFiveLineWindowAroundActiveLine()
    {
        MidiLyricCueBuilder.MidiLyricFragment[] fragments =
        [
            new(TimeSpan.Zero, "/Line 1"),
            new(TimeSpan.FromSeconds(3), "/Line 2"),
            new(TimeSpan.FromSeconds(6), "/Line 3"),
            new(TimeSpan.FromSeconds(9), "/Line 4"),
            new(TimeSpan.FromSeconds(12), "/Line 5"),
            new(TimeSpan.FromSeconds(15), "/Line 6"),
        ];

        List<SubtitleCue> cues = MidiLyricCueBuilder.BuildCues(fragments, TimeSpan.FromSeconds(18));
        KaraokeCueTiming timing = Assert.IsType<KaraokeCueTiming>(cues[2].KaraokeTiming);

        Assert.Equal(5, timing.Lines.Count);
        Assert.Equal(2, timing.ActiveLineIndex);
        Assert.Equal(TimeSpan.FromSeconds(4.5), cues[2].Start);
        Assert.Equal("Line 1\nLine 2\nLine 3\nLine 4\nLine 5", cues[2].Text);
    }

    [Fact]
    public void BuildCues_ZeroGapLines_DoNotOverlap()
    {
        MidiLyricCueBuilder.MidiLyricFragment[] fragments =
        [
            new(TimeSpan.Zero, "/First"),
            new(TimeSpan.Zero, "/Second"),
        ];

        List<SubtitleCue> cues = MidiLyricCueBuilder.BuildCues(fragments, TimeSpan.FromSeconds(1));

        Assert.Equal(cues[0].End, cues[1].Start);
        Assert.True(cues[0].Start < cues[0].End);
    }

    [Fact]
    public void BuildCues_PageWithLongGap_HoldsAndPreviewsAtPreferredTimes()
    {
        MidiLyricCueBuilder.MidiLyricFragment[] fragments =
        [
            new(TimeSpan.Zero, "Last lyric"),
            new(TimeSpan.FromSeconds(8), "\nNew page"),
        ];

        List<SubtitleCue> cues = MidiLyricCueBuilder.BuildCues(fragments, TimeSpan.FromSeconds(12));
        KaraokeCueTiming timing = Assert.IsType<KaraokeCueTiming>(cues[1].KaraokeTiming);

        Assert.True(timing.ClearsPage);
        Assert.Equal(TimeSpan.FromSeconds(1), timing.PageFadeOutStart);
        Assert.Equal(TimeSpan.FromSeconds(2), timing.PageFadeOutEnd);
        Assert.Equal(TimeSpan.FromSeconds(2), timing.PageFadeInStart);
        Assert.Equal(TimeSpan.FromSeconds(3), timing.PageFadeInEnd);
        Assert.Equal(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(8) - timing.PageFadeInEnd);
    }

    [Fact]
    public void BuildCues_FirstPageFadeCompletesBeforeFirstWipe()
    {
        MidiLyricCueBuilder.MidiLyricFragment[] fragments =
        [
            new(TimeSpan.FromSeconds(5), "/First lyric"),
            new(TimeSpan.FromSeconds(10), "/Second lyric"),
        ];

        List<SubtitleCue> cues = MidiLyricCueBuilder.BuildCues(fragments, TimeSpan.FromSeconds(12));
        KaraokeCueTiming timing = Assert.IsType<KaraokeCueTiming>(cues[0].KaraokeTiming);

        Assert.Equal(TimeSpan.Zero, cues[0].Start);
        Assert.Equal(TimeSpan.FromSeconds(3), timing.TransitionStart);
        Assert.Equal(TimeSpan.FromSeconds(4), timing.TransitionComplete);
        Assert.Equal(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5) - timing.TransitionComplete);
        Assert.Equal(1, timing.GetTransitionProgress(
            TimeSpan.FromSeconds(4)));
        Assert.Equal(0, timing.Lines[0].GetHighlightedLength(TimeSpan.FromSeconds(4)));
    }

    [Fact]
    public void BuildCues_ShortInitialLeadPrioritizesEarliestCompletedFade()
    {
        MidiLyricCueBuilder.MidiLyricFragment[] fragments =
        [
            new(TimeSpan.FromSeconds(2), "/First lyric"),
        ];

        SubtitleCue cue = Assert.Single(MidiLyricCueBuilder.BuildCues(fragments, TimeSpan.FromSeconds(4)));
        KaraokeCueTiming timing = Assert.IsType<KaraokeCueTiming>(cue.KaraokeTiming);

        Assert.Equal(TimeSpan.Zero, cue.Start);
        Assert.Equal(TimeSpan.FromSeconds(1), timing.TransitionComplete);
        Assert.Equal(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2) - timing.TransitionComplete);
        Assert.Equal(0, timing.Lines[0].GetHighlightedLength(timing.TransitionComplete));
    }

    [Fact]
    public void BuildCues_FirstWipeAtZero_ShowsImmediatelyWithoutFade()
    {
        MidiLyricCueBuilder.MidiLyricFragment[] fragments =
        [
            new(TimeSpan.Zero, "/Instant lyric"),
        ];

        SubtitleCue cue = Assert.Single(MidiLyricCueBuilder.BuildCues(fragments, TimeSpan.FromSeconds(2)));
        KaraokeCueTiming timing = Assert.IsType<KaraokeCueTiming>(cue.KaraokeTiming);

        Assert.Equal(TimeSpan.Zero, cue.Start);
        Assert.Equal(TimeSpan.Zero, timing.TransitionComplete);
        Assert.Equal(1, timing.GetTransitionProgress(
            TimeSpan.Zero));
    }

    [Fact]
    public void BuildCues_LongGap_MarksCountdownWithoutChangingLyricTextOrTiming()
    {
        MidiLyricCueBuilder.MidiLyricFragment[] fragments =
        [
            new(TimeSpan.Zero, "/First"),
            new(TimeSpan.FromSeconds(8), "/Second"),
        ];

        List<SubtitleCue> cues = MidiLyricCueBuilder.BuildCues(fragments, TimeSpan.FromSeconds(10));
        KaraokeCueTiming timing = Assert.IsType<KaraokeCueTiming>(cues[0].KaraokeTiming);
        KaraokeLineTiming countdownLine = Assert.Single(timing.Lines, static line => line.Text == "Second");

        Assert.True(countdownLine.HasCountdown);
        KaraokeCountdownState initial = countdownLine.GetCountdownState(TimeSpan.FromSeconds(3));
        Assert.True(initial.IsVisible);
        Assert.Equal(0, initial.CompletedArrowCount);
        Assert.Equal(0, initial.ActiveArrowProgress);

        KaraokeCountdownState firstWipe = countdownLine.GetCountdownState(TimeSpan.FromSeconds(3.5));
        Assert.Equal(0, firstWipe.CompletedArrowCount);
        Assert.Equal(0.5f, firstWipe.ActiveArrowProgress, 3);
        Assert.Equal(1, countdownLine.GetCountdownState(TimeSpan.FromSeconds(4)).CompletedArrowCount);
        Assert.Equal(2, countdownLine.GetCountdownState(TimeSpan.FromSeconds(5)).CompletedArrowCount);
        Assert.Equal(3, countdownLine.GetCountdownState(TimeSpan.FromSeconds(6)).CompletedArrowCount);

        KaraokeCountdownState fading = countdownLine.GetCountdownState(TimeSpan.FromSeconds(7.5));
        Assert.Equal(4, fading.CompletedArrowCount);
        Assert.Equal(0.5f, fading.Opacity, 3);
        Assert.False(countdownLine.GetCountdownState(TimeSpan.FromSeconds(8)).IsVisible);
        Assert.Equal(0, countdownLine.GetHighlightedLength(TimeSpan.FromSeconds(7.9)));
        Assert.Equal("First\nSecond", cues[0].Text);
        KaraokeCueTiming scrollTiming = Assert.IsType<KaraokeCueTiming>(cues[1].KaraokeTiming);
        Assert.Equal(TimeSpan.FromSeconds(1.5), scrollTiming.TransitionComplete - scrollTiming.TransitionStart);
        TimeSpan countdownStart = countdownLine.FirstLyricStart - KaraokeLineTiming.CountdownDuration;
        Assert.Equal(TimeSpan.Zero, countdownStart - scrollTiming.TransitionComplete);
    }

    [Theory]
    [InlineData(3.0, 1.5, 0.0)]
    [InlineData(1.5, 1.5, 0.0)]
    [InlineData(0.6, 1.5, -0.9)]
    public void BuildCues_ScrollPreservesFifteenHundredMillisecondsAcrossTightGaps(
        double nextLyricSeconds,
        double expectedScrollSeconds,
        double expectedCompletionLeadSeconds)
    {
        MidiLyricCueBuilder.MidiLyricFragment[] fragments =
        [
            new(TimeSpan.Zero, "/First"),
            new(TimeSpan.FromSeconds(nextLyricSeconds), "/Second"),
        ];

        List<SubtitleCue> cues = MidiLyricCueBuilder.BuildCues(
            fragments,
            TimeSpan.FromSeconds(nextLyricSeconds + 2));
        KaraokeCueTiming timing = Assert.IsType<KaraokeCueTiming>(cues[1].KaraokeTiming);

        Assert.True((timing.TransitionComplete - timing.TransitionStart - TimeSpan.FromSeconds(expectedScrollSeconds)).Duration() < TimeSpan.FromMilliseconds(1));
        Assert.True((TimeSpan.FromSeconds(nextLyricSeconds) - timing.TransitionComplete - TimeSpan.FromSeconds(expectedCompletionLeadSeconds)).Duration() < TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public void BuildCues_TightPageGapCrossfadesFullDuration()
    {
        MidiLyricCueBuilder.MidiLyricFragment[] fragments =
        [
            new(TimeSpan.Zero, "First page"),
            new(TimeSpan.FromSeconds(1.5), "\nSecond page"),
        ];

        List<SubtitleCue> cues = MidiLyricCueBuilder.BuildCues(fragments, TimeSpan.FromSeconds(4));
        KaraokeCueTiming timing = Assert.IsType<KaraokeCueTiming>(cues[1].KaraokeTiming);

        Assert.True(timing.PageFadeOutStart < TimeSpan.FromMilliseconds(1));
        Assert.Equal(TimeSpan.FromSeconds(1), timing.PageFadeOutEnd);
        Assert.Equal(TimeSpan.FromSeconds(0.5), timing.PageFadeInStart);
        Assert.Equal(TimeSpan.FromSeconds(1.5), timing.PageFadeInEnd);
        TimeSpan fadeOutQuarter = timing.PageFadeOutStart
            + TimeSpan.FromTicks((timing.PageFadeOutEnd - timing.PageFadeOutStart).Ticks / 4);
        TimeSpan fadeInQuarter = timing.PageFadeInStart
            + TimeSpan.FromTicks((timing.PageFadeInEnd - timing.PageFadeInStart).Ticks / 4);
        Assert.Equal(0.015625f, timing.GetPageFadeOutProgress(fadeOutQuarter), 5);
        Assert.Equal(0.0625f, timing.GetPageFadeInProgress(fadeInQuarter, TimeSpan.FromSeconds(1)), 5);
        Assert.Equal(1, timing.GetPageFadeOutProgress(timing.PageFadeOutEnd));
        Assert.Equal(1, timing.GetPageFadeInProgress(timing.PageFadeInEnd, TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void BuildCues_SubsecondPageGapCrossfadesAcrossTheAvailableGap()
    {
        MidiLyricCueBuilder.MidiLyricFragment[] fragments =
        [
            new(TimeSpan.Zero, "First page"),
            new(TimeSpan.FromMilliseconds(600), "\nSecond page"),
        ];

        List<SubtitleCue> cues = MidiLyricCueBuilder.BuildCues(fragments, TimeSpan.FromSeconds(3));
        KaraokeCueTiming timing = Assert.IsType<KaraokeCueTiming>(cues[1].KaraokeTiming);

        Assert.True(timing.PageFadeOutStart < TimeSpan.FromMilliseconds(1));
        Assert.Equal(TimeSpan.FromMilliseconds(600), timing.PageFadeOutEnd);
        Assert.Equal(TimeSpan.Zero, timing.PageFadeInStart);
        Assert.Equal(TimeSpan.FromMilliseconds(600), timing.PageFadeInEnd);
    }

    [Fact]
    public void BuildCues_ZeroTransitionDurationDisablesPageFades()
    {
        MidiLyricCueBuilder.MidiLyricFragment[] fragments =
        [
            new(TimeSpan.Zero, "First page"),
            new(TimeSpan.FromSeconds(3), "\nSecond page"),
        ];

        List<SubtitleCue> cues = MidiLyricCueBuilder.BuildCues(
            fragments,
            TimeSpan.FromSeconds(5),
            TimeSpan.Zero);
        KaraokeCueTiming timing = Assert.IsType<KaraokeCueTiming>(cues[1].KaraokeTiming);

        Assert.Equal(timing.PageFadeOutStart, timing.PageFadeOutEnd);
        Assert.Equal(timing.PageFadeInStart, timing.PageFadeInEnd);
    }

    [Fact]
    public void SyllableWipe_UsesConfiguredDuration()
    {
        var line = new KaraokeLineTiming(
            "Word",
            [new KaraokeTextSegment(0, 4, TimeSpan.Zero)]);

        Assert.Equal(0.5f, line.GetSyllableWipeFraction(
            TimeSpan.FromMilliseconds(300),
            TimeSpan.FromMilliseconds(600)), 3);
        Assert.Equal(0.25f, line.GetSyllableWipeFraction(
            TimeSpan.FromMilliseconds(300),
            TimeSpan.FromMilliseconds(1200)), 3);
    }

    [Fact]
    public void ProgressRevision_ChangesAtTerminalFadeFrame()
    {
        var line = new KaraokeLineTiming(
            "Last",
            [new KaraokeTextSegment(0, 4, TimeSpan.Zero)]);
        var timing = new KaraokeCueTiming([line], 0, TimeSpan.Zero, TimeSpan.FromSeconds(2))
        {
            EndLyricFadeStart = TimeSpan.Zero,
            EndLyricFadeEnd = TimeSpan.FromSeconds(1),
        };

        var almostComplete = timing.GetProgressRevision(
            TimeSpan.FromMilliseconds(999),
            TimeSpan.FromMilliseconds(600));
        var complete = timing.GetProgressRevision(
            TimeSpan.FromSeconds(1),
            TimeSpan.FromMilliseconds(600));

        Assert.NotEqual(almostComplete, complete);
        Assert.True(timing.GetEndLyricOpacity(TimeSpan.FromMilliseconds(999)) > 0);
        Assert.Equal(0, timing.GetEndLyricOpacity(TimeSpan.FromSeconds(1)));
    }
}
