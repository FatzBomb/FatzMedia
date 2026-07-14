using FatzBomb.MediaControl.Core.Subtitle;
using ManagedBass.Midi;
using System.Text;
using MBass = ManagedBass.Bass;

namespace FatzBomb.MediaControl.Core.Audio;

/// <summary>
/// Extracts karaoke lyrics from an open BASS MIDI channel using BASS MIDI marker APIs.
/// Lyric markers (MIDI meta event 5) and text markers (MIDI meta event 1) are converted
/// to <see cref="SubtitleCue"/> objects with timing derived from the BASS channel position.
/// </summary>
internal static class MidiLyricExtractor
{
    private const int PreferredLineLength = 42;

    private static readonly TimeSpan TargetPageHold = TimeSpan.FromMilliseconds(2000);
    private static readonly TimeSpan TargetPagePreview = TimeSpan.FromMilliseconds(1000);
    private static readonly TimeSpan TargetPageTransitionDuration = TimeSpan.FromMilliseconds(1000);
    private static readonly TimeSpan TargetScrollDuration = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan LyricLeadOffset = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan ParagraphGap = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SentenceGap = TimeSpan.FromSeconds(0.9);
    private static readonly HashSet<string> MidiExtensions = new(StringComparer.OrdinalIgnoreCase)
    { ".mid", ".midi", ".kar", ".rmi" };

    public static bool IsMidiFile(string path) =>
        MidiExtensions.Contains(Path.GetExtension(path));

    /// <summary>
    /// Extracts lyric cues from an open BASS MIDI channel (created via Bass.MusicLoad).
    /// Returns null if the channel has no lyrics or is not a MIDI stream.
    /// </summary>
    public static IReadOnlyList<SubtitleCue>? ExtractLyrics(
        int channelHandle,
        string sourcePath,
        TimeSpan? transitionDuration = null)
    {
        if (channelHandle == 0)
            return null;

        MidiMarker[] lyrics = GetMarksSafe(channelHandle, MidiMarkerType.Lyric);
        MidiMarker[] textEvents = GetMarksSafe(channelHandle, MidiMarkerType.Text);

        var durationBytes = MBass.ChannelGetLength(channelHandle);
        var durationSeconds = MBass.ChannelBytes2Seconds(channelHandle, durationBytes);
        TimeSpan duration = double.IsFinite(durationSeconds) && durationSeconds > 0
            ? TimeSpan.FromSeconds(durationSeconds)
            : TimeSpan.Zero;
        IReadOnlyList<MidiMarker> metadataMarkers =
            [.. lyrics.Concat(textEvents).OrderBy(static marker => marker.Position)];
        KaraokeMetadata metadata = MidiMetadataExtractor.Extract(channelHandle, sourcePath, duration, metadataMarkers);
        List<SubtitleCue> cues = BuildCuesFromMarkers(
            channelHandle,
            lyrics,
            textEvents,
            duration,
            transitionDuration ?? TargetPageTransitionDuration);
        if (cues.Count > 0)
        {
            for (var i = 0; i < cues.Count; i++)
            {
                if (cues[i].KaraokeTiming is { } timing)
                {
                    cues[i] = cues[i] with { KaraokeTiming = timing with { Metadata = metadata } };
                }
            }
        }
        return cues.Count > 0 ? cues : null;
    }

