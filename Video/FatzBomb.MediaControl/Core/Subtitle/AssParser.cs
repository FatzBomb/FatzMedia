using System.Globalization;
using System.Text.RegularExpressions;

namespace FatzBomb.MediaControl.Core.Subtitle;

/// <summary>
/// Reads plain dialogue cues from ASS/SSA files. Styling and positioning tags are deliberately
/// ignored for the text-subtitle MVP; DirectWrite applies the Core subtitle style at render time.
/// </summary>
internal static partial class AssParser
{
    private const string DialoguePrefix = "Dialogue:";

    public static IReadOnlyList<SubtitleCue> Parse(string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        var cues = new List<SubtitleCue>();
        var sequence = 1;
        foreach (var line in content.TrimStart('\uFEFF').Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!line.StartsWith(DialoguePrefix, StringComparison.OrdinalIgnoreCase))
                continue;

            var fields = line[DialoguePrefix.Length..].Split(',', 10, StringSplitOptions.None);
            if (fields.Length != 10
                || !TryParseTimestamp(fields[1], out TimeSpan start)
                || !TryParseTimestamp(fields[2], out TimeSpan end)
                || end <= start)
            {
                continue;
            }

            var text = NormalizeDialogueText(fields[9]);
            if (text.Length == 0)
                continue;

            cues.Add(new SubtitleCue(sequence++, start, end, text));
        }

        return cues;
    }

    private static bool TryParseTimestamp(string value, out TimeSpan timestamp)
    {
        timestamp = default;
        var parts = value.Trim().Split(':');
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

    internal static string NormalizeDialogueText(string text) => OverrideTagRegex().Replace(text, string.Empty)
        .Replace("\\N", "\n", StringComparison.Ordinal)
        .Replace("\\n", "\n", StringComparison.Ordinal)
        .Trim();

    [GeneratedRegex(@"\{[^}]*\}")]
    private static partial Regex OverrideTagRegex();
}
