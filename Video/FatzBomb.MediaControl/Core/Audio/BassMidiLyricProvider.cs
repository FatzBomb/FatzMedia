using FatzBomb.MediaControl.Core.Abstractions;
using FatzBomb.MediaControl.Core.Subtitle;

namespace FatzBomb.MediaControl.Core.Audio;

/// <summary>
/// Default MIDI lyric provider backed by BASS marker APIs through <see cref="MidiLyricExtractor"/>.
/// </summary>
internal sealed class BassMidiLyricProvider : IMidiLyricProvider
{
    public bool IsMidiFile(string path) => MidiLyricExtractor.IsMidiFile(path);

    public IReadOnlyList<SubtitleCue>? ExtractLyrics(
        int sourceHandle,
        string sourcePath,
        TimeSpan transitionDuration) =>
        MidiLyricExtractor.ExtractLyrics(sourceHandle, sourcePath, transitionDuration);
}
