using FatzBomb.MediaControl.Core.Subtitle;

namespace FatzBomb.MediaControl.Core.Audio;

/// <summary>
/// Extracts karaoke lyrics from an open BASS MIDI channel. Obsolete (ENG-039): it takes a BASS
/// handle, so it goes with BASS (037A). The player shows MIDI lyrics as subtitles on its own.
/// </summary>
[Obsolete("MidiLyricExtractor takes a BASS channel handle and will be removed with BASS (037A). The player shows MIDI and KAR lyrics as subtitles itself.")]
public static class MidiLyricExtractor
{
    public static bool IsMidiFile(string path) => MidiLyricCueBuilder.IsMidiFile(path);

    /// <summary>
    /// Extracts lyric cues from an open BASS MIDI channel (created via Bass.MusicLoad).
    /// Returns null if the channel has no lyrics or is not a MIDI stream.
    /// </summary>
    public static IReadOnlyList<SubtitleCue>? ExtractLyrics(
        int channelHandle,
        string sourcePath,
        TimeSpan? transitionDuration = null) =>
        MidiLyricCueBuilder.ExtractLyrics(channelHandle, sourcePath, transitionDuration);
}
