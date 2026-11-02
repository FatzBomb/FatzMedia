using FatzBomb.MediaControl.Core.Audio;
using FatzBomb.MediaControl.Core.Subtitle;

namespace FatzBomb.MediaControl.Core.Abstractions;

/// <summary>
/// Supplies karaoke lyric cues embedded in a MIDI source. Platform implementations
/// own any native marker/metadata APIs; Core orchestration consumes only cues.
/// </summary>
public interface IMidiLyricProvider
{
    bool IsMidiFile(string path);

    /// <summary>
    /// Extracts the lyric cues of <paramref name="sourcePath"/>. <paramref name="sourceHandle"/> is
    /// the BASS source channel of the open media (0 when there is none). The built-in provider
    /// reads MIDI through an engine-neutral reader instead (037A slice 1); a host-supplied provider
    /// is still called with the handle.
    /// </summary>
    IReadOnlyList<SubtitleCue>? ExtractLyrics(
        int sourceHandle,
        string sourcePath,
        TimeSpan transitionDuration);
}

/// <summary>
/// A <see cref="IMidiLyricProvider"/> that builds cues from an engine-neutral
/// <see cref="IMidiEventReader"/> (037A slice 1), so the player needs no audio engine handle.
/// </summary>
internal interface IMidiLyricReaderProvider : IMidiLyricProvider
{
    /// <summary>
    /// Extracts the lyric cues <paramref name="reader"/> reads; <see langword="null"/> when there is
    /// no reader or no lyrics.
    /// </summary>
    IReadOnlyList<SubtitleCue>? ExtractLyrics(
        IMidiEventReader? reader,
        string sourcePath,
        TimeSpan transitionDuration);
}
