using System.Diagnostics;
using FatzMedia.Video.Core.Abstractions;
using FatzMedia.Video.Core.Graphics.Composition;
using FatzMedia.Video.Core.Graphics.Rendering;

namespace FatzMedia.Video.Controllers;

/// <summary>
/// ENG-039: finds a lost renderer device without waiting for a present to report it, recovers it,
/// and reports a renderer call that never returns. The GPU-reset smoke showed every renderer
/// failing silently: the loss surfaced only as a Composition warning (or not at all), and nothing
/// recovered.
/// </summary>
public sealed partial class MediaPresentationCoordinator
{
    /// <summary>How often the renderer is asked whether its device is lost.</summary>
    internal static readonly TimeSpan DeviceHealthInterval = TimeSpan.FromSeconds(1);

    /// <summary>A failed present asks again at most this often.</summary>
    internal static readonly TimeSpan DeviceProbeKickInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>A renderer call running this long is reported as blocked, once per call.</summary>
    internal static readonly TimeSpan RendererStallThreshold = TimeSpan.FromSeconds(3);

    /// <summary>After a recovery gave up, it is tried again this often while a surface is attached.</summary>
    internal static readonly TimeSpan RecoveryRetryInterval = TimeSpan.FromSeconds(10);

    /// <summary>
    /// A surface call presents on its own thread, unless a renderer call has been running this long:
    /// then the present is left to the thread pool so the UI thread does not wait behind it.
    /// </summary>
    static readonly TimeSpan SurfacePresentDeferThreshold = TimeSpan.FromMilliseconds(250);

    static readonly TimeSpan[] DefaultRecoveryBackoff =
    [
        TimeSpan.FromMilliseconds(500),
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(4),
        TimeSpan.FromSeconds(8),
    ];

    const string DeviceRecoveryDiagnosticCategory = "DeviceRecovery";
    const string PresentationDiagnosticCategory = "Presentation";

    readonly TimeProvider _timeProvider;
    // Calls on the presentation threads (presents, under _presentGate) and on the surface threads
    // (attach, resize, initialize, under _lock), each timed for the stall detector.
    readonly RendererCallTracker _renderCalls = new();
    readonly RendererCallTracker _surfaceCalls = new();
    // A refused native video frame, reported once per episode like a refused composition.
    readonly RendererDiagnosticLatch _nativePresentFailure = new();
    // Created under _lock once the renderer first initialized; disposed by Dispose.
    ITimer? _deviceHealthTimer;
    int _deviceHealthTickRunning;
    long _lastDeviceProbeKickPlusOne;
    // Set when a recovery gave up (timestamp + 1); cleared by a successful one.
    long _recoveryFailedAtPlusOne;
    int _deferredSurfacePresentQueued;
    // Set by a failed attach: the next tick probes even with no surface attached (ENG-039).
    int _probeAfterFailedAttach;

    /// <summary>
    /// Raised on the recovery thread after the renderer's device was lost and recovered: frames
    /// decoded on the lost device are gone, so the player reopens its video (ENG-039).
    /// </summary>
    public event EventHandler? DeviceRecovered;

    /// <inheritdoc />
    public event EventHandler? DeviceRecoveryFailed;

    /// <summary>
    /// The pauses between recovery attempts; one attempt more than pauses. Tests shorten them.
    /// </summary>
    internal TimeSpan[] RecoveryBackoff { get; set; } = DefaultRecoveryBackoff;

    /// <summary>Gets whether a device recovery is running (tests).</summary>
    internal bool IsRecoveryRunning => Volatile.Read(ref _recoveryInProgress) != 0;

    bool HasAttachedSurfaces => Volatile.Read(ref _surfaceSnapshot).Length > 0;

    /// <summary>A recovery runs, or gave up and waits to retry: hardware frames refer to the lost device.</summary>
    bool IsRecoveringDevice =>
        Volatile.Read(ref _recoveryInProgress) != 0 || Volatile.Read(ref _recoveryFailedAtPlusOne) != 0;

    /// <summary>
    /// Starts recovering the renderer's device: the single entry point of every trigger (the health
    /// probe, a failed present, the device-lost diagnostic). Logs one Error under the player's
    /// owner. Does nothing while a recovery runs or after Dispose.
    /// </summary>
    internal void RequestDeviceLostRecovery(string reason) => StartDeviceLostRecovery(reason, isRetry: false);

