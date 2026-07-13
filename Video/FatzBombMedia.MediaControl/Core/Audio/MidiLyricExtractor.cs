using FatzBombMedia.MediaControl.Core.Subtitle;
using ManagedBass.Midi;
using System.Text;
using MBass = ManagedBass.Bass;

namespace FatzBombMedia.MediaControl.Core.Audio;

/// <summary>
/// Extracts karaoke lyrics from an open BASS MIDI channel using BASS MIDI marker APIs.
/// Lyric markers (MIDI meta event 5) and text markers (MIDI meta event 1) are converted
/// to <see cref="SubtitleCue"/> objects with timing derived from the BASS channel position.
/// </summary>
internal static class MidiLyricExtractor
{
    private const int PreferredLineLength = 42;
    private static readonly TimeSpan PreferredTransitionLead = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan PreferredInitialPreview = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan PreferredPageHold = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PreferredPagePreview = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DefaultTransitionDuration = TimeSpan.FromMilliseconds(450);
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
    public static IReadOnlyList<SubtitleCue>? ExtractLyrics(int channelHandle, TimeSpan? transitionDuration = null)
    {
        if (channelHandle == 0)
            return null;

        MidiMarker[] lyrics = GetMarksSafe(channelHandle, MidiMarkerType.Lyric);
        MidiMarker[] textEvents = GetMarksSafe(channelHandle, MidiMarkerType.Text);

        List<SubtitleCue> cues = BuildCuesFromMarkers(channelHandle, lyrics, textEvents, transitionDuration ?? DefaultTransitionDuration);
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
        TimeSpan transitionDuration)
    {
        MidiMarker[] lyricMarkers = FilterKaraokeEvents(lyrics);
        MidiMarker[] markers = lyricMarkers.Length > 0 ? lyricMarkers : FilterKaraokeEvents(textEvents);

        if (markers.Length == 0)
            return [];

        var durationBytes = MBass.ChannelGetLength(stream);
        var durationSeconds = MBass.ChannelBytes2Seconds(stream, durationBytes);
        var fragments = new List<MidiLyricFragment>(markers.Length);

        foreach (MidiMarker marker in markers)
        {
            var startSeconds = MBass.ChannelBytes2Seconds(stream, marker.Position);
            if (!double.IsFinite(startSeconds) || startSeconds < 0)
                continue;

            fragments.Add(new MidiLyricFragment(TimeSpan.FromSeconds(startSeconds), marker.Text));
        }

        if (fragments.Count == 0)
            return [];

        TimeSpan duration = double.IsFinite(durationSeconds) && durationSeconds > 0
            ? TimeSpan.FromSeconds(durationSeconds)
            : fragments[^1].Start + TimeSpan.FromSeconds(3);
        return BuildCues(fragments, duration, transitionDuration);
    }

