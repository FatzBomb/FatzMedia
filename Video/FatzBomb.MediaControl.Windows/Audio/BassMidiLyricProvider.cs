using FatzBomb.MediaControl.Core.Abstractions;
using FatzBomb.MediaControl.Core.Audio;
using FatzBomb.MediaControl.Core.Subtitle;

namespace FatzBomb.MediaControl.Windows.Audio;

/// <summary>
/// Windows MIDI lyric provider backed by BASS marker APIs.
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
