using FatzMedia.Video.Core.Abstractions;
using FatzMedia.Video.Core.Subtitle;

namespace FatzMedia.Video.Core.Audio;

/// <summary>
/// Default MIDI lyric provider through <see cref="MidiLyricCueBuilder"/>: from an engine-neutral
/// <see cref="IMidiEventReader"/> (037A slice 1), or from a BASS source handle as before.
/// </summary>
internal sealed class BassMidiLyricProvider : IMidiLyricReaderProvider
{
    public bool IsMidiFile(string path) => MidiLyricCueBuilder.IsMidiFile(path);

    public IReadOnlyList<SubtitleCue>? ExtractLyrics(
        int sourceHandle,
        string sourcePath,
        TimeSpan transitionDuration) =>
        MidiLyricCueBuilder.ExtractLyrics(sourceHandle, sourcePath, transitionDuration);

    public IReadOnlyList<SubtitleCue>? ExtractLyrics(
        IMidiEventReader? reader,
        string sourcePath,
        TimeSpan transitionDuration) =>
        MidiLyricCueBuilder.ExtractLyrics(reader, sourcePath, transitionDuration);
}
