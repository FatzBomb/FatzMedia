namespace FatzMedia.Video.Sample.Player.Shared.ViewModels;

/// <summary>
/// Seek-bar scrubbing (Stage 035A): <see cref="BeginSeekPreview"/> when the drag starts, a
/// <see cref="SeekPositionSeconds"/> for every move, and <see cref="EndSeekPreview"/> when it ends.
/// The engine scrubs in between (preview frames, optional silent drag), so every host scrubs the
/// same way while dragging.
/// </summary>
/// <remarks>
/// <see cref="PlayerCore.BeginScrub"/> and <see cref="PlayerCore.EndScrub"/> wait
/// for the operations queued before them (a network seek can take seconds), so they run in order on
/// a "scrub lane" off the UI thread. Drag seeks use <see cref="PlayerCore.SeekAsync"/>; one
/// made while the lane is busy waits for it (only the newest), so it never overtakes the
/// <c>BeginScrub</c> of its own drag or the release of the previous one.
/// </remarks>
public partial class PlayerViewModel
{
    readonly Lock _deferredSeekGate = new();
    // UI thread only.
    Task _scrubLane = Task.CompletedTask;
    // Guarded by _deferredSeekGate: the newest drag target waiting for the lane.
    double? _deferredSeekTarget;

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

    /// <summary>Starts a drag on the seek bar: the engine scrubs until <see cref="EndSeekPreview"/>.</summary>
    public void BeginSeekPreview()
    {
        if (!CanSeek || Player is not { } player) return;
        _isSeekPreviewActive = true;
        EnqueueScrubCall(() =>
        {
            RunScrubCall(() => player.BeginScrub());
            return Task.CompletedTask;
        });
    }

    /// <summary>Ends the drag: seeks to <see cref="SeekPositionSeconds"/> and ends the scrub.</summary>
    public void EndSeekPreview()
    {
        if (!_isSeekPreviewActive)
            return;

        _isSeekPreviewActive = false;
        SuppressPositionRefresh();
        if (Player is not { } player)
            return;

        var target = SeekPositionSeconds;
        EnqueueScrubCall(async () =>
        {
            try
            {
                // The release seek first: it is ignored when it repeats the last drag position.
                await SendDragSeekAsync(player, target).ConfigureAwait(false);
            }
            finally
            {
                // Always end the scrub, or the engine stays muted and treats later seeks as drag seeks.
                RunScrubCall(() => player.EndScrub());
            }
        });
    }

    /// <summary>The drag was interrupted (for example the pointer capture was lost): ends it where it is.</summary>
    public void CancelSeekPreview()
        => EndSeekPreview();

    /// <summary>
    /// Seeks once, outside a drag (a click on the bar with no drag, or a key step), and shows the
    /// target until the engine gets there.
    /// </summary>
    public void SeekTo(double seconds)
    {
        if (!CanSeek || _isSeekPreviewActive)
            return;

        SeekPositionSeconds = seconds;
        PositionSeconds = SeekPositionSeconds;
        SuppressPositionRefresh();
        QueueDragSeek(SeekPositionSeconds);
    }

    // Stage 022A slice 4: drag seeks run on the engine's control queue, not the UI thread;
    // SeekAsync keeps only the newest of the requests still waiting, so every move can send one.
    void QueueDragSeek(double target)
    {
        if (Player is not { } player)
            return;

        if (_scrubLane.IsCompleted)
        {
            _ = SendDragSeekAsync(player, target);
            return;
        }

        bool scheduled;
        lock (_deferredSeekGate)
        {
            scheduled = _deferredSeekTarget is not null;
            _deferredSeekTarget = target;
        }

        if (scheduled)
            return;

        EnqueueScrubCall(() =>
        {
            double? deferred;
            lock (_deferredSeekGate)
            {
                deferred = _deferredSeekTarget;
                _deferredSeekTarget = null;
            }

            // Not awaited: later drag seeks coalesce with it on the engine's queue.
            if (deferred is { } seconds)
                _ = SendDragSeekAsync(player, seconds);
            return Task.CompletedTask;
        });
    }

    void EnqueueScrubCall(Func<Task> call) =>
        _scrubLane = _scrubLane
            .ContinueWith(_ => call(), CancellationToken.None, TaskContinuationOptions.DenyChildAttach, TaskScheduler.Default)
            .Unwrap();

    void RunScrubCall(Action call)
    {
        try
        {
            call();
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
        {
            // Closed or resetting mid-drag: there is no scrub left to begin or end.
        }
        catch (Exception ex)
        {
            HandleDiagnosticOnUiThread(MediaDiagnosticLevel.Warning, "Player", $"Scrub failed: {ex.Message}");
        }
    }

    // Fire-and-forget from the drag handlers: every failure is handled here, never left unobserved.
    async Task SendDragSeekAsync(PlayerCore player, double target)
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
        catch (Exception ex)
        {
            HandleDiagnosticOnUiThread(MediaDiagnosticLevel.Warning, "Player", $"Seek failed: {ex.Message}");
        }
    }
}
