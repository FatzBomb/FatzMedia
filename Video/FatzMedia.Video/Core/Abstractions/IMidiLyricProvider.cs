using FatzMedia.Video.Core.Audio;
using FatzMedia.Video.Core.Subtitle;

namespace FatzMedia.Video.Core.Abstractions;

/// <summary>
/// Supplies the karaoke lyric cues embedded in a MIDI source, from the MIDI events the audio engine reads
/// (<see cref="IMidiEventReader"/>).
/// </summary>
interface IMidiLyricProvider
{
    bool IsMidiFile(string path);

    /// <summary>
    /// Extracts the lyric cues <paramref name="reader"/> reads; <see langword="null"/> when there is
    /// no reader or no lyrics.
    /// </summary>
    IReadOnlyList<SubtitleCue>? ExtractLyrics(
        IMidiEventReader? reader,
        string sourcePath,
        TimeSpan transitionDuration);
}
