using FatzBomb.MediaControl.Core.Abstractions;

namespace FatzBomb.MediaControl;

/// <summary>
/// The background video's own pause (owner decision 2026-11-06). By default a background video
/// loops on its own while it is shown, independent of the media;
/// <see cref="Core.Configuration.MediaPlayerConfiguration.BackgroundFollowsMedia"/> makes it follow
/// the media's transport instead. Either way these calls pause it explicitly.
/// </summary>
public partial class MediaControlPlayer
{
    private volatile bool _backgroundPaused;

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
    /// Ends an explicit background pause: the background plays again at once, or with the media when
    /// it follows the media's transport. Blocks; UI code uses <see cref="ResumeBackgroundAsync"/>.
    /// </summary>
    public bool ResumeBackground() => ExecuteControlOperation(() => SetBackgroundPaused(false));

    /// <summary>Ends an explicit background pause, on the control queue.</summary>
    public Task<bool> ResumeBackgroundAsync() => EnqueueControlOperation(() => SetBackgroundPaused(false));

    private bool SetBackgroundPaused(bool paused)
    {
        _backgroundPaused = paused;
        return ApplyBackgroundPause();
    }

    /// <summary>Applies the host's background pause to the current engine (also after a rebuild).</summary>
    private bool ApplyBackgroundPause()
    {
        if (_mediaEngine is not IBackgroundPlaybackControl control)
            return false;

        if (_backgroundPaused)
            return control.PauseBackground();

        return !control.IsBackgroundPaused || control.ResumeBackground();
    }
}
