namespace FatzMedia.Video.Core.Abstractions;

/// <summary>
/// Engine support for subtitle tracks that arrive after the open (ENG-039 P5-1): the embedded text
/// tracks of a local video are decoded on a background task.
/// </summary>
internal interface ISubtitleTrackSource
{
    /// <summary>
    /// Raised on a background thread when <see cref="IMediaEngine.SubtitleTracks"/> or the selected
    /// track changed after the open, for example when the embedded track scan finished.
    /// </summary>
    event EventHandler? SubtitleTracksChanged;
}
