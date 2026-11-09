using System.Globalization;

namespace FatzMedia.Video.Core.Audio;

/// <summary>
/// Karaoke metadata (title, artist, key, tempo, ...) from a MIDI source's text events, read through
/// an engine-neutral <see cref="IMidiEventReader"/> (037A slice 1).
/// </summary>
static class MidiMetadataExtractor
{
    public static KaraokeMetadata Extract(
        IMidiEventReader reader,
        string sourcePath,
        TimeSpan duration,
        IReadOnlyList<MidiMark> lyricOrTextMarkers)
    {
        var parsed = ParseMarkerMetadata(
            lyricOrTextMarkers.Select(static marker => marker.Text));
        var title = parsed.Title;
        var artist = parsed.Artist;
        var writer = parsed.Writer;
        var lyricist = parsed.Lyricist;
        var year = parsed.Year;
        var copyright = parsed.Copyright;
        var keySignature = parsed.KeySignature;
        var tempo = parsed.Tempo;
        var timeSignature = parsed.TimeSignature;
        List<string> additionalInfo = [.. parsed.AdditionalInfo];

        var trackNames = reader.GetMarks(MidiMarkKind.TrackName);
        title ??= trackNames
            .OrderBy(static marker => marker.Track)
            .Select(static marker => marker.Text?.Trim())
            .FirstOrDefault(static text => !string.IsNullOrWhiteSpace(text));

        var standardCopyright = JoinMarkerText(reader.GetMarks(MidiMarkKind.Copyright));
        if (!string.IsNullOrWhiteSpace(standardCopyright))
            copyright = standardCopyright;

        var rawKey = FirstMarkerText(reader.GetMarks(MidiMarkKind.KeySignature));
        if (!string.IsNullOrWhiteSpace(rawKey))
            keySignature = FormatKeySignature(rawKey);

        var rawTiming = FirstMarkerText(reader.GetMarks(MidiMarkKind.TimeSignature));
        if (!string.IsNullOrWhiteSpace(rawTiming))
            timeSignature = rawTiming.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];

        var initialTempo = reader.GetTempoEvents()
            .Where(static midiEvent => midiEvent.MicrosecondsPerQuarterNote > 0)
            .OrderBy(static midiEvent => midiEvent.Position)
            .Cast<MidiTempoEvent?>()
            .FirstOrDefault();
        if (initialTempo is { MicrosecondsPerQuarterNote: > 0 } tempoEvent)
        {
            var beatsPerMinute = 60_000_000d / tempoEvent.MicrosecondsPerQuarterNote;
            tempo = $"{Math.Round(beatsPerMinute):0} BPM";
        }

        var hasEmbeddedMetadata = !string.IsNullOrWhiteSpace(title)
            || !string.IsNullOrWhiteSpace(artist)
            || !string.IsNullOrWhiteSpace(writer)
            || !string.IsNullOrWhiteSpace(lyricist)
            || !string.IsNullOrWhiteSpace(year)
            || !string.IsNullOrWhiteSpace(copyright)
            || !string.IsNullOrWhiteSpace(keySignature)
            || !string.IsNullOrWhiteSpace(tempo)
            || !string.IsNullOrWhiteSpace(timeSignature)
            || additionalInfo.Count > 0;

        title = string.IsNullOrWhiteSpace(title)
            ? Path.GetFileNameWithoutExtension(sourcePath)
            : title;
        if (string.IsNullOrWhiteSpace(title))
            title = "MIDI Track";

