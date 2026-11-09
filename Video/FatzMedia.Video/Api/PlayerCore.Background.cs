using FatzMedia.Video.Core.Abstractions;

namespace FatzMedia.Video;

/// <summary>
/// The background video's own pause (owner decision 2026-11-06). By default a background video
/// loops on its own while it is shown, independent of the media;
/// <see cref="Core.Configuration.MediaPlayerConfiguration.BackgroundFollowsMedia"/> makes it follow
/// the media's transport instead. Either way these calls pause it explicitly.
/// </summary>
public partial class PlayerCore
{
    volatile bool _backgroundPaused;

    /// <summary>
    /// Gets whether the background video is paused by <see cref="PauseBackground"/>. The pause holds
    /// across loads and mode switches until <see cref="ResumeBackground"/>.
    /// </summary>
    public bool IsBackgroundPaused => _backgroundPaused;

    /// <summary>
    /// Pauses the background video on its current frame until <see cref="ResumeBackground"/>.
    /// Blocks; UI code uses <see cref="PauseBackgroundAsync"/>.
    /// </summary>
    /// <returns><see langword="false"/> when the player has no background support.</returns>
    public bool PauseBackground() => ExecuteControlOperation(() => SetBackgroundPaused(true));

    /// <summary>Pauses the background video, on the control queue.</summary>
    public Task<bool> PauseBackgroundAsync() => EnqueueControlOperation(() => SetBackgroundPaused(true));

    /// <summary>
    /// Ends an explicit background pause and plays the background video now, whatever the media
    /// does, also while it is paused or stopped. When the background follows the media, the media's
    /// next pause or stop applies that rule again. Blocks; UI code uses <see cref="ResumeBackgroundAsync"/>.
    /// </summary>
    public bool ResumeBackground() => ExecuteControlOperation(() => SetBackgroundPaused(false));

    /// <summary>Ends an explicit background pause and plays the background, on the control queue.</summary>
    public Task<bool> ResumeBackgroundAsync() => EnqueueControlOperation(() => SetBackgroundPaused(false));

    bool SetBackgroundPaused(bool paused)
    {
        if (_mediaEngine is not IBackgroundPlaybackControl control)
            return false;

        _backgroundPaused = paused;
        return paused ? control.PauseBackground() : control.ResumeBackground();
    }

    /// <summary>After a rebuild: the new engine gets the host's pause again (a resume needs nothing).</summary>
    bool ApplyBackgroundPause() =>
        !_backgroundPaused || (_mediaEngine is IBackgroundPlaybackControl control && control.PauseBackground());
}
