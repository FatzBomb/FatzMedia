using FatzMedia.Video.Core.Subtitle;

namespace FatzMedia.Video.Pipeline;

/// <summary>
/// Embedded subtitle scan (ENG-039 P5-1). Decoding the text subtitle tracks of a local video reads
/// the whole file, so it runs on a background task after the video opened instead of inside the
/// open. A newer open, a close or dispose cancels it, and a generation check under
/// <see cref="_subtitleScanGate"/> drops a result that arrives after that.
/// </summary>
sealed partial class PlaybackCoordinator
{
    readonly Func<string, CancellationToken, IReadOnlyList<EmbeddedSubtitleTrack>> _decodeEmbeddedSubtitles;

    // Guards the scan state below, and orders a late result against user selections and the
    // cancellation of a newer open: once CancelEmbeddedSubtitleScan returns, no stale result applies.
    readonly Lock _subtitleScanGate = new();
    CancellationTokenSource? _subtitleScan;
    int _subtitleScanGeneration;

    // The host selected a track (or none) since the video opened: a late scan result never overrides it.
    bool _subtitleTrackChosen;

    // A track the host asked for before the scan delivered it (an embedded id selected on
    // MediaLoaded, as hosts restoring a preference do): the scan selects it when it arrives.
    string? _requestedSubtitleTrackId;

    /// <summary>
    /// Raised on the scan's background thread when embedded subtitle tracks arrived after the open
    /// and were added to <see cref="SubtitleTracks"/> (and, unless the host chose a track, selected).
    /// </summary>
    public event EventHandler? SubtitleTracksChanged;

    /// <summary>The scan in progress or last finished, for tests; never faults.</summary>
    internal Task EmbeddedSubtitleScan
    {
        get
        {
            lock (_subtitleScanGate)
                return field;
        }

        private set;
    } = Task.CompletedTask;

    public bool SelectSubtitleTrack(string? trackId)
    {
        lock (_subtitleScanGate)
        {
            var selected = _session.Subtitle?.SelectTrack(trackId) == true;
            if (selected)
            {
                _subtitleTrackChosen = true;
                _requestedSubtitleTrackId = null;
            }
            else if (trackId is not null && !EmbeddedSubtitleScan.IsCompleted)
            {
                _requestedSubtitleTrackId = trackId;
            }

            return selected;
        }
    }

    /// <summary>
    /// Cancels the embedded subtitle scan of the current video, if any. Its result, even one
    /// already decoded, is dropped. Called by every open, close and dispose.
    /// </summary>
    public void CancelEmbeddedSubtitleScan()
    {
        lock (_subtitleScanGate)
        {
            _subtitleScanGeneration++;
            if (_subtitleScan is not { } scan)
                return;

            _subtitleScan = null;
            scan.Cancel();
            scan.Dispose();
        }
    }

    void StartEmbeddedSubtitleScan(string videoPath)
    {
        // The scan's diagnostics belong to the player that opened the video.
        var owner = DiagnosticRouter.Owner;
        lock (_subtitleScanGate)
        {
            _subtitleScan?.Cancel();
            _subtitleScan?.Dispose();
            var scan = new CancellationTokenSource();
            _subtitleScan = scan;
            var generation = ++_subtitleScanGeneration;
            _subtitleTrackChosen = false;
            _requestedSubtitleTrackId = null;
            var token = scan.Token;

            // The open runs on the player's control queue: its scope and the load's cancellation
            // must not flow into the scan, which re-enters only the diagnostic owner.
            using (ExecutionContext.SuppressFlow())
            {
                EmbeddedSubtitleScan = Task.Factory.StartNew(
                    () => ScanEmbeddedSubtitles(videoPath, generation, owner, token),
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
                    TaskScheduler.Default);
            }
        }
    }

    void ScanEmbeddedSubtitles(string videoPath, int generation, object? owner, CancellationToken cancellationToken)
    {
        using var ownerScope = DiagnosticRouter.EnterOwner(owner);
        IReadOnlyList<EmbeddedSubtitleTrack> tracks;
        try
        {
            tracks = _decodeEmbeddedSubtitles(videoPath, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            if (!cancellationToken.IsCancellationRequested)
                DiagnosticRouter.Raise(MediaDiagnosticLevel.Warning, "Subtitle", $"Failed to decode embedded subtitles: {ex.Message}", sourcePath: videoPath, innerException: ex);
            return;
        }

        if (tracks.Count == 0)
            return;

        lock (_subtitleScanGate)
        {
            if (generation != _subtitleScanGeneration || _session.Subtitle is not { } subtitle)
                return;

            if (!subtitle.LoadEmbeddedTracks(tracks, selectFirstTrack: false))
                return;

            // Embedded tracks take precedence over a sidecar, as when they were decoded during the
            // open; the sidecar stays selectable. A track the host chose is kept, and one it asked
            // for before it arrived is selected.
            if (!_subtitleTrackChosen)
            {
                var requested = _requestedSubtitleTrackId;
                _requestedSubtitleTrackId = null;
                if (requested is null || !subtitle.SelectTrack(requested))
                    _ = subtitle.SelectTrack(SubtitlePipeline.EmbeddedTrackId(tracks[0].StreamIndex));
            }
        }

        SubtitleTracksChanged?.Invoke(this, EventArgs.Empty);
    }
}
