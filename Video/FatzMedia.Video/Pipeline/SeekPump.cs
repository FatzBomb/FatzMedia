using System.Diagnostics;

namespace FatzMedia.Video.Pipeline;

/// <summary>
/// What <see cref="SeekPump"/> asks of the pipeline it drives (Stage 015A slice 2; CDG uses it
/// too since ENG-013).
/// </summary>
interface ISeekPumpHost
{
    /// <summary>Clamps a requested position to the media (<see cref="VideoSeekPolicy.NormalizeTarget"/>).</summary>
    double NormalizeSeekTarget(double seconds);

    /// <summary>
    /// Runs one seek under the pipeline's transport gate. A preview leaves playback stopped; a
    /// settle resumes it if it was playing. Returns whether a video frame was presented.
    /// </summary>
    bool RunSeek(double target, bool asPreview, bool allowPreviewFallback);

    /// <summary>Presentation time of the frame on screen, or NaN when there is none.</summary>
    double CurrentFrameTime { get; }

    /// <summary>
    /// Waits for the adaptive pump interval between a preview and the next decision, returning
    /// early on shutdown. New requests that arrive meanwhile are picked up afterwards.
    /// </summary>
    void WaitForPumpInterval();
}

/// <summary>
/// The latest-wins seek pump, extracted from <c>VideoPipeline</c> without behaviour change
/// (Stage 015A slice 2) and shared with <c>CdgPipeline</c> (ENG-013). It owns the pending,
/// active, and minimum-committed targets, the "seek sequence" that makes Play during a seek
/// wait for the settle, and the preview-then-settle loop that runs on a dedicated thread:
/// <list type="bullet">
/// <item>A lone request previews exactly; once a second, different target arrives it is an
/// active scrub and previews may show a nearby frame.</item>
/// <item>When no newer request is waiting, the target settles. An active scrub settles on the
/// frame its last preview showed (035A).</item>
/// <item>A settle is skipped if a newer, meaningfully different request supersedes it.</item>
/// </list>
/// <see cref="DrainPendingRequests"/> runs one burst synchronously, so tests can replay
/// drags, holds, and releases without FFmpeg or threads.
/// </summary>
sealed class SeekPump : IDisposable
{
    const int ShutdownJoinMilliseconds = 2000;
    const int IntervalPollMilliseconds = 10;

    readonly ISeekPumpHost _host;
    readonly string _threadName;
    readonly string _diagnosticSource;
    readonly Lock _gate = new();
    readonly AutoResetEvent _signal = new(false);
    Thread? _thread;
    // A worker whose join timed out (its seek still held the transport gate). Start waits for it
    // while the shutdown flag is still set, so it cannot resume next to the new worker.
    Thread? _lingeringThread;
    volatile bool _shutdown;
    double? _pendingSeconds;
    double _activeTargetSeconds = double.NaN;

    // True from a request until its exact settle completes with nothing newer queued. A Play
    // in that window is applied by the settle, which resumes playback from the new position.