    void StartDeviceLostRecovery(string reason, bool isRetry)
    {
        if (_disposed || Interlocked.CompareExchange(ref _recoveryInProgress, 1, 0) != 0)
            return;

        using var owner = DiagnosticRouter.EnterOwner(_diagnosticOwner);
        if (isRetry)
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Warning, DeviceRecoveryDiagnosticCategory, $"Retrying GPU device recovery ({reason}).");
        else
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Error, DeviceRecoveryDiagnosticCategory, $"GPU device lost ({reason}); recovering.");

        try
        {
            _recoveryScheduler.Start(RunDeviceLostRecovery);
        }
        catch (ObjectDisposedException)
        {
            // Disposed meanwhile: nothing left to recover.
            Volatile.Write(ref _recoveryInProgress, 0);
        }
    }

    void RunDeviceLostRecovery(CancellationToken cancellationToken)
    {
        using var owner = DiagnosticRouter.EnterOwner(_diagnosticOwner);
        var recovered = false;
        var reportFailure = false;
        var wasRenderingActive = _isRenderingActive;
        try
        {
            if (_disposed)
                return;

            StopRendering();

            var backoff = RecoveryBackoff;
            var attempts = backoff.Length + 1;
            var made = 0;
            for (var attempt = 1; attempt <= attempts && !cancellationToken.IsCancellationRequested; attempt++)
            {
                if (attempt > 1 && cancellationToken.WaitHandle.WaitOne(backoff[attempt - 2]))
                    break;

                made = attempt;

                // No present runs meanwhile, and no surface call sees a half-built device.
                lock (_presentGate)
                {
                    lock (_lock)
                    {
                        if (_disposed)
                            return;

                        using (BeginRendererCall(_renderCalls, "device recovery"))
                            _rendererBackend.CleanupResources();

                        // The renderer's retained hardware frame went with the device: a retained
                        // refresh must not ask the new one for it.
                        _presentedGpuBackground = null;

                        // Re-creates the attached surfaces on the new device.
                        recovered = EnsureBackendInitialized(surfaceCall: true);
                    }
                }

                if (recovered)
                {
                    DiagnosticRouter.Raise(MediaDiagnosticLevel.Info, _rendererBackend.DeviceLostDiagnosticCategory, $"GPU device recovered after {attempt} attempt(s).");
                    break;
                }

                DiagnosticRouter.Raise(MediaDiagnosticLevel.Warning, _rendererBackend.DeviceLostDiagnosticCategory, $"Device-lost recovery attempt {attempt}/{attempts} failed.");
                if (!_rendererBackend.CanRetryDeviceRecovery)
                {
                    // ENG-050: the platform refuses the renderer for the rest of the process;
                    // waiting out the back-off only delays the host's switch to another renderer.
                    DiagnosticRouter.Raise(MediaDiagnosticLevel.Warning, _rendererBackend.DeviceLostDiagnosticCategory, "The renderer cannot be re-created in this process; no more recovery attempts.");
                    break;
                }
            }

            if (!recovered && !cancellationToken.IsCancellationRequested && !_disposed)
            {
                // The first failure of an episode is reported to the player (ENG-050); the 10 s
                // retries after it are not.
                reportFailure = Volatile.Read(ref _recoveryFailedAtPlusOne) == 0;
                Volatile.Write(ref _recoveryFailedAtPlusOne, _timeProvider.GetTimestamp() + 1);
                DiagnosticRouter.Raise(
                    MediaDiagnosticLevel.Error,
                    _rendererBackend.DeviceLostRecoveryFailedDiagnosticCategory,
                    $"GPU device-lost recovery failed after {made} attempt(s); retrying every {RecoveryRetryInterval.TotalSeconds:0} s while a surface is attached.");
            }
        }
        finally
        {
            if (recovered)
                Volatile.Write(ref _recoveryFailedAtPlusOne, 0);
            Volatile.Write(ref _recoveryInProgress, 0);
        }

        if (reportFailure && !_disposed)
        {
            try
            {
                DeviceRecoveryFailed?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MediaPresentationCoordinator] DeviceRecoveryFailed handler failed: {ex.Message}");
            }
        }

        if (!recovered || _disposed)
            return;

        // Hardware frames are accepted again from here on.
        if (wasRenderingActive || _mediaEngine.State == MediaPlayerState.Playing)
            StartRendering();

        PresentCurrentComposition("device-lost recovery completed");

        try
        {
            DeviceRecovered?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MediaPresentationCoordinator] DeviceRecovered handler failed: {ex.Message}");
        }
    }

    /// <summary>Starts the health timer once the renderer first initialized. Under _lock.</summary>
    void ArmDeviceHealthNoLock()
    {
        if (_deviceHealthTimer is not null || _disposed)
            return;

        // The timer must not carry the caller's context (a control operation, a host handler): its
        // ticks enter the player's diagnostic owner themselves.
        AsyncFlowControl? suppressed = ExecutionContext.IsFlowSuppressed() ? null : ExecutionContext.SuppressFlow();
        try
        {
            _deviceHealthTimer = _timeProvider.CreateTimer(
                static state => ((MediaPresentationCoordinator)state!).OnDeviceHealthTick(),
                this,
                DeviceHealthInterval,
                DeviceHealthInterval);
        }
        finally
        {
            suppressed?.Undo();
        }
    }

    void StopDeviceHealth()
    {
        ITimer? timer;
        lock (_lock)
        {
            timer = _deviceHealthTimer;
            _deviceHealthTimer = null;
        }

        timer?.Dispose();
    }

    /// <summary>
    /// A present or surface call failed: ask the renderer about its device now instead of at the
    /// next tick (at most every 250 ms, and only while a surface is attached, or for a failed
    /// attach: with no surface left nothing else would find the loss). The probe runs on the
    /// timer's thread, not on the caller's.
    /// </summary>
    void KickDeviceProbe(bool attachFailed = false)
    {
        var timer = Volatile.Read(ref _deviceHealthTimer);
        if (timer is null || _disposed)
            return;

        if (attachFailed)
            Volatile.Write(ref _probeAfterFailedAttach, 1);
        else if (!HasAttachedSurfaces)
            return;

        var now = _timeProvider.GetTimestamp();
        var last = Volatile.Read(ref _lastDeviceProbeKickPlusOne);
        if (last != 0 && _timeProvider.GetElapsedTime(last - 1, now) < DeviceProbeKickInterval)
            return;

        Volatile.Write(ref _lastDeviceProbeKickPlusOne, now + 1);
        try
        {
            _ = timer.Change(TimeSpan.Zero, DeviceHealthInterval);
        }
        catch (ObjectDisposedException)
        {
            // Disposed meanwhile.
        }
    }

    void OnDeviceHealthTick()
    {
        if (Interlocked.Exchange(ref _deviceHealthTickRunning, 1) != 0)
            return;

        try
        {
            if (_disposed)
                return;

            using var owner = DiagnosticRouter.EnterOwner(_diagnosticOwner);
            var stalled = CheckForStall(_renderCalls) | CheckForStall(_surfaceCalls);
            if (Volatile.Read(ref _recoveryInProgress) != 0)
                return;

            // Given up: retry every 10 s, or at once when a surface call created a device meanwhile
            // (the full recovery then also has the video reopened).
            var failedAt = Volatile.Read(ref _recoveryFailedAtPlusOne);
            if (failedAt != 0)
            {
                if (HasAttachedSurfaces
                    && (_rendererBackend.IsInitialized || _timeProvider.GetElapsedTime(failedAt - 1) >= RecoveryRetryInterval))
                    StartDeviceLostRecovery("the previous recovery failed", isRetry: true);
                return;
            }

            // Nothing is shown without a surface; the next attach finds the loss (it fails and probes).
            var attachFailed = Interlocked.Exchange(ref _probeAfterFailedAttach, 0) != 0;
            if ((!HasAttachedSurfaces && !attachFailed) || !_rendererBackend.IsInitialized)
                return;

            if (TryProbeDeviceLost(out var reason))
                RequestDeviceLostRecovery(reason);
            else if (stalled)
                Debug.WriteLine("[MediaPresentationCoordinator] A renderer call is blocked; the device reports no loss.");
        }
        catch (Exception ex)
        {
            // A timer callback must not throw: that would end the process.
            Debug.WriteLine($"[MediaPresentationCoordinator] Device health check failed: {ex.Message}");
        }
        finally
        {
            Volatile.Write(ref _deviceHealthTickRunning, 0);
        }
    }

    bool TryProbeDeviceLost(out string reason)
    {
        reason = string.Empty;
        try
        {
            if (!_rendererBackend.ProbeDeviceLost(out var probeReason))
                return false;

            reason = string.IsNullOrWhiteSpace(probeReason) ? $"{_rendererBackend.BackendName} reported its device lost" : probeReason;
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MediaPresentationCoordinator] Device probe failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>Reports a renderer call that has not returned for 3 s, once per call; true while one is that late.</summary>
    bool CheckForStall(RendererCallTracker tracker)
    {
        var startedPlusOne = Volatile.Read(ref tracker.StartedPlusOne);
        if (startedPlusOne == 0)
            return false;

        var elapsed = _timeProvider.GetElapsedTime(startedPlusOne - 1);
        if (elapsed < RendererStallThreshold)
            return false;

        if (Interlocked.CompareExchange(ref tracker.ReportedPlusOne, startedPlusOne, 0) == 0)
        {
            DiagnosticRouter.Raise(
                MediaDiagnosticLevel.Error,
                PresentationDiagnosticCategory,
                $"Renderer call has not returned for {elapsed.TotalSeconds:0} s ({Volatile.Read(ref tracker.Operation)}); presentation is blocked.");
        }

        return true;
    }

    bool IsRendererCallLate(RendererCallTracker tracker, TimeSpan threshold)
    {
        var startedPlusOne = Volatile.Read(ref tracker.StartedPlusOne);
        return startedPlusOne != 0 && _timeProvider.GetElapsedTime(startedPlusOne - 1) >= threshold;
    }

    /// <summary>Times one renderer call for the stall detector; a nested call is timed as part of the outer one.</summary>
    RendererCallScope BeginRendererCall(RendererCallTracker tracker, string operation)
    {
        if (Volatile.Read(ref tracker.StartedPlusOne) != 0)
            return default;

        Volatile.Write(ref tracker.Operation, operation);
        Volatile.Write(ref tracker.StartedPlusOne, _timeProvider.GetTimestamp() + 1);
        return new RendererCallScope(this, tracker);
    }

    void EndRendererCall(RendererCallTracker tracker)
    {
        var startedPlusOne = Interlocked.Exchange(ref tracker.StartedPlusOne, 0);
        if (startedPlusOne == 0 || Interlocked.Exchange(ref tracker.ReportedPlusOne, 0) != startedPlusOne)
            return;

        // It was reported as blocked: say that it came back.
        using var owner = DiagnosticRouter.EnterOwner(_diagnosticOwner);
        DiagnosticRouter.Raise(
            MediaDiagnosticLevel.Info,
            PresentationDiagnosticCategory,
            $"Renderer call returned after {_timeProvider.GetElapsedTime(startedPlusOne - 1).TotalSeconds:0.#} s ({tracker.Operation}).");
    }

    /// <summary>
    /// Presents after a surface call. On the caller's thread normally; while a renderer call has
    /// been running for a while (a present stuck after a device loss) the present is left to the
    /// thread pool, so the UI thread returns instead of waiting behind it (ENG-039).
    /// </summary>
    void PresentAfterSurfaceChange(string reason)
    {
        if (!IsRendererCallLate(_renderCalls, SurfacePresentDeferThreshold))
        {
            PresentCurrentComposition(reason);
            return;
        }

        // 0 idle, 1 running, 2 queued or asked again while running. One pool thread at most: a
        // present stuck in the renderer must not take a new pool thread per surface change.
        if (Interlocked.Exchange(ref _deferredSurfacePresentQueued, 2) != 0)
            return;

        _ = ThreadPool.QueueUserWorkItem(
            static state =>
            {
                (var coordinator, var reason) = state;
                do
                {
                    Volatile.Write(ref coordinator._deferredSurfacePresentQueued, 1);
                    try
                    {
                        if (!coordinator._disposed)
                            coordinator.PresentCurrentComposition(reason);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[MediaPresentationCoordinator] Deferred surface present failed: {ex.Message}");
                    }
                }
                while (Interlocked.CompareExchange(ref coordinator._deferredSurfacePresentQueued, 0, 1) != 1);
            },
            (this, reason),
            preferLocal: false);
    }

    /// <param name="Revision">The background revision the frame was handed over with.</param>
    readonly record struct GpuBackground(int Width, int Height, MediaCompositionStretchMode StretchMode, long Revision);

    /// <summary>The renderer call in progress on one kind of thread (presents, or surface calls).</summary>
    sealed class RendererCallTracker
    {
        // Timestamp + 1 of the call in progress, or 0.
        public long StartedPlusOne;
        // StartedPlusOne of the call reported as blocked, or 0.
        public long ReportedPlusOne;
        public string? Operation;
    }

    readonly struct RendererCallScope(MediaPresentationCoordinator? coordinator, RendererCallTracker? tracker) : IDisposable
    {
        public void Dispose()
        {
            if (coordinator is not null && tracker is not null)
                coordinator.EndRendererCall(tracker);
        }
    }
}
