using System.Globalization;
using ManagedBass.Midi;

namespace FatzBombMedia.MediaControl.Core.Audio;

internal static class MidiMetadataExtractor
{
    public static KaraokeMetadata Extract(
        int stream,
        string sourcePath,
        TimeSpan duration,
        IReadOnlyList<MidiMarker> lyricOrTextMarkers)
    {
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

        foreach (MidiMarker marker in lyricOrTextMarkers)
        {
            string text = marker.Text?.Trim() ?? string.Empty;
            if (text.StartsWith("@T", StringComparison.OrdinalIgnoreCase) && text.Length > 2)
            {
                AddDistinct(titles, text[2..].Trim());
            }
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
            string thirdTitle = titles[2];
            if (LooksLikeCopyright(thirdTitle) && string.IsNullOrWhiteSpace(copyright))
                copyright = thirdTitle;
            else
                AddDistinct(additionalInfo, thirdTitle);
        }

        MidiMarker[] trackNames = GetMarksSafe(stream, MidiMarkerType.TrackName);
        title ??= trackNames
            .OrderBy(static marker => marker.Track)
            .Select(static marker => marker.Text?.Trim())
            .FirstOrDefault(static text => !string.IsNullOrWhiteSpace(text));

        string standardCopyright = JoinMarkerText(GetMarksSafe(stream, MidiMarkerType.Copyright));
        if (!string.IsNullOrWhiteSpace(standardCopyright))
            copyright = standardCopyright;

        string rawKey = FirstMarkerText(GetMarksSafe(stream, MidiMarkerType.KeySignature));
        if (!string.IsNullOrWhiteSpace(rawKey))
            keySignature = FormatKeySignature(rawKey);

        string rawTiming = FirstMarkerText(GetMarksSafe(stream, MidiMarkerType.TimeSignature));
        if (!string.IsNullOrWhiteSpace(rawTiming))
            timeSignature = rawTiming.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];

        MidiEvent? initialTempo = GetEventsSafe(stream, MidiEventType.Tempo)
            .Where(static midiEvent => midiEvent.Parameter > 0)
            .OrderBy(static midiEvent => midiEvent.Position)
            .Cast<MidiEvent?>()
            .FirstOrDefault();
        if (initialTempo is { Parameter: > 0 } tempoEvent)
        {
            var beatsPerMinute = 60_000_000d / tempoEvent.Parameter;
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

    internal static string FormatKeySignature(string value)
    {
        string[] parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
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

    private static void ParseInformation(
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
        if (TryReadLabel(value, ["title", "song"], out string labeledTitle))
            title ??= labeledTitle;
        else if (TryReadLabel(value, ["artist", "performer", "performed by"], out string labeledArtist))
            artist ??= labeledArtist;
        else if (TryReadLabel(value, ["written by", "writer", "composer", "music by"], out string labeledWriter))
            writer ??= labeledWriter;
        else if (TryReadLabel(value, ["lyricist", "lyrics by", "lyrics"], out string labeledLyricist))
            lyricist ??= labeledLyricist;
        else if (TryReadLabel(value, ["year", "date"], out string labeledYear))
            year ??= labeledYear;
        else if (TryReadLabel(value, ["copyright", "copyright information"], out string labeledCopyright))
            copyright ??= labeledCopyright;
        else if (TryReadLabel(value, ["key"], out string labeledKey))
            keySignature ??= labeledKey;
        else if (TryReadLabel(value, ["tempo", "bpm"], out string labeledTempo))
            tempo ??= labeledTempo.EndsWith("BPM", StringComparison.OrdinalIgnoreCase) ? labeledTempo : $"{labeledTempo} BPM";
        else if (TryReadLabel(value, ["timing", "time signature", "meter"], out string labeledTiming))
            timeSignature ??= labeledTiming;
        else if (LooksLikeCopyright(value))
            copyright ??= value;
        else if (value.Length == 4 && int.TryParse(value, out var parsedYear) && parsedYear is >= 1800 and <= 2200)
            year ??= value;
        else
            AddDistinct(additionalInfo, value);
    }

    private static bool TryReadLabel(string value, IReadOnlyList<string> labels, out string result)
    {
        foreach (string label in labels)
        {
            if (!value.StartsWith(label, StringComparison.OrdinalIgnoreCase))
                continue;

            string remainder = value[label.Length..].TrimStart(' ', ':', '-', '=');
            if (remainder.Length > 0)
            {
                result = remainder;
                return true;
            }
        }

        result = string.Empty;
        return false;
    }

    private static bool LooksLikeCopyright(string value) =>
        value.Contains("copyright", StringComparison.OrdinalIgnoreCase)
        || value.Contains("(c)", StringComparison.OrdinalIgnoreCase)
        || value.Contains('©');

    private static MidiMarker[] GetMarksSafe(int stream, MidiMarkerType type)
    {
        try
        {
            return BassMidi.StreamGetMarks(stream, -1, type) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static MidiEvent[] GetEventsSafe(int stream, MidiEventType type)
    {
        try
        {
            return BassMidi.StreamGetEvents(stream, -1, type) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static string FirstMarkerText(IEnumerable<MidiMarker> markers) => markers
        .Select(static marker => marker.Text?.Trim())
        .FirstOrDefault(static text => !string.IsNullOrWhiteSpace(text)) ?? string.Empty;

    private static string JoinMarkerText(IEnumerable<MidiMarker> markers) => string.Join(
        " • ",
        markers.Select(static marker => marker.Text?.Trim())
            .Where(static text => !string.IsNullOrWhiteSpace(text))
            .Distinct(StringComparer.OrdinalIgnoreCase));

    private static void AddDistinct(List<string> values, string value)
    {
        if (!string.IsNullOrWhiteSpace(value) && !values.Contains(value, StringComparer.OrdinalIgnoreCase))
            values.Add(value);
    }
}

internal sealed record KaraokeMetadata(
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
