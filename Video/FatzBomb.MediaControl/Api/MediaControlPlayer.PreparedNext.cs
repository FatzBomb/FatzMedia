using FatzBomb.MediaControl.Core.Abstractions;
using FatzBomb.MediaControl.Core.Prepared;
using FatzBomb.MediaControl.Core.Threading;
using Microsoft.Extensions.DependencyInjection;

#pragma warning disable IDE0130
namespace FatzBomb.MediaControl;

/// <summary>
/// Depth-one prepared-next slot. The host owns its playlist; Core only keeps exactly one item
/// analyzed and ready so the transition into it is immediate.
/// </summary>
public partial class MediaControlPlayer
{
    private readonly PreparedNextSlot _preparedNext = new();
    private volatile bool _autoTransitionToPreparedNext;

    public event EventHandler<PreparedNextChangedEventArgs>? PreparedNextChanged;

    public event EventHandler<PreparedNextTransitionedEventArgs>? PreparedNextTransitioned;

    public PreparedNextInfo PreparedNext => _preparedNext.Snapshot;

    public bool AutoTransitionToPreparedNext
    {
        get => _autoTransitionToPreparedNext;
        set => _autoTransitionToPreparedNext = value;
    }

    public Task<MediaAnalysisResult?> AnalyzeAsync(string path, CancellationToken cancellationToken = default) =>
        AnalyzeAsync(MediaInputSource.FromFile(path), cancellationToken);

    public async Task<MediaAnalysisResult?> AnalyzeAsync(MediaInputSource source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ThrowIfResetting();
        if (!source.IsFile)
            return null;

        IMediaFileAnalyzer analyzer = ResolveMediaFileAnalyzer();
        return await analyzer.AnalyzeAsync(source.FilePath!, cancellationToken).ConfigureAwait(false);
    }

    public Task<bool> PrepareNextAsync(string path, CancellationToken cancellationToken = default) =>
        PrepareNextAsync(MediaInputSource.FromFile(path), cancellationToken);

    public async Task<bool> PrepareNextAsync(MediaInputSource source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ThrowIfResetting();
        if (source.IsFile ? !System.IO.File.Exists(source.FilePath) : !source.IsBgraImage)
            return false;

        IMediaFileAnalyzer analyzer = ResolveMediaFileAnalyzer();
        PreparedNextTicket ticket = _preparedNext.Begin(source, cancellationToken, out PreparedNextChange accepted);
        PublishPreparedNextChange(accepted);

        MediaAnalysisResult? analysis = null;
        try
        {
            if (source.IsFile)
                analysis = await analyzer.AnalyzeAsync(source.FilePath!, ticket.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (_preparedNext.TryFail(ticket.Generation, out PreparedNextChange cancelled))
                PublishPreparedNextChange(cancelled);
            return false;
        }
        catch (Exception ex)
        {
            // Analysis is an optimization; the item stays preparable and the transition load
            // reports any real failure.
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Warning, "PreparedNext", $"Analysis failed: {ex.Message}", source.FilePath, ex);
        }

        if (!_preparedNext.TryComplete(ticket.Generation, analysis, out PreparedNextChange ready))
            return false;

        PublishPreparedNextChange(ready);
        return true;
    }

    public bool ClearPreparedNext()
    {
        if (!_preparedNext.Clear(out PreparedNextChange change))
            return false;

        PublishPreparedNextChange(change);
        return true;
    }

    public Task<bool> TransitionToPreparedNextAsync()
    {
        ThrowIfResetting();
        AudioCrossfadeOptions fade = PreparedNextTransitionFade;
        if (!fade.IsEnabled)
            return EnqueueControlOperation(() => TransitionToPreparedNextCoreAsync(expectedGeneration: null, isAutomatic: false, fade: null));

        // The faded path waits off the queue; a pause, seek, stop, or load in the meantime
        // means the host changed course, so the transition is abandoned.
        var transportGeneration = Controls.TransportGeneration;
        var loadGeneration = File.LoadGeneration;
        bool StillValid() =>
            !_disposed
            && Controls.TransportGeneration == transportGeneration
            && File.LoadGeneration == loadGeneration;

        return RunFadedPreparedNextTransitionAsync(expectedGeneration: null, isAutomatic: false, fade, fadeOutgoing: true, StillValid);
    }

