using System.Text;
using FatzMedia.Video.Core.Subtitle;

namespace FatzMedia.Video.Core.Audio;

/// <summary>
/// Builds karaoke lyric cues from the events of a MIDI source, read through an engine-neutral
/// <see cref="IMidiEventReader"/> (037A slice 1). Lyric events (MIDI meta event 5), or text
/// events (meta event 1) when there are none, become <see cref="SubtitleCue"/> objects timed by
/// the reader's positions. On BASS the reader is <see cref="BassMidiEventReader"/> on the open
/// MIDI channel, which makes the same BASS marker calls this class made before.
/// </summary>
static class MidiLyricCueBuilder
{
    const int PreferredLineLength = 42;

    static readonly TimeSpan TargetPageHold = TimeSpan.FromMilliseconds(2000);
    static readonly TimeSpan TargetPagePreview = TimeSpan.FromMilliseconds(1000);
    static readonly TimeSpan TargetPageTransitionDuration = TimeSpan.FromMilliseconds(1000);
    static readonly TimeSpan TargetScrollDuration = TimeSpan.FromMilliseconds(1500);
    static readonly TimeSpan LyricLeadOffset = TimeSpan.FromMilliseconds(400);
    static readonly TimeSpan ParagraphGap = TimeSpan.FromSeconds(2);
    static readonly TimeSpan SentenceGap = TimeSpan.FromSeconds(0.9);
    static readonly HashSet<string> MidiExtensions = new(StringComparer.OrdinalIgnoreCase)
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
        TimeSpan? transitionDuration = null) =>
        ExtractLyrics(BassMidiEventReader.ForHandle(channelHandle), sourcePath, transitionDuration);

    /// <summary>
    /// Extracts lyric cues from the MIDI events <paramref name="reader"/> reads. Returns null when
    /// there is no reader or the source has no lyrics.
    /// </summary>
    public static IReadOnlyList<SubtitleCue>? ExtractLyrics(
        IMidiEventReader? reader,
        string sourcePath,
        TimeSpan? transitionDuration = null)
    {
        if (reader is null)
            return null;

        var lyrics = reader.GetMarks(MidiMarkKind.Lyric);
        var textEvents = reader.GetMarks(MidiMarkKind.Text);

        var durationSeconds = reader.GetDurationSeconds();
        var duration = double.IsFinite(durationSeconds) && durationSeconds > 0
            ? TimeSpan.FromSeconds(durationSeconds)
            : TimeSpan.Zero;
        IReadOnlyList<MidiMark> metadataMarkers =
            [.. lyrics.Concat(textEvents).OrderBy(static marker => marker.Position)];
        var metadata = MidiMetadataExtractor.Extract(reader, sourcePath, duration, metadataMarkers);
        var cues = BuildCuesFromMarkers(
            reader,
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

    static List<SubtitleCue> BuildCuesFromMarkers(
        IMidiEventReader reader,
        IReadOnlyList<MidiMark> lyrics,
        IReadOnlyList<MidiMark> textEvents,
        TimeSpan duration,
        TimeSpan transitionDuration)
    {
        var lyricMarkers = FilterKaraokeEvents(lyrics);
        var markers = lyricMarkers.Length > 0 ? lyricMarkers : FilterKaraokeEvents(textEvents);

        if (markers.Length == 0)
            return [];

        var fragments = new List<MidiLyricFragment>(markers.Length);

        foreach (var marker in markers)
        {
            var startSeconds = reader.PositionToSeconds(marker.Position);
            if (!double.IsFinite(startSeconds) || startSeconds < 0)
                continue;

            // Shift lyric markers 400ms earlier so the wipe highlights ahead of the note,
            // giving the singer preparation time like CDG files typically do.
            var adjustedStart = TimeSpan.FromSeconds(startSeconds) - LyricLeadOffset;
            if (adjustedStart < TimeSpan.Zero)
                adjustedStart = TimeSpan.Zero;

            fragments.Add(new MidiLyricFragment(adjustedStart, marker.Text!));
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
        var animationDuration = transitionDuration is { } configuredDuration
            ? Max(TimeSpan.Zero, configuredDuration)
            : TargetPageTransitionDuration;
        var lines = BuildLines(fragments);
        var cues = new List<SubtitleCue>(lines.Count);
        if (lines.Count == 0)
            return cues;

        MarkCountdownLines(lines);

        var schedules = new TransitionSchedule[lines.Count];
        schedules[0] = CalculateInitialTransition(lines[0], animationDuration);
        for (var i = 1; i < lines.Count; i++)
        {
            var clearsPage = lines[i - 1].Paragraph != lines[i].Paragraph;
            var schedule = clearsPage
                ? CalculatePageTransition(lines[i - 1], lines[i], animationDuration)
                : CalculateScrollTransition(lines[i - 1], lines[i]);
            var minimumStart = schedules[i - 1].Start + TimeSpan.FromTicks(1);
            schedules[i] = schedule.Start >= minimumStart
                ? schedule
                : schedule.MoveStart(minimumStart);
        }

        var firstLyricStart = GetFirstSyllableStart(lines[0]);
        var firstPresentationStart = lines[0].HasCountdown
            ? Max(TimeSpan.Zero, firstLyricStart - KaraokeLineTiming.CountdownDuration)
            : firstLyricStart;

        // Fade five seconds after the final syllable, or during the final second when the media ends sooner.
        var lastSyllableEnd = GetLastSyllableStart(lines[^1]);
        var endFadeStart = lastSyllableEnd + TimeSpan.FromSeconds(5);
        var endFadeEnd = endFadeStart + TimeSpan.FromMilliseconds(1000);
        if (endFadeEnd > duration)
        {
            endFadeEnd = duration;
            endFadeStart = Max(lastSyllableEnd, endFadeEnd - TimeSpan.FromMilliseconds(1000));
        }

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var schedule = schedules[i];
            var cueStart = i == 0 ? TimeSpan.Zero : schedule.Start;
            var end = i + 1 < lines.Count ? schedules[i + 1].Start : duration;
            if (i == lines.Count - 1 && end <= cueStart)
                end = cueStart + TimeSpan.FromMilliseconds(100);

            var timingLines = CreateLineWindow(lines, i, line.Paragraph, out var activeLineIndex);
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

    static TransitionSchedule CalculateScrollTransition(
        KaraokeLine currentLine,
        KaraokeLine nextLine)
    {
        var nextLineStart = GetFirstSyllableStart(nextLine);
        var nextPresentationStart = nextLine.HasCountdown
            ? Max(TimeSpan.Zero, nextLineStart - KaraokeLineTiming.CountdownDuration)
            : nextLineStart;

        var currentLineEnd = GetLastSyllableStart(currentLine);
        var earliestScrollStart = GetFirstSyllableStart(currentLine);
        TimeSpan scrollStart;
        TimeSpan scrollComplete;
        var gap = nextPresentationStart - currentLineEnd;
        if (gap >= TargetScrollDuration)
        {
            scrollComplete = nextPresentationStart;
            scrollStart = scrollComplete - TargetScrollDuration;
        }
        else
        {
            var gapCenter = currentLineEnd + TimeSpan.FromTicks(gap.Ticks / 2);
            scrollStart = gapCenter - TimeSpan.FromTicks(TargetScrollDuration.Ticks / 2);
            scrollComplete = scrollStart + TargetScrollDuration;
            if (scrollStart < earliestScrollStart)
            {
                var shift = earliestScrollStart - scrollStart;
                scrollStart += shift;
                scrollComplete += shift;
            }
        }

        return TransitionSchedule.Normal(scrollStart, scrollComplete);
    }

    static TransitionSchedule CalculateInitialTransition(
        KaraokeLine firstLine,
        TimeSpan transitionDuration)
    {
        var firstLineStart = GetFirstSyllableStart(firstLine);
        var preview = firstLine.HasCountdown ? KaraokeLineTiming.CountdownDuration : TargetPagePreview;
        var fadeInEnd = Max(TimeSpan.Zero, firstLineStart - preview);
        var fadeInStart = Max(TimeSpan.Zero, fadeInEnd - transitionDuration);
        return TransitionSchedule.Normal(fadeInStart, fadeInEnd);
    }

    static TransitionSchedule CalculatePageTransition(
        KaraokeLine currentLine,
        KaraokeLine nextLine,
        TimeSpan transitionDuration)
    {
        var currentPageEnd = GetLastSyllableStart(currentLine);
        var nextPageStart = GetFirstSyllableStart(nextLine);
        var totalGap = nextPageStart - currentPageEnd;

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
            var roomAfterFullFades = Max(TimeSpan.Zero, totalGap - (transitionDuration * 2));
            presentationLead = Min(TargetPagePreview, roomAfterFullFades);
        }

        var incomingReady = nextPageStart - presentationLead;
        var transitionWindow = Max(TimeSpan.Zero, incomingReady - currentPageEnd);
        var fadeDuration = Min(transitionDuration, transitionWindow);
        var outHold = TimeSpan.Zero;
        if (transitionWindow >= transitionDuration * 2)
        {
            outHold = Min(TargetPageHold, transitionWindow - (fadeDuration * 2));
        }

        var outStart = currentPageEnd + outHold;
        var outFadeEnd = outStart + fadeDuration;
        var inFadeEnd = incomingReady;
        var inFadeStart = inFadeEnd - fadeDuration;

        return TransitionSchedule.Page(
            outStart, inFadeEnd,
            outStart, outFadeEnd,
            inFadeStart, inFadeEnd);
    }

    static TimeSpan GetLastSyllableStart(KaraokeLine line) => line.Segments.Count > 0
        ? line.Segments[^1].Start
        : line.Start;

    static TimeSpan GetFirstSyllableStart(KaraokeLine line) => line.Segments.Count > 0
        ? line.Segments[0].Start
        : line.Start;

    static TimeSpan Min(TimeSpan left, TimeSpan right) => left <= right ? left : right;
    static TimeSpan Max(TimeSpan left, TimeSpan right) => left >= right ? left : right;

    static List<KaraokeLineTiming> CreateLineWindow(
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

    static List<KaraokeLineTiming> CreateOutgoingPageWindow(
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

    static List<KaraokeLine> BuildLines(IReadOnlyList<MidiLyricFragment> fragments)
    {
        var lines = new List<KaraokeLine>();
        var lineText = new StringBuilder();
        var lineParts = new List<KaraokePart>();
        TimeSpan? lineStart = null;
        TimeSpan? previousStart = null;
        var paragraph = 0;

        foreach (var fragment in fragments.OrderBy(static fragment => fragment.Start))
        {
            if (string.IsNullOrEmpty(fragment.Text))
                continue;

            var gap = previousStart is { } previous ? fragment.Start - previous : TimeSpan.Zero;
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

    static void AddPart(
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

    static void FlushLine(
        List<KaraokeLine> lines,
        StringBuilder lineText,
        List<KaraokePart> lineParts,
        ref TimeSpan? lineStart,
        int paragraph)
    {
        (var text, var segments) = NormalizeParts(lineParts);
        if (text.Length > 0 && lineStart is { } start)
            lines.Add(new KaraokeLine(start, text, paragraph, segments));

        _ = lineText.Clear();
        lineParts.Clear();
        lineStart = null;
    }

    static (string Text, IReadOnlyList<KaraokeTextSegment> Segments) NormalizeParts(
        IReadOnlyList<KaraokePart> parts)
    {
        var result = new StringBuilder(parts.Sum(static part => part.Text.Length));
        var segments = new List<KaraokeTextSegment>(parts.Count);
        var pendingSpace = false;
        foreach (var part in parts)
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

    static bool EndsSentence(StringBuilder text)
    {
        for (var i = text.Length - 1; i >= 0; i--)
        {
            if (char.IsWhiteSpace(text[i]))
                continue;

            return text[i] is '.' or '!' or '?' or ':' or ';';
        }

        return false;
    }

    static MidiMark[] FilterKaraokeEvents(IReadOnlyList<MidiMark> events)
    {
        var result = new List<MidiMark>();
        foreach (var marker in events)
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
    readonly record struct TransitionSchedule(
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

    readonly record struct KaraokePart(TimeSpan Start, string Text);
    readonly record struct KaraokeLine(
        TimeSpan Start,
        string Text,
        int Paragraph,
        IReadOnlyList<KaraokeTextSegment> Segments,
        bool HasCountdown = false);

    static readonly TimeSpan CountdownGap = TimeSpan.FromSeconds(6);

    static void MarkCountdownLines(List<KaraokeLine> lines)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line.Segments.Count == 0)
                continue;

            var firstLyric = line.Segments[0].Start;
            var prevEnd = i > 0 ? GetLastSyllableStart(lines[i - 1]) : TimeSpan.Zero;
            var gap = firstLyric - prevEnd;

            if (gap <= CountdownGap)
                continue;

            lines[i] = line with { HasCountdown = true };
        }
    }
}