    /// <param name="host">The pipeline the pump drives.</param>
    /// <param name="threadName">Name of the worker thread, such as <c>VideoSeek</c>.</param>
    /// <param name="diagnosticSource">Source reported with seek failures, such as <c>VideoPipeline</c>.</param>
    public SeekPump(ISeekPumpHost host, string threadName, string diagnosticSource)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentException.ThrowIfNullOrWhiteSpace(threadName);
        ArgumentException.ThrowIfNullOrWhiteSpace(diagnosticSource);
        _host = host;
        _threadName = threadName;
        _diagnosticSource = diagnosticSource;
    }

    /// <summary>Clears every target and starts the worker thread (restarting it if running).</summary>
    public void Start()
    {
        Stop();
        if (_lingeringThread is { } lingering)
        {
            if (lingering.IsAlive && !lingering.Join(ShutdownJoinMilliseconds))
                DiagnosticRouter.Raise(MediaDiagnosticLevel.Warning, _diagnosticSource, $"The previous {_threadName} worker has not exited; starting a new one anyway.");

            _lingeringThread = null;
        }

        Reset();
        _shutdown = false;
        _thread = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = _threadName
        };
        _thread.Start();
    }

    /// <summary>
    /// Stops and joins the worker. Call without holding the pipeline's transport gate: an
    /// in-flight seek holds it, and joining lets that seek finish.
    /// </summary>
    public void Stop()
    {
        var thread = _thread;
        if (thread == null)
            return;

        _shutdown = true;
        _ = _signal.Set();
        try
        {
            if (!thread.Join(ShutdownJoinMilliseconds))
                _lingeringThread = thread;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[{_diagnosticSource}] Seek worker join failed: {ex.Message}");
        }

        _thread = null;
        Reset();
    }

    /// <summary>
    /// Makes the seek in progress give up at its next check (<see cref="HasSupersedingRequest"/>
    /// turns true) without joining the worker, as when the pipeline closes; <see cref="Stop"/>
    /// joins it and <see cref="Start"/> clears the flag (ENG-035).
    /// </summary>
    public void BeginShutdown()
    {
        _shutdown = true;
        _ = _signal.Set();
    }

    /// <summary>Queues a seek; it replaces any request not yet taken (latest wins).</summary>
    public void Request(double target)
    {
        lock (_gate)
        {
            _pendingSeconds = target;
            IsSequenceActive = true;
        }

        _ = _signal.Set();
    }

    /// <summary>Forgets every target, as when the pipeline stops or the worker restarts.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _pendingSeconds = null;
            IsSequenceActive = false;
            _activeTargetSeconds = double.NaN;
            MinimumCommittedTarget = double.NaN;
        }
    }

    /// <summary>Drops a request not yet taken, as when a loop restart repositions playback itself.</summary>
    public void CancelPendingRequest()
    {
        lock (_gate)
        {
            _pendingSeconds = null;
            IsSequenceActive = false;
            MinimumCommittedTarget = double.NaN;
        }
    }

    public bool HasPendingRequest
    {
        get
        {
            lock (_gate)
                return _pendingSeconds.HasValue;
        }
    }

    /// <summary>
    /// True when a newer request waits whose target differs meaningfully from the seek in
    /// progress (or no seek is in progress), or during shutdown.
    /// </summary>
    public bool HasSupersedingRequest
    {
        get
        {
            if (_shutdown)
                return true;

            lock (_gate)
                return _pendingSeconds.HasValue && (double.IsNaN(_activeTargetSeconds)
                    || VideoSeekPolicy.IsMeaningfullyDifferentTarget(_pendingSeconds.Value, _activeTargetSeconds));
        }
    }

    /// <summary>True from a request until its settle finishes with nothing newer queued.</summary>
    public bool IsSequenceActive
    {
        get
        {
            lock (_gate)
                return field;
        }

        private set;
    }

    /// <summary>Ends the seek sequence when no newer request is waiting (called at the end of a settle).</summary>
    public void EndSequenceIfSettled()
    {
        lock (_gate)
            if (!_pendingSeconds.HasValue)
                IsSequenceActive = false;
    }

    public void SetActiveTarget(double target)
    {
        lock (_gate)
            _activeTargetSeconds = target;
    }

    /// <summary>Clears the active target if it is still <paramref name="target"/> (within the change threshold).</summary>
    public void ClearActiveTarget(double target)
    {
        lock (_gate)
            if (!double.IsNaN(_activeTargetSeconds) && Math.Abs(_activeTargetSeconds - target) <= VideoSeekPolicy.TargetChangeThresholdSeconds)
                _activeTargetSeconds = double.NaN;
    }

    /// <summary>
    /// The earliest presentation time playback may show after a committed seek; frames before
    /// it are stale. NaN when no seek is committed.
    /// </summary>
    public double MinimumCommittedTarget
    {
        get
        {
            lock (_gate)
                return field;
        }

        private set;
    } = double.NaN;

    public void SetMinimumCommittedTarget(double target)
    {
        lock (_gate)
            MinimumCommittedTarget = target;
    }

    public void ClearMinimumCommittedTarget()
    {
        lock (_gate)
            MinimumCommittedTarget = double.NaN;
    }

    /// <summary>
    /// Waits up to <paramref name="intervalMilliseconds"/>, waking early on shutdown. Used by the
    /// pipeline's <see cref="ISeekPumpHost.WaitForPumpInterval"/>.
    /// </summary>
    public void WaitInterval(int intervalMilliseconds)
    {
        if (intervalMilliseconds <= 0)
            return;

        var started = Stopwatch.GetTimestamp();
        while (!_shutdown && Stopwatch.GetElapsedTime(started).TotalMilliseconds < intervalMilliseconds)
            _ = _signal.WaitOne(TimeSpan.FromMilliseconds(IntervalPollMilliseconds));
    }

    /// <summary>
    /// Processes requests until none is waiting: preview each changed target, then settle the
    /// last one. The worker thread runs this after every wake-up.
    /// </summary>
    internal void DrainPendingRequests()
    {
        var lastPreviewTarget = double.NaN;
        // The time of the frame an active scrub's preview put on screen. A scrub preview may
        // show a frame up to two seconds from the target; settling exactly on the target would
        // then resume a second or two from what the viewer is looking at, so the settle resumes
        // from the shown frame instead.
        var shownPreviewFrameTime = double.NaN;
        while (!_shutdown)
        {
            if (!TryTakePending(out var target))
                break;

            var normalizedTarget = _host.NormalizeSeekTarget(target);
            if (!VideoSeekPolicy.IsMeaningfullyDifferentTarget(normalizedTarget, lastPreviewTarget))
            {
                if (!HasPendingRequest)
                {
                    Settle(VideoSeekPolicy.GetScrubSettleTarget(normalizedTarget, shownPreviewFrameTime));
                    break;
                }

                continue;
            }

            // A lone click keeps preview exact so the displayed frame is the playback start.
            // Once a second changing target arrives, this is an active scrub: allow nearby
            // preview frames for responsive feedback; the settle then resumes from the frame
            // shown.
            var allowPreviewFallback = !double.IsNaN(lastPreviewTarget);
            var previewFramePresented = false;
            try
            {
                previewFramePresented = _host.RunSeek(normalizedTarget, asPreview: true, allowPreviewFallback);
            }
            catch (Exception ex)
            {
                DiagnosticRouter.Raise(MediaDiagnosticLevel.Warning, _diagnosticSource, $"Seek preview failed: {ex.Message}", innerException: ex);
            }

            lastPreviewTarget = normalizedTarget;
            shownPreviewFrameTime = allowPreviewFallback && previewFramePresented
                ? _host.CurrentFrameTime
                : double.NaN;
            if (!previewFramePresented && !HasPendingRequest)
            {
                Settle(normalizedTarget);
                break;
            }

            _host.WaitForPumpInterval();
            if (!HasPendingRequest)
            {
                Settle(VideoSeekPolicy.GetScrubSettleTarget(normalizedTarget, shownPreviewFrameTime));
                break;
            }
        }
    }

    public void Dispose()
    {
        Stop();
        _signal.Dispose();
    }

    void WorkerLoop()
    {
        while (!_shutdown)
        {
            _ = _signal.WaitOne();
            DrainPendingRequests();
        }
    }

    void Settle(double target)
    {
        if (_shutdown || HasSupersedingRequest)
            return;

        try
        {
            _ = _host.RunSeek(target, asPreview: false, allowPreviewFallback: false);
        }
        catch (Exception ex)
        {
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Warning, _diagnosticSource, $"Seek failed: {ex.Message}", innerException: ex);
        }
    }

    bool TryTakePending(out double target)
    {
        lock (_gate)
        {
            if (!_pendingSeconds.HasValue)
            {
                target = 0;
                return false;
            }

            target = _pendingSeconds.Value;
            _pendingSeconds = null;
            return true;
        }
    }
}
