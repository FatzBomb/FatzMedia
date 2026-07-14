using System.Globalization;

namespace FatzBomb.MediaControl.Core.Subtitle;

internal static class SrtParser
{
    public static IReadOnlyList<SubtitleCue> Parse(string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        using var reader = new StringReader(content.TrimStart('\uFEFF'));
        var cues = new List<SubtitleCue>();
        var fallbackSequence = 1;

        while (TryReadNonEmptyLine(reader, out var firstLine))
        {
            var timingLine = firstLine;
            var sequence = fallbackSequence;
            if (!firstLine.Contains("-->", StringComparison.Ordinal))
            {
                if (int.TryParse(firstLine.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsedSequence))
                    sequence = parsedSequence;

                timingLine = reader.ReadLine();
                if (timingLine == null)
                    break;
            }

            fallbackSequence = Math.Max(fallbackSequence + 1, sequence + 1);

            if (!TryParseTiming(timingLine, out TimeSpan start, out TimeSpan end) || end <= start)
            {
                SkipCueBody(reader);
                continue;
            }

            var lines = new List<string>();
            string? textLine;
            while (!string.IsNullOrEmpty(textLine = reader.ReadLine()))
                lines.Add(textLine);

            if (lines.Count == 0)
                continue;

            cues.Add(new SubtitleCue(sequence, start, end, string.Join("\n", lines)));
        }

        return cues;
    }

    private static bool TryReadNonEmptyLine(StringReader reader, out string line)
    {
        while (reader.ReadLine() is { } candidate)
        {
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                line = candidate;
                return true;
            }
        }

        line = string.Empty;
        return false;
    }

    private static void SkipCueBody(StringReader reader)
    {
        while (reader.ReadLine() is { } line && !string.IsNullOrWhiteSpace(line))
        {
        }
    }

    private static bool TryParseTiming(string line, out TimeSpan start, out TimeSpan end)
    {
        start = default;
        end = default;
        var separator = line.IndexOf("-->", StringComparison.Ordinal);
        return separator >= 0 && TryParseTimestamp(line[..separator], out start)
            && TryParseTimestamp(line[(separator + 3)..], out end);
    }

    private static bool TryParseTimestamp(string value, out TimeSpan timestamp)
    {
        timestamp = default;
        var parts = value.Trim().Replace(',', '.').Split(':');
        if (parts.Length != 3
            || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var hours)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minutes)
            || !double.TryParse(parts[2], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var seconds)
            || hours < 0 || minutes is < 0 or >= 60 || seconds is < 0 or >= 60)
        {
            return false;
        }

        timestamp = TimeSpan.FromHours(hours) + TimeSpan.FromMinutes(minutes) + TimeSpan.FromSeconds(seconds);
        return true;
    }
}