    private async Task<bool> TransitionToPreparedNextCoreAsync(long? expectedGeneration, bool isAutomatic, AudioCrossfadeOptions? fade)
    {
        if (!_preparedNext.TryBeginTransition(expectedGeneration, out PreparedNextTransition? transition, out PreparedNextChange? claimed))
        {
            if (claimed is { } stale)
                PublishPreparedNextChange(stale);
            return false;
        }

        PublishPreparedNextChange(claimed!.Value);
        var success = false;
        try
        {
            if (fade is null)
            {
                success = await File.LoadAsync(transition!.Source).ConfigureAwait(false);
                if (success && !File.PlayOnLoad)
                    success = Controls.Play();
            }
            else
            {
                success = File.LoadWithoutAutoPlay(transition!.Source);
                if (success)
                {
                    PrimeIncomingFade(fade);
                    success = Controls.Play();
                }
            }
        }
        catch (Exception ex)
        {
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Error, "PreparedNext", $"Transition failed: {ex.Message}", transition!.Source.FilePath, ex);
        }

        if (_preparedNext.TryCompleteTransition(transition!.Generation, out PreparedNextChange completed))
            PublishPreparedNextChange(completed);

        if (success)
        {
            RaiseOnControlQueue(() => HostEventDispatcher.Raise(
                this,
                PreparedNextTransitioned,
                new PreparedNextTransitionedEventArgs(transition.Source, transition.Analysis, isAutomatic),
                nameof(PreparedNextTransitioned)));
        }

        return success;
    }

    /// <summary>
    /// Called on the control queue while natural media end is being delivered. The transition is
    /// queued behind both the host <see cref="MediaEnded"/> delivery and Core's end completion, and
    /// it is skipped when the host changed transport, loaded other media, enabled looping, disabled
    /// auto transition, or replaced/cleared the prepared item in the meantime.
    /// </summary>
    private void ScheduleAutomaticPreparedNextTransition()
    {
        if (!_autoTransitionToPreparedNext || Controls.LoopMedia || !_preparedNext.IsReady())
            return;

        var slotGeneration = _preparedNext.Generation;
        var transportGeneration = Controls.TransportGeneration;
        var loadGeneration = File.LoadGeneration;
        MediaControlOperationQueue queue = _controlOperations;

        bool StillValid() =>
            !_disposed
            && _autoTransitionToPreparedNext
            && !Controls.LoopMedia
            && Controls.TransportGeneration == transportGeneration
            && File.LoadGeneration == loadGeneration
            && _preparedNext.IsReady(slotGeneration);

        // First hop lands after the MediaEnded delivery; the second hop lands after Core's
        // CompleteMediaEnd, which BeginMediaEnd queues after raising MediaEnded.
        _ = queue.Enqueue(() =>
        {
            _ = queue.Enqueue(() =>
            {
                if (!StillValid())
                    return Task.FromResult(false);

                AudioCrossfadeOptions fade = PreparedNextTransitionFade;
                if (!fade.IsEnabled)
                    return TransitionToPreparedNextCoreAsync(slotGeneration, isAutomatic: true, fade: null);

                // The outgoing media already ended, so only the incoming envelope applies. The
                // faded path waits off the queue and re-validates before its load step.
                using (ExecutionContext.SuppressFlow())
                {
                    _ = Task.Run(() => RunFadedPreparedNextTransitionAsync(
                        slotGeneration,
                        isAutomatic: true,
                        fade,
                        fadeOutgoing: false,
                        StillValid));
                }

                return Task.FromResult(true);
            });
        });
    }

    /// <summary>Queues an empty operation; completes once all previously queued work ran.</summary>
    internal Task FlushControlOperationsAsync() => _controlOperations.Enqueue(static () => { });

    private IMediaFileAnalyzer ResolveMediaFileAnalyzer() =>
        _serviceProvider.GetService<IMediaFileAnalyzer>() ?? UnavailableMediaFileAnalyzer.Instance;

    private void PublishPreparedNextChange(PreparedNextChange change)
    {
        if (change.Previous == change.Current)
            return;

        var args = new PreparedNextChangedEventArgs(change.Previous, change.Current);
        RaiseOnControlQueue(() => HostEventDispatcher.Raise(this, PreparedNextChanged, args, nameof(PreparedNextChanged)));
    }

    /// <summary>
    /// Raises inline when already on the control queue so slot events stay ordered with the load
    /// and transport events of the same operation; otherwise queues delivery.
    /// </summary>
    private void RaiseOnControlQueue(Action raise)
    {
        if (_disposed)
            return;

        MediaControlOperationQueue queue = _controlOperations;
        if (queue.IsExecutingOperation)
        {
            raise();
            return;
        }

        try
        {
            _ = queue.Enqueue(raise);
        }
        catch (ObjectDisposedException)
        {
            // The player is resetting or disposing; slot notifications are dropped with it.
        }
        catch (InvalidOperationException)
        {
            // The queue stopped accepting work during teardown.
        }
    }

    private void ClearPreparedNextForLifecycle()
    {
        if (_preparedNext.Clear(out PreparedNextChange change))
            PublishPreparedNextChange(change);
    }
}