    internal static List<SubtitleCue> BuildCues(
        IReadOnlyList<MidiLyricFragment> fragments,
        TimeSpan duration,
        TimeSpan? transitionDuration = null)
    {
        TimeSpan animationDuration = transitionDuration is { } configuredDuration && configuredDuration > TimeSpan.Zero
            ? configuredDuration
            : DefaultTransitionDuration;
        List<KaraokeLine> lines = BuildLines(fragments);
        var cues = new List<SubtitleCue>(lines.Count);
        if (lines.Count == 0)
            return cues;

        var schedules = new TransitionSchedule[lines.Count];
        schedules[0] = CalculateInitialTransition(lines[0], duration, animationDuration);
        for (var i = 1; i < lines.Count; i++)
        {
            var clearsPage = lines[i - 1].Paragraph != lines[i].Paragraph;
            TransitionSchedule schedule = clearsPage
                ? CalculatePageTransition(lines[i - 1], lines[i], animationDuration)
                : CalculateScrollTransition(lines[i - 1], lines[i], animationDuration);
            TimeSpan minimumStart = schedules[i - 1].Start + TimeSpan.FromTicks(1);
            schedules[i] = schedule.Start >= minimumStart
                ? schedule
                : schedule.MoveStart(minimumStart);
        }

        for (var i = 0; i < lines.Count; i++)
        {
            KaraokeLine line = lines[i];
            TransitionSchedule schedule = schedules[i];
            TimeSpan cueStart = schedule.Start;
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
                KaraokeTiming = new KaraokeCueTiming(timingLines, activeLineIndex, cueStart, end)
                {
                    ClearsPage = clearsPage,
                    IncomingStart = line.Start,
                    TransitionComplete = Min(schedule.Complete, end),
                    PageFadeOutStart = schedule.FadeOutStart,
                    PageFadeOutEnd = schedule.FadeOutEnd,
                    PageFadeInStart = schedule.FadeInStart,
                    PageFadeInEnd = schedule.FadeInEnd,
                    OutgoingPageLines = outgoingPageLines,
                    OutgoingActiveLineIndex = outgoingActiveLineIndex,
                },
            });
        }

        return cues;
    }

    private static TransitionSchedule CalculateScrollTransition(
        KaraokeLine currentLine,
        KaraokeLine nextLine,
        TimeSpan transitionDuration)
    {
        TimeSpan lastSyllable = GetLastSyllableStart(currentLine);
        TimeSpan desiredCompletion = nextLine.Start - PreferredTransitionLead;
        if (desiredCompletion - lastSyllable >= transitionDuration)
            return TransitionSchedule.Normal(desiredCompletion - transitionDuration, desiredCompletion);

        TimeSpan start = Max(lastSyllable, currentLine.Start);
        TimeSpan complete = Min(start + transitionDuration, nextLine.Start);
        return TransitionSchedule.Normal(start, Max(start, complete));
    }

    private static TransitionSchedule CalculateInitialTransition(
        KaraokeLine firstLine,
        TimeSpan duration,
        TimeSpan transitionDuration)
    {
        TimeSpan desiredCompletion = firstLine.Start - PreferredInitialPreview;
        if (desiredCompletion >= TimeSpan.Zero)
        {
            TimeSpan start = Max(TimeSpan.Zero, desiredCompletion - transitionDuration);
            return TransitionSchedule.Normal(start, desiredCompletion);
        }

        TimeSpan complete = Min(firstLine.Start, transitionDuration);
        return TransitionSchedule.Normal(TimeSpan.Zero, Min(complete, duration));
    }

    private static TransitionSchedule CalculatePageTransition(
        KaraokeLine currentLine,
        KaraokeLine nextLine,
        TimeSpan transitionDuration)
    {
        TimeSpan lastSyllable = GetLastSyllableStart(currentLine);
        TimeSpan gap = Max(TimeSpan.Zero, nextLine.Start - lastSyllable);
        TimeSpan fadeInEnd = gap >= PreferredPageHold + PreferredPagePreview
            ? nextLine.Start - PreferredPagePreview
            : lastSyllable + TimeSpan.FromTicks(gap.Ticks / 2);
        fadeInEnd = Min(nextLine.Start, Max(lastSyllable, fadeInEnd));
        TimeSpan fadeInStart = Max(lastSyllable, fadeInEnd - transitionDuration);

        TimeSpan desiredFadeOutStart = lastSyllable + PreferredPageHold;
        TimeSpan latestFadeOutStart = fadeInStart - transitionDuration;
        TimeSpan fadeOutStart = Max(lastSyllable, Min(desiredFadeOutStart, latestFadeOutStart));
        TimeSpan fadeOutEnd = Min(fadeOutStart + transitionDuration, fadeInStart);

        return TransitionSchedule.Page(
            fadeOutStart,
            fadeInEnd,
            fadeOutStart,
            Max(fadeOutStart, fadeOutEnd),
            fadeInStart,
            fadeInEnd);
    }

    private static TimeSpan GetLastSyllableStart(KaraokeLine line) => line.Segments.Count > 0
        ? line.Segments[^1].Start
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
            result.Add(new KaraokeLineTiming(lines[i].Text, lines[i].Segments));

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
            result.Add(new KaraokeLineTiming(lines[i].Text, lines[i].Segments));

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
        IReadOnlyList<KaraokeTextSegment> Segments);
}
