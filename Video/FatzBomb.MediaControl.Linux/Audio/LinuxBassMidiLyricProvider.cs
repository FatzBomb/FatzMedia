using FatzBomb.MediaControl.Core.Abstractions;
using FatzBomb.MediaControl.Core.Audio;
using FatzBomb.MediaControl.Core.Subtitle;

namespace FatzBomb.MediaControl.Linux.Audio;

/// <summary>MIDI/KAR lyric provider backed by BASS marker APIs (same Core extractor as Windows).</summary>
internal sealed class LinuxBassMidiLyricProvider : IMidiLyricProvider
{
    public bool IsMidiFile(string path) => MidiLyricExtractor.IsMidiFile(path);

    public IReadOnlyList<SubtitleCue>? ExtractLyrics(int sourceHandle, string sourcePath, TimeSpan transitionDuration) =>
        MidiLyricExtractor.ExtractLyrics(sourceHandle, sourcePath, transitionDuration);
}
