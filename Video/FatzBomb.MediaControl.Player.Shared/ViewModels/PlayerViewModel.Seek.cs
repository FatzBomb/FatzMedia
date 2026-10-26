namespace FatzBomb.MediaControl.Player.Shared.ViewModels;

/// <summary>
/// Seek-bar scrubbing (Stage 035A): <see cref="BeginSeekPreview"/> when the drag starts, a
/// <see cref="SeekPositionSeconds"/> for every move, and <see cref="EndSeekPreview"/> when it ends.
/// The engine scrubs in between (preview frames, optional silent drag), so every host scrubs the
/// same way while dragging.
/// </summary>
public partial class PlayerViewModel
{
    /// <summary>
    /// Gets or sets where the seek bar's thumb is. While a seek preview is active, each change
    /// seeks there (newest request wins); otherwise it follows the playback position.
    /// </summary>
    public double SeekPositionSeconds
    {
        get;
        set
        {
            var target = DurationSeconds > 0 ? Math.Clamp(CleanSeconds(value), 0, DurationSeconds) : CleanSeconds(value);

            if (!SetProperty(ref field, target) || !_isSeekPreviewActive)
                return;

            PositionSeconds = target;
            SuppressPositionRefresh();
            QueueDragSeek(target);
        }
    }

    // Stage 022A slice 4: drag seeks run on the engine's control queue, not the UI thread;
    // SeekAsync keeps only the newest of the requests still waiting, so every move can send one.
    private void QueueDragSeek(double target)
    {
        if (Player is { } player)
            _ = SendDragSeekAsync(player, target);
    }

    private static async Task SendDragSeekAsync(MediaControlPlayer player, double target)
    {
        try
        {
            _ = await player.SeekAsync(target).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // Closed mid-drag: nothing left to seek.
        }
        catch (InvalidOperationException)
        {
            // Resetting mid-drag: the release seek reports any real problem.
        }
    }

    /// <summary>Starts a drag on the seek bar: the engine scrubs until <see cref="EndSeekPreview"/>.</summary>
    public void BeginSeekPreview()
    {
        if (!CanSeek) return;
        _isSeekPreviewActive = true;
        _ = Player?.BeginScrub();
    }

    /// <summary>Ends the drag: seeks to <see cref="SeekPositionSeconds"/> and ends the scrub.</summary>
    public void EndSeekPreview()
    {
        _isSeekPreviewActive = false;
        SuppressPositionRefresh();
        _ = Player?.Seek(SeekPositionSeconds);
        _ = Player?.EndScrub();
    }

    /// <summary>The drag was interrupted (for example the pointer capture was lost): ends it where it is.</summary>
    public void CancelSeekPreview()
        => EndSeekPreview();
}
