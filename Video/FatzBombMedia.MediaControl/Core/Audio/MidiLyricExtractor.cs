using FatzBombMedia.MediaControl.Core.Subtitle;
using ManagedBass;
using ManagedBass.Midi;
using MBass = ManagedBass.Bass;

namespace FatzBombMedia.MediaControl.Core.Audio;

/// <summary>
/// Extracts karaoke lyrics from an open BASS MIDI channel using BASS MIDI marker APIs.
/// Lyric markers (MIDI meta event 5) and text markers (MIDI meta event 1) are converted
/// to <see cref="SubtitleCue"/> objects with timing derived from the BASS channel position.
/// </summary>
internal static class MidiLyricExtractor
{
    private static readonly HashSet<string> MidiExtensions = new(StringComparer.OrdinalIgnoreCase)
    { ".mid", ".midi", ".kar", ".rmi" };

    public static bool IsMidiFile(string path) =>
        MidiExtensions.Contains(Path.GetExtension(path));

    /// <summary>
    /// Extracts lyric cues from an open BASS MIDI channel (created via Bass.MusicLoad).
    /// Returns null if the channel has no lyrics or is not a MIDI stream.
    /// </summary>
    public static IReadOnlyList<SubtitleCue>? ExtractLyrics(int channelHandle)
    {
        if (channelHandle == 0)
            return null;

        var lyrics = GetMarksSafe(channelHandle, MidiMarkerType.Lyric);
        var textEvents = GetMarksSafe(channelHandle, MidiMarkerType.Text);

        var cues = BuildCuesFromMarkers(channelHandle, lyrics, textEvents);
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
        MidiMarker[] textEvents)
    {
        var markers = lyrics.Length > 0 ? lyrics : FilterKaraokeTextEvents(textEvents);

        if (markers.Length == 0)
            return [];

        var cues = new List<SubtitleCue>(markers.Length);
        var durationBytes = MBass.ChannelGetLength(stream);
        var durationSeconds = MBass.ChannelBytes2Seconds(stream, durationBytes);

        for (var i = 0; i < markers.Length; i++)
        {
            var marker = markers[i];
            var startSeconds = MBass.ChannelBytes2Seconds(stream, marker.Position);

            var endSeconds = i + 1 < markers.Length
                ? MBass.ChannelBytes2Seconds(stream, markers[i + 1].Position)
                : durationSeconds;

            if (endSeconds <= startSeconds)
                endSeconds = startSeconds + 0.1;

            var text = CleanLyricText(marker.Text);
            if (string.IsNullOrWhiteSpace(text))
                continue;

            cues.Add(new SubtitleCue(
                i,
                TimeSpan.FromSeconds(startSeconds),
                TimeSpan.FromSeconds(endSeconds),
                text));
        }

        return cues;
    }

    private static MidiMarker[] FilterKaraokeTextEvents(MidiMarker[] textEvents)
    {
        var result = new List<MidiMarker>();
        foreach (var marker in textEvents)
        {
            var text = marker.Text;
            if (string.IsNullOrEmpty(text))
                continue;

            // Skip KAR metadata prefixes (@K, @L, @T, @I, @S)
            if (text.StartsWith("@") && text.Length <= 3)
                continue;

            if (text.StartsWith("@K") || text.StartsWith("@L") || text.StartsWith("@T") || text.StartsWith("@I") || text.StartsWith("@S"))
                continue;

            result.Add(marker);
        }

        return result.ToArray();
    }

    private static string CleanLyricText(string text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        // KAR files use / for newlines and \ for line breaks
        text = text.Replace('/', '\n').Replace('\\', '\n');

        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        text = string.Join("\n", lines.Select(l => l.Trim()));

        return text.Trim();
    }
}
