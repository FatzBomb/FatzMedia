using FatzMedia.Video.Core.Subtitle;

#pragma warning disable IDE0130
namespace FatzMedia.Video;

/// <summary>
/// The subtitle tracks of the loaded media and the selected one, after they changed without a new
/// load (<see cref="IMediaControlSession.SubtitleTracksChanged"/>).
/// </summary>
public sealed class SubtitleTracksChangedEventArgs(IReadOnlyList<SubtitleTrack> tracks, string? selectedTrackId) : EventArgs
{
    /// <summary>Gets the available subtitle tracks, as <see cref="IMediaControlSession.SubtitleTracks"/> returns them now.</summary>
    public IReadOnlyList<SubtitleTrack> Tracks { get; } = tracks ?? throw new ArgumentNullException(nameof(tracks));

    /// <summary>Gets the selected track ID, or <see langword="null"/> when subtitles are off.</summary>
    public string? SelectedTrackId { get; } = selectedTrackId;
}