    private static MidiMarker[] GetMarksSafe(int channelHandle, MidiMarkerType type)
    {
        try
        {
            return BassMidi.StreamGetMarks(channelHandle, -1, type) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static List<SubtitleCue> BuildCuesFromMarkers(
        int stream,
        MidiMarker[] lyrics,
        MidiMarker[] textEvents,
        TimeSpan duration,
        TimeSpan transitionDuration)
    {
        MidiMarker[] lyricMarkers = FilterKaraokeEvents(lyrics);
        MidiMarker[] markers = lyricMarkers.Length > 0 ? lyricMarkers : FilterKaraokeEvents(textEvents);

        if (markers.Length == 0)
            return [];

        var fragments = new List<MidiLyricFragment>(markers.Length);

        foreach (MidiMarker marker in markers)
        {
            var startSeconds = MBass.ChannelBytes2Seconds(stream, marker.Position);
            if (!double.IsFinite(startSeconds) || startSeconds < 0)
                continue;

            // Shift lyric markers 400ms earlier so the wipe highlights ahead of the note,
            // giving the singer preparation time like CDG files typically do.
            TimeSpan adjustedStart = TimeSpan.FromSeconds(startSeconds) - LyricLeadOffset;
            if (adjustedStart < TimeSpan.Zero)
                adjustedStart = TimeSpan.Zero;

            fragments.Add(new MidiLyricFragment(adjustedStart, marker.Text));
        }

        if (fragments.Count == 0)
            return [];

        if (duration <= TimeSpan.Zero)
            duration = fragments[^1].Start + TimeSpan.FromSeconds(3);
        return BuildCues(fragments, duration, transitionDuration);
    }

    internal static List<SubtitleCue> BuildCues(
        IReadOnlyList<MidiLyricFragment> fragments,
        TimeSpan duration,
        TimeSpan? transitionDuration = null)
    {
        TimeSpan animationDuration = transitionDuration is { } configuredDuration
            ? Max(TimeSpan.Zero, configuredDuration)
            : TargetPageTransitionDuration;
        List<KaraokeLine> lines = BuildLines(fragments);
        var cues = new List<SubtitleCue>(lines.Count);
        if (lines.Count == 0)
            return cues;

        MarkCountdownLines(lines);

        var schedules = new TransitionSchedule[lines.Count];
        schedules[0] = CalculateInitialTransition(lines[0], animationDuration);
        for (var i = 1; i < lines.Count; i++)
        {
            var clearsPage = lines[i - 1].Paragraph != lines[i].Paragraph;
            TransitionSchedule schedule = clearsPage
                ? CalculatePageTransition(lines[i - 1], lines[i], animationDuration)
                : CalculateScrollTransition(lines[i - 1], lines[i]);
            TimeSpan minimumStart = schedules[i - 1].Start + TimeSpan.FromTicks(1);
            schedules[i] = schedule.Start >= minimumStart
                ? schedule
                : schedule.MoveStart(minimumStart);
        }

        TimeSpan firstLyricStart = GetFirstSyllableStart(lines[0]);
        TimeSpan firstPresentationStart = lines[0].HasCountdown
            ? Max(TimeSpan.Zero, firstLyricStart - KaraokeLineTiming.CountdownDuration)
            : firstLyricStart;

        // Fade five seconds after the final syllable, or during the final second when the media ends sooner.
        TimeSpan lastSyllableEnd = GetLastSyllableStart(lines[^1]);
        TimeSpan endFadeStart = lastSyllableEnd + TimeSpan.FromSeconds(5);
        TimeSpan endFadeEnd = endFadeStart + TimeSpan.FromMilliseconds(1000);
        if (endFadeEnd > duration)
        {
            endFadeEnd = duration;
            endFadeStart = Max(lastSyllableEnd, endFadeEnd - TimeSpan.FromMilliseconds(1000));
        }

        for (var i = 0; i < lines.Count; i++)
        {
            KaraokeLine line = lines[i];
            TransitionSchedule schedule = schedules[i];
            TimeSpan cueStart = i == 0 ? TimeSpan.Zero : schedule.Start;
            TimeSpan end = i + 1 < lines.Count ? schedules[i + 1].Start : duration;
            if (i == lines.Count - 1 && end <= cueStart)
                end = cueStart + TimeSpan.FromMilliseconds(100);

            List<KaraokeLineTiming> timingLines = CreateLineWindow(lines, i, line.Paragraph, out var activeLineIndex);
            var clearsPage = i > 0 && lines[i - 1].Paragraph != line.Paragraph;
            IReadOnlyList<KaraokeLineTiming> outgoingPageLines = [];
            var outgoingActiveLineIndex = 0;
            if (clearsPage)
            {
                outgoingPageLines = CreateOutgoingPageWindow(
                    lines,
                    i - 1,
                    lines[i - 1].Paragraph,
                    out outgoingActiveLineIndex);
            }

            var text = string.Join('\n', timingLines.Select(static timing => timing.Text));

            cues.Add(new SubtitleCue(i, cueStart, end, text)
            {
                KaraokeTiming = new KaraokeCueTiming(timingLines, activeLineIndex, schedule.Start, end)
                {
                    ClearsPage = clearsPage,
                    IncomingStart = GetFirstSyllableStart(line),
                    TransitionComplete = Min(schedule.Complete, end),
                    PageFadeOutStart = schedule.FadeOutStart,
                    PageFadeOutEnd = schedule.FadeOutEnd,
                    PageFadeInStart = schedule.FadeInStart,
                    PageFadeInEnd = schedule.FadeInEnd,
                    OutgoingPageLines = outgoingPageLines,
                    OutgoingActiveLineIndex = outgoingActiveLineIndex,
                    FirstLyricStart = firstLyricStart,
                    FirstPresentationStart = firstPresentationStart,
                    MediaDuration = duration,
                    EndLyricFadeStart = endFadeStart,
                    EndLyricFadeEnd = endFadeEnd,
                },
            });
        }

        return cues;
    }

    private static TransitionSchedule CalculateScrollTransition(
        KaraokeLine currentLine,
        KaraokeLine nextLine)
    {
        TimeSpan nextLineStart = GetFirstSyllableStart(nextLine);
        TimeSpan nextPresentationStart = nextLine.HasCountdown
            ? Max(TimeSpan.Zero, nextLineStart - KaraokeLineTiming.CountdownDuration)
            : nextLineStart;

        TimeSpan currentLineEnd = GetLastSyllableStart(currentLine);
        TimeSpan earliestScrollStart = GetFirstSyllableStart(currentLine);
        TimeSpan scrollStart;
        TimeSpan scrollComplete;
        TimeSpan gap = nextPresentationStart - currentLineEnd;
        if (gap >= TargetScrollDuration)
        {
            scrollComplete = nextPresentationStart;
            scrollStart = scrollComplete - TargetScrollDuration;
        }
        else
        {
            TimeSpan gapCenter = currentLineEnd + TimeSpan.FromTicks(gap.Ticks / 2);
            scrollStart = gapCenter - TimeSpan.FromTicks(TargetScrollDuration.Ticks / 2);
            scrollComplete = scrollStart + TargetScrollDuration;
            if (scrollStart < earliestScrollStart)
            {
                TimeSpan shift = earliestScrollStart - scrollStart;
                scrollStart += shift;
                scrollComplete += shift;
            }
        }

        return TransitionSchedule.Normal(scrollStart, scrollComplete);
    }

    private static TransitionSchedule CalculateInitialTransition(
        KaraokeLine firstLine,
        TimeSpan transitionDuration)
    {
        TimeSpan firstLineStart = GetFirstSyllableStart(firstLine);
        TimeSpan preview = firstLine.HasCountdown ? KaraokeLineTiming.CountdownDuration : TargetPagePreview;
        TimeSpan fadeInEnd = Max(TimeSpan.Zero, firstLineStart - preview);
        TimeSpan fadeInStart = Max(TimeSpan.Zero, fadeInEnd - transitionDuration);
        return TransitionSchedule.Normal(fadeInStart, fadeInEnd);
    }

    private static TransitionSchedule CalculatePageTransition(
        KaraokeLine currentLine,
        KaraokeLine nextLine,
        TimeSpan transitionDuration)
    {
        TimeSpan currentPageEnd = GetLastSyllableStart(currentLine);
        TimeSpan nextPageStart = GetFirstSyllableStart(nextLine);
        TimeSpan totalGap = nextPageStart - currentPageEnd;

        // Handle zero/negative gap
        if (totalGap <= TimeSpan.Zero)
            return TransitionSchedule.Page(
                currentPageEnd, nextPageStart,
                currentPageEnd, currentPageEnd,
                nextPageStart, nextPageStart);

        TimeSpan presentationLead;
        if (nextLine.HasCountdown)
        {
            presentationLead = Min(KaraokeLineTiming.CountdownDuration, totalGap);
        }
        else
        {
            TimeSpan roomAfterFullFades = Max(TimeSpan.Zero, totalGap - (transitionDuration * 2));
            presentationLead = Min(TargetPagePreview, roomAfterFullFades);
        }

        TimeSpan incomingReady = nextPageStart - presentationLead;
        TimeSpan transitionWindow = Max(TimeSpan.Zero, incomingReady - currentPageEnd);
        TimeSpan fadeDuration = Min(transitionDuration, transitionWindow);
        TimeSpan outHold = TimeSpan.Zero;
        if (transitionWindow >= transitionDuration * 2)
        {
            outHold = Min(TargetPageHold, transitionWindow - (fadeDuration * 2));
        }

        TimeSpan outStart = currentPageEnd + outHold;
        TimeSpan outFadeEnd = outStart + fadeDuration;
        TimeSpan inFadeEnd = incomingReady;
        TimeSpan inFadeStart = inFadeEnd - fadeDuration;

        return TransitionSchedule.Page(
            outStart, inFadeEnd,
            outStart, outFadeEnd,
            inFadeStart, inFadeEnd);
    }

    private static TimeSpan GetLastSyllableStart(KaraokeLine line) => line.Segments.Count > 0
        ? line.Segments[^1].Start
        : line.Start;

    private static TimeSpan GetFirstSyllableStart(KaraokeLine line) => line.Segments.Count > 0
        ? line.Segments[0].Start
        : line.Start;

    private static TimeSpan Min(TimeSpan left, TimeSpan right) => left <= right ? left : right;
    private static TimeSpan Max(TimeSpan left, TimeSpan right) => left >= right ? left : right;

    private static List<KaraokeLineTiming> CreateLineWindow(
        IReadOnlyList<KaraokeLine> lines,
        int activeIndex,
        int paragraph,
        out int activeLineIndex)
    {
        var first = activeIndex;
        while (first > 0 && first > activeIndex - 2 && lines[first - 1].Paragraph == paragraph)
            first--;

        var last = activeIndex;
        while (last + 1 < lines.Count && last < activeIndex + 2 && lines[last + 1].Paragraph == paragraph)
            last++;

        var result = new List<KaraokeLineTiming>(last - first + 1);
        for (var i = first; i <= last; i++)
            result.Add(new KaraokeLineTiming(lines[i].Text, lines[i].Segments, lines[i].HasCountdown));

        activeLineIndex = activeIndex - first;
        return result;
    }

    private static List<KaraokeLineTiming> CreateOutgoingPageWindow(
        IReadOnlyList<KaraokeLine> lines,
        int activeIndex,
        int paragraph,
        out int activeLineIndex)
    {
        var first = activeIndex;
        while (first > 0 && first > activeIndex - 2 && lines[first - 1].Paragraph == paragraph)
            first--;

        var result = new List<KaraokeLineTiming>(activeIndex - first + 1);
        for (var i = first; i <= activeIndex; i++)
            result.Add(new KaraokeLineTiming(lines[i].Text, lines[i].Segments, lines[i].HasCountdown));

        activeLineIndex = result.Count - 1;
        return result;
    }

    private static List<KaraokeLine> BuildLines(IReadOnlyList<MidiLyricFragment> fragments)
    {
        var lines = new List<KaraokeLine>();
        var lineText = new StringBuilder();
        var lineParts = new List<KaraokePart>();
        TimeSpan? lineStart = null;
        TimeSpan? previousStart = null;
        var paragraph = 0;

        foreach (MidiLyricFragment fragment in fragments.OrderBy(static fragment => fragment.Start))
        {
            if (string.IsNullOrEmpty(fragment.Text))
                continue;

            TimeSpan gap = previousStart is { } previous ? fragment.Start - previous : TimeSpan.Zero;
            var startsWithDisplayControl = fragment.Text[0] is '\\' or '/' or '\r' or '\n' or '<';
            if (!startsWithDisplayControl && lineText.Length > 0 && (gap >= ParagraphGap ||
                (gap >= SentenceGap && EndsSentence(lineText))))
            {
                FlushLine(lines, lineText, lineParts, ref lineStart, paragraph);
                if (gap >= ParagraphGap)
                    paragraph++;
            }
            else if (lineText.Length >= PreferredLineLength && char.IsWhiteSpace(lineText[^1]))
            {
                FlushLine(lines, lineText, lineParts, ref lineStart, paragraph);
            }

            var partText = new StringBuilder();
            foreach (var character in fragment.Text)
            {
                switch (character)
                {
                    case '\\': // Soft Karaoke: clear the current screen.
                    case '\n': // Tune1000: end the paragraph and clear the screen.
                        AddPart(lineText, lineParts, partText, fragment.Start, ref lineStart);
                        FlushLine(lines, lineText, lineParts, ref lineStart, paragraph);
                        paragraph++;
                        lineStart = fragment.Start;
                        break;
                    case '/': // Soft Karaoke: advance to the next line.
                    case '\r': // Tune1000: advance to the next line.
                        AddPart(lineText, lineParts, partText, fragment.Start, ref lineStart);
                        FlushLine(lines, lineText, lineParts, ref lineStart, paragraph);
                        lineStart = fragment.Start;
                        break;
                    case '<': // Solton stores each complete line after '<'.
                        AddPart(lineText, lineParts, partText, fragment.Start, ref lineStart);
                        FlushLine(lines, lineText, lineParts, ref lineStart, paragraph);
                        lineStart = fragment.Start;
                        break;
                    default:
                        _ = partText.Append(character);
                        break;
                }
            }
            AddPart(lineText, lineParts, partText, fragment.Start, ref lineStart);

            previousStart = fragment.Start;
        }

        FlushLine(lines, lineText, lineParts, ref lineStart, paragraph);
        return lines;
    }

    private static void AddPart(
        StringBuilder lineText,
        List<KaraokePart> lineParts,
        StringBuilder partText,
        TimeSpan start,
        ref TimeSpan? lineStart)
    {
        if (partText.Length == 0)
            return;

        var text = partText.ToString();
        lineStart ??= start;
        _ = lineText.Append(text);
        lineParts.Add(new KaraokePart(start, text));
        _ = partText.Clear();
    }

    private static void FlushLine(
        List<KaraokeLine> lines,
        StringBuilder lineText,
        List<KaraokePart> lineParts,
        ref TimeSpan? lineStart,
        int paragraph)
    {
        (var text, IReadOnlyList<KaraokeTextSegment>? segments) = NormalizeParts(lineParts);
        if (text.Length > 0 && lineStart is { } start)
            lines.Add(new KaraokeLine(start, text, paragraph, segments));

        _ = lineText.Clear();
        lineParts.Clear();
        lineStart = null;
    }

    private static (string Text, IReadOnlyList<KaraokeTextSegment> Segments) NormalizeParts(
        IReadOnlyList<KaraokePart> parts)
    {
        var result = new StringBuilder(parts.Sum(static part => part.Text.Length));
        var segments = new List<KaraokeTextSegment>(parts.Count);
        var pendingSpace = false;
        foreach (KaraokePart part in parts)
        {
            var segmentStart = -1;
            var hasText = false;
            foreach (var character in part.Text)
            {
                if (char.IsWhiteSpace(character))
                {
                    pendingSpace = result.Length > 0;
                    continue;
                }

                if (pendingSpace)
                    _ = result.Append(' ');
                if (segmentStart < 0)
                    segmentStart = result.Length;
                _ = result.Append(character);
                pendingSpace = false;
                hasText = true;
            }

            if (hasText)
                segments.Add(new KaraokeTextSegment(segmentStart, result.Length - segmentStart, part.Start));
        }

        return (result.ToString(), segments);
    }

    private static bool EndsSentence(StringBuilder text)
    {
        for (var i = text.Length - 1; i >= 0; i--)
        {
            if (char.IsWhiteSpace(text[i]))
                continue;

            return text[i] is '.' or '!' or '?' or ':' or ';';
        }

        return false;
    }

    private static MidiMarker[] FilterKaraokeEvents(MidiMarker[] events)
    {
        var result = new List<MidiMarker>();
        foreach (MidiMarker marker in events)
        {
            var text = marker.Text;
            if (string.IsNullOrEmpty(text))
                continue;

            // Soft Karaoke metadata starts with '@'; Tune1000 and Solton chords start with '%'.
            if (text.StartsWith('@') || text.StartsWith('%'))
                continue;

            result.Add(marker);
        }

        return [.. result];
    }

    internal readonly record struct MidiLyricFragment(TimeSpan Start, string Text);
    private readonly record struct TransitionSchedule(
        TimeSpan Start,
        TimeSpan Complete,
        bool IsPage,
        TimeSpan FadeOutStart,
        TimeSpan FadeOutEnd,
        TimeSpan FadeInStart,
        TimeSpan FadeInEnd)
    {
        public static TransitionSchedule Normal(TimeSpan start, TimeSpan complete) =>
            new(start, complete, false, default, default, default, default);

        public static TransitionSchedule Page(
            TimeSpan start,
            TimeSpan complete,
            TimeSpan fadeOutStart,
            TimeSpan fadeOutEnd,
            TimeSpan fadeInStart,
            TimeSpan fadeInEnd) =>
            new(start, complete, true, fadeOutStart, fadeOutEnd, fadeInStart, fadeInEnd);

        public TransitionSchedule MoveStart(TimeSpan start) => this with
        {
            Start = start,
            Complete = Max(Complete, start),
            FadeOutStart = IsPage ? start : FadeOutStart,
            FadeOutEnd = IsPage ? Max(FadeOutEnd, start) : FadeOutEnd,
        };
    }

    private readonly record struct KaraokePart(TimeSpan Start, string Text);
    private readonly record struct KaraokeLine(
        TimeSpan Start,
        string Text,
        int Paragraph,
        IReadOnlyList<KaraokeTextSegment> Segments,
        bool HasCountdown = false);

    private static readonly TimeSpan CountdownGap = TimeSpan.FromSeconds(6);

    private static void MarkCountdownLines(List<KaraokeLine> lines)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            KaraokeLine line = lines[i];
            if (line.Segments.Count == 0)
                continue;

            TimeSpan firstLyric = line.Segments[0].Start;
            TimeSpan prevEnd = i > 0 ? GetLastSyllableStart(lines[i - 1]) : TimeSpan.Zero;
            TimeSpan gap = firstLyric - prevEnd;

            if (gap <= CountdownGap)
                continue;

            lines[i] = line with { HasCountdown = true };
        }
    }
}
