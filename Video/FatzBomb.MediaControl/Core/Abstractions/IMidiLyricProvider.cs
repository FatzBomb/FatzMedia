using FatzBomb.MediaControl.Core.Subtitle;

namespace FatzBomb.MediaControl.Core.Abstractions;

/// <summary>
/// Supplies karaoke lyric cues embedded in a MIDI source. Platform implementations
/// own any native marker/metadata APIs; Core orchestration consumes only cues.
/// </summary>
public interface IMidiLyricProvider
{
    bool IsMidiFile(string path);

    IReadOnlyList<SubtitleCue>? ExtractLyrics(
        int sourceHandle,
        string sourcePath,
        TimeSpan transitionDuration);
}
