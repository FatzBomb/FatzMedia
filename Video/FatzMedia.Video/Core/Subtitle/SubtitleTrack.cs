namespace FatzMedia.Video.Core.Subtitle;

/// <summary>
/// A selectable subtitle source for the current media item.
/// </summary>
public sealed record SubtitleTrack(string Id, string DisplayName, bool IsEmbedded);

internal sealed record EmbeddedSubtitleTrack(int StreamIndex, string CodecName, IReadOnlyList<SubtitleCue> Cues);
