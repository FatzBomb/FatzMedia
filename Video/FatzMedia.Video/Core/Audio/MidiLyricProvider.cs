using FatzMedia.Video.Core.Abstractions;
using FatzMedia.Video.Core.Subtitle;

namespace FatzMedia.Video.Core.Audio;

/// <summary>The MIDI lyric provider: cues through <see cref="MidiLyricCueBuilder"/>.</summary>
sealed class MidiLyricProvider : IMidiLyricProvider
{
    public bool IsMidiFile(string path) => MidiLyricCueBuilder.IsMidiFile(path);

    public IReadOnlyList<SubtitleCue>? ExtractLyrics(
        IMidiEventReader? reader,
        string sourcePath,
        TimeSpan transitionDuration) =>
        MidiLyricCueBuilder.ExtractLyrics(reader, sourcePath, transitionDuration);
}
