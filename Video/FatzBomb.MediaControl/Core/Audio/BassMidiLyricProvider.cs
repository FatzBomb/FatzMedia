using FatzBomb.MediaControl.Core.Abstractions;
using FatzBomb.MediaControl.Core.Subtitle;

namespace FatzBomb.MediaControl.Core.Audio;

/// <summary>
/// Default MIDI lyric provider backed by BASS marker APIs through <see cref="MidiLyricCueBuilder"/>.
/// </summary>
internal sealed class BassMidiLyricProvider : IMidiLyricProvider
{
    public bool IsMidiFile(string path) => MidiLyricCueBuilder.IsMidiFile(path);

    public IReadOnlyList<SubtitleCue>? ExtractLyrics(
        int sourceHandle,
        string sourcePath,
        TimeSpan transitionDuration) =>
        MidiLyricCueBuilder.ExtractLyrics(sourceHandle, sourcePath, transitionDuration);
}
