using FatzMedia.Video.Core.Abstractions;
using FatzMedia.Video.Core.Audio;
using FatzMedia.Video.Core.Files;
using FatzMedia.Video.Core.Prepared;
using FatzMedia.Video.Core.Threading;
using Microsoft.Extensions.DependencyInjection;

#pragma warning disable IDE0130
namespace FatzMedia.Video;

/// <summary>
/// Depth-one prepared-next slot. The host owns its playlist; Core only keeps exactly one item
/// analyzed and ready so the transition into it is immediate.
/// </summary>
public partial class PlayerCore
{
    readonly PreparedNextSlot _preparedNext = new();
    volatile bool _autoTransitionToPreparedNext;

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
        // Called from the host's thread: the analysis and its diagnostics belong to this player.
        using var diagnosticOwner = DiagnosticRouter.EnterOwner(this);
        ArgumentNullException.ThrowIfNull(source);
        ThrowIfResetting();
        if (!source.IsFile)
            return null;

        return await AnalyzeFileAsync(source.FilePath!, cancellationToken).ConfigureAwait(false);
    }

    public Task<bool> PrepareNextAsync(string path, CancellationToken cancellationToken = default) =>
        PrepareNextAsync(MediaInputSource.FromFile(path), cancellationToken);

    public async Task<bool> PrepareNextAsync(MediaInputSource source, CancellationToken cancellationToken = default)
    {
        using var diagnosticOwner = DiagnosticRouter.EnterOwner(this);
        ArgumentNullException.ThrowIfNull(source);
        ThrowIfResetting();
        if (source.IsFile ? !System.IO.File.Exists(source.FilePath) : !source.IsBgraImage)
            return false;

        var ticket = _preparedNext.Begin(source, cancellationToken, out var accepted);
        PublishPreparedNextChange(accepted);

        MediaAnalysisResult? analysis = null;
        try
        {
            if (source.IsFile)
                analysis = await AnalyzeFileAsync(source.FilePath!, ticket.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (_preparedNext.TryFail(ticket.Generation, out var cancelled))
                PublishPreparedNextChange(cancelled);
            return false;
        }
        catch (Exception ex)
        {
            // Analysis is an optimization; the item stays preparable and the transition load
            // reports any real failure.
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Warning, "PreparedNext", $"Analysis failed: {ex.Message}", source.FilePath, ex);
        }

        if (!_preparedNext.TryComplete(ticket.Generation, analysis, out var ready))
            return false;

        PublishPreparedNextChange(ready);
        return true;
    }

    public bool ClearPreparedNext()
    {
        if (!_preparedNext.Clear(out var change))
            return false;

        PublishPreparedNextChange(change);
        return true;
    }

    public Task<bool> TransitionToPreparedNextAsync()
    {
        ThrowIfResetting();
        var fade = PreparedNextTransitionFade;
        if (!fade.IsEnabled)
            return EnqueueControlOperation(() => TransitionToPreparedNextCoreAsync(expectedGeneration: null, isAutomatic: false, fade: null));

        // The faded path waits off the queue; a pause, seek, stop, or load in the meantime
        // means the host changed course, so the transition is abandoned. It is pinned to the
        // item that is Ready now: replacing or clearing the slot during the fade abandons it too.
        var slotGeneration = _preparedNext.Generation;
        var transportGeneration = Controls.TransportGeneration;
        var loadGeneration = File.LoadGeneration;
        bool StillValid() =>
            !_disposed
            && Controls.TransportGeneration == transportGeneration
            && File.LoadGeneration == loadGeneration;

        // The transition runs off the queue in the flow started here.
        using var diagnosticOwner = DiagnosticRouter.EnterOwner(this);
        return RunFadedPreparedNextTransitionAsync(slotGeneration, isAutomatic: false, fade, fadeOutgoing: true, StillValid);
    }

    async Task<bool> TransitionToPreparedNextCoreAsync(long? expectedGeneration, bool isAutomatic, AudioCrossfadeOptions? fade)
    {
        if (!_preparedNext.TryBeginTransition(expectedGeneration, out var transition, out var claimed))
        {
            if (claimed is { } stale)
                PublishPreparedNextChange(stale);
            return false;
        }

        PublishPreparedNextChange(claimed!.Value);
        var success = false;
        // A Reset or Dispose meanwhile cancels the load (ENG-024).
        var lifetime = _loadLifetime.Token;
        try
        {
            if (fade is null)
            {
                success = await File.LoadAsync(transition!.Source, backgroundSource: null, ignoreSidecar: false, lifetime).ConfigureAwait(false);
                if (success && !File.PlayOnLoad)
                    success = Controls.Play();
            }
            else
            {
                success = File.LoadWithoutAutoPlay(transition!.Source, lifetime);
                if (success)
                {
                    PrimeIncomingFade(fade);
                    success = Controls.Play();
                }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            // The player is resetting or disposing.
        }
        catch (Exception ex)
        {
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Error, "PreparedNext", $"Transition failed: {ex.Message}", transition!.Source.FilePath, ex);
        }

        if (_preparedNext.TryCompleteTransition(transition!.Generation, out var completed))
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
    void ScheduleAutomaticPreparedNextTransition()
    {
        if (!_autoTransitionToPreparedNext || Controls.LoopMedia || !_preparedNext.IsReady())
            return;

        var slotGeneration = _preparedNext.Generation;
        var transportGeneration = Controls.TransportGeneration;
        var loadGeneration = File.LoadGeneration;
        var queue = _controlOperations;

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

                var fade = PreparedNextTransitionFade;
                if (!fade.IsEnabled)
                    return TransitionToPreparedNextCoreAsync(slotGeneration, isAutomatic: true, fade: null);

                // The outgoing media already ended, so only the incoming envelope applies. The
                // faded path waits off the queue and re-validates before its load step.
                using (ExecutionContext.SuppressFlow())
                {
                    _ = Task.Run(() =>
                    {
                        // Suppressing the flow also dropped the diagnostic owner: restore it.
                        using var diagnosticOwner = DiagnosticRouter.EnterOwner(this);
                        return RunFadedPreparedNextTransitionAsync(
                            slotGeneration,
                            isAutomatic: true,
                            fade,
                            fadeOutgoing: false,
                            StillValid);
                    });
                }

                return Task.FromResult(true);
            });
        });
    }

    /// <summary>Test hook: waits for the control operations queued so far, then for the diagnostics they raised.</summary>
    internal async Task FlushControlOperationsAsync()
    {
        await _controlOperations.Enqueue(static () => { }).ConfigureAwait(false);
        await _diagnosticEvents.FlushAsync().ConfigureAwait(false);
    }

    IMediaFileAnalyzer ResolveMediaFileAnalyzer() =>
        _serviceProvider.GetService<IMediaFileAnalyzer>() ?? UnavailableMediaFileAnalyzer.Instance;

    /// <summary>
    /// Runs the plain-audio analyzer off the caller's thread (opening and prescanning a file is
    /// synchronous work a UI thread must not do). Video, archives, images, and CDG files are not
    /// plain audio and are not analyzed.
    /// </summary>
    Task<MediaAnalysisResult?> AnalyzeFileAsync(string path, CancellationToken cancellationToken)
    {
        if (!IsAnalyzableAudioFile(path))
            return Task.FromResult<MediaAnalysisResult?>(null);

        var analyzer = ResolveMediaFileAnalyzer();
        return Task.Run(() => analyzer.AnalyzeAsync(path, cancellationToken), cancellationToken);
    }

    internal static bool IsAnalyzableAudioFile(string path) =>
        !FileTypes.IsVideo(path)
        && !FileTypes.IsArchive(path)
        && !FileTypes.IsImage(path)
        && !FileTypes.IsCdg(path);

    void PublishPreparedNextChange(PreparedNextChange change)
    {
        if (change.Previous == change.Current)
            return;

        // An FFmpeg-fallback item converted ahead of time is freed once it leaves the slot
        // (cleared, replaced, or stale); a transition has already taken its conversion.
        if (change.Previous.Source?.FilePath is { } previousPath
            && !string.Equals(previousPath, change.Current.Source?.FilePath, StringComparison.Ordinal))
            FfmpegAudioFallback.Release(previousPath);

        var args = new PreparedNextChangedEventArgs(change.Previous, change.Current);
        RaiseOnControlQueue(() => HostEventDispatcher.Raise(this, PreparedNextChanged, args, nameof(PreparedNextChanged)));
    }

    /// <summary>
    /// Raises inline when already on the control queue so slot events stay ordered with the load
    /// and transport events of the same operation; otherwise queues delivery.
    /// </summary>
    void RaiseOnControlQueue(Action raise)
    {
        if (_disposed)
            return;

        var queue = _controlOperations;
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

    void ClearPreparedNextForLifecycle()
    {
        if (_preparedNext.Clear(out var change))
            PublishPreparedNextChange(change);
    }
}
