using FatzMedia.Video.Core.Subtitle;

namespace FatzMedia.Video.Core.Abstractions;

/// <summary>
/// UI-agnostic timed subtitle cue source.
/// </summary>
public interface ISubtitleEngine : IDisposable
{
    bool IsOpen { get; }
    IReadOnlyList<SubtitleCue> GetActiveCues(double positionSeconds);

    void Load(string filePath);
    void LoadCues(IReadOnlyList<SubtitleCue> cues);
    void Close();

    event EventHandler? CuesChanged;
}