        return new KaraokeMetadata(
            title,
            artist ?? string.Empty,
            writer ?? string.Empty,
            lyricist ?? string.Empty,
            year ?? string.Empty,
            copyright ?? string.Empty,
            keySignature ?? string.Empty,
            tempo ?? string.Empty,
            timeSignature ?? string.Empty,
            duration,
            !hasEmbeddedMetadata,
            [.. additionalInfo]);
    }

    internal static ParsedMarkerMetadata ParseMarkerMetadata(IEnumerable<string?> markerTexts)
    {
        ArgumentNullException.ThrowIfNull(markerTexts);

        var titles = new List<string>();
        var additionalInfo = new List<string>();
        string? title = null;
        string? artist = null;
        string? writer = null;
        string? lyricist = null;
        string? year = null;
        string? copyright = null;
        string? keySignature = null;
        string? tempo = null;
        string? timeSignature = null;

        foreach (var markerText in markerTexts)
        {
            var text = markerText?.Trim() ?? string.Empty;
            if (text.StartsWith("@T", StringComparison.OrdinalIgnoreCase) && text.Length > 2)
                AddDistinct(titles, text[2..].Trim());
            else if (text.StartsWith("@I", StringComparison.OrdinalIgnoreCase) && text.Length > 2)
            {
                ParseInformation(
                    text[2..].Trim(),
                    ref title,
                    ref artist,
                    ref writer,
                    ref lyricist,
                    ref year,
                    ref copyright,
                    ref keySignature,
                    ref tempo,
                    ref timeSignature,
                    additionalInfo);
            }
        }

        title ??= titles.ElementAtOrDefault(0);
        artist ??= titles.ElementAtOrDefault(1);
        if (titles.Count > 2)
        {
            var thirdTitle = titles[2];
            if (LooksLikeCopyright(thirdTitle) && string.IsNullOrWhiteSpace(copyright))
                copyright = thirdTitle;
            else
                AddDistinct(additionalInfo, thirdTitle);
        }

        return new ParsedMarkerMetadata(
            title,
            artist,
            writer,
            lyricist,
            year,
            copyright,
            keySignature,
            tempo,
            timeSignature,
            additionalInfo);
    }

    internal sealed record ParsedMarkerMetadata(
        string? Title,
        string? Artist,
        string? Writer,
        string? Lyricist,
        string? Year,
        string? Copyright,
        string? KeySignature,
        string? Tempo,
        string? TimeSignature,
        IReadOnlyList<string> AdditionalInfo);

    internal static string FormatKeySignature(string value)
    {
        var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2
            || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var accidentals)
            || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var mode)
            || accidentals is < -7 or > 7)
        {
            return value.Trim();
        }

        string[] majorKeys = ["Cb", "Gb", "Db", "Ab", "Eb", "Bb", "F", "C", "G", "D", "A", "E", "B", "F#", "C#"];
        string[] minorKeys = ["Abm", "Ebm", "Bbm", "Fm", "Cm", "Gm", "Dm", "Am", "Em", "Bm", "F#m", "C#m", "G#m", "D#m", "A#m"];
        return mode == 1 ? minorKeys[accidentals + 7] : majorKeys[accidentals + 7];
    }

    static void ParseInformation(
        string value,
        ref string? title,
        ref string? artist,
        ref string? writer,
        ref string? lyricist,
        ref string? year,
        ref string? copyright,
        ref string? keySignature,
        ref string? tempo,
        ref string? timeSignature,
        List<string> additionalInfo)
    {
        if (TryReadLabel(value, ["title", "song"], out var labeledTitle))
            title ??= labeledTitle;
        else if (TryReadLabel(value, ["artist", "performer", "performed by"], out var labeledArtist))
            artist ??= labeledArtist;
        else if (TryReadLabel(value, ["written by", "writer", "composer", "music by"], out var labeledWriter))
            writer ??= labeledWriter;
        else if (TryReadLabel(value, ["lyricist", "lyrics by", "lyrics"], out var labeledLyricist))
            lyricist ??= labeledLyricist;
        else if (TryReadLabel(value, ["year", "date"], out var labeledYear))
            year ??= labeledYear;
        else if (TryReadLabel(value, ["copyright", "copyright information"], out var labeledCopyright))
            copyright ??= labeledCopyright;
        else if (TryReadLabel(value, ["key"], out var labeledKey))
            keySignature ??= labeledKey;
        else if (TryReadLabel(value, ["tempo", "bpm"], out var labeledTempo))
            tempo ??= labeledTempo.EndsWith("BPM", StringComparison.OrdinalIgnoreCase) ? labeledTempo : $"{labeledTempo} BPM";
        else if (TryReadLabel(value, ["timing", "time signature", "meter"], out var labeledTiming))
            timeSignature ??= labeledTiming;
        else if (LooksLikeCopyright(value))
            copyright ??= value;
        else if (value.Length == 4 && int.TryParse(value, out var parsedYear) && parsedYear is >= 1800 and <= 2200)
            year ??= value;
        else
            AddDistinct(additionalInfo, value);
    }

    static bool TryReadLabel(string value, IReadOnlyList<string> labels, out string result)
    {
        foreach (var label in labels)
        {
            if (!value.StartsWith(label, StringComparison.OrdinalIgnoreCase))
                continue;

            var remainder = value[label.Length..].TrimStart(' ', ':', '-', '=');
            if (remainder.Length > 0)
            {
                result = remainder;
                return true;
            }
        }

        result = string.Empty;
        return false;
    }

    static bool LooksLikeCopyright(string value) =>
        value.Contains("copyright", StringComparison.OrdinalIgnoreCase)
        || value.Contains("(c)", StringComparison.OrdinalIgnoreCase)
        || value.Contains('©');

    static string FirstMarkerText(IEnumerable<MidiMark> markers) => markers
        .Select(static marker => marker.Text?.Trim())
        .FirstOrDefault(static text => !string.IsNullOrWhiteSpace(text)) ?? string.Empty;

    static string JoinMarkerText(IEnumerable<MidiMark> markers) => string.Join(
        " • ",
        markers.Select(static marker => marker.Text?.Trim())
            .Where(static text => !string.IsNullOrWhiteSpace(text))
            .Distinct(StringComparer.OrdinalIgnoreCase));

    static void AddDistinct(List<string> values, string value)
    {
        if (!string.IsNullOrWhiteSpace(value) && !values.Contains(value, StringComparer.OrdinalIgnoreCase))
            values.Add(value);
    }
}

public sealed record KaraokeMetadata(
    string Title,
    string Artist,
    string Writer,
    string Lyricist,
    string Year,
    string Copyright,
    string KeySignature,
    string Tempo,
    string TimeSignature,
    TimeSpan Duration,
    bool IsFallback,
    IReadOnlyList<string> AdditionalInfo);
