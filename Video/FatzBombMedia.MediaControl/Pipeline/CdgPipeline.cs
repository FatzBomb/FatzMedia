
using FatzBombMedia.MediaControl.Core.Abstractions;
using System.Diagnostics;

namespace FatzBombMedia.MediaControl.Pipeline;

internal sealed class CdgPipeline(ICdgEngine cdgEngine, PlaybackClock clock, MediaState state)
{
    private readonly MediaState _state = state ?? throw new ArgumentNullException(nameof(state));
    private CancellationTokenSource? _completionCts;
    private bool _isPairedWithAudio;
    private int _completionGeneration;
    private int _completionSignaled;

    // CDG seek is already relatively cheap, but callers may still send many seek requests while
    // scrubbing. Keep only the latest pending CDG target and process it at a small internal cadence.
    // This is intentionally not public API; hosts should simply call Seek(time).
    private readonly Lock _transportGate = new();
    private readonly Lock _seekPumpLock = new();
    private readonly AutoResetEvent _seekSignal = new(false);
    private Thread? _seekThread;
    private volatile bool _seekWorkerShutdown;
    private double? _pendingSeekSeconds;
    private const int CdgSeekIntervalMilliseconds = 50;
    private const double CdgSeekTargetChangeThresholdSeconds = 0.025;

    private PlaybackClock Clock { get; } = clock ?? throw new ArgumentNullException(nameof(clock));
    public ICdgEngine Engine { get; } = cdgEngine ?? throw new ArgumentNullException(nameof(cdgEngine));
    public bool IsOpen { get; private set; }
    public double DurationSeconds => Engine.TotalDurationSeconds;
    public event EventHandler? PlaybackEnded;

    public void OpenPaired(string path, int audioStreamHandle)
    {
        ThrowIfInvalidPath(path);
        Engine.LoadPaired(path, audioStreamHandle);
        IsOpen = true;
        _isPairedWithAudio = true;
        _state.SetDuration(Math.Max(_state.DurationSeconds, DurationSeconds));
        _state.SetPosition(0);
        StartSeekWorker();
    }

    public void OpenPaired(IArchiveEntryInfo archiveEntry, int audioStreamHandle)
    {
        ArgumentNullException.ThrowIfNull(archiveEntry);
        Engine.LoadPaired(archiveEntry, audioStreamHandle);
        IsOpen = true;
        _isPairedWithAudio = true;
        _state.SetDuration(Math.Max(_state.DurationSeconds, DurationSeconds));
        _state.SetPosition(0);
        StartSeekWorker();
    }

    public void OpenStandalone(string path)
    {
        ThrowIfInvalidPath(path);
        Engine.Load(path);
        Clock.Stop();
        Clock.DurationSeconds = DurationSeconds;
        IsOpen = true;
        _isPairedWithAudio = false;
        _state.SetOpened(MediaContent.Cdg, path, DurationSeconds);
        RenderAt(0, forceRebuild: false);
        StartSeekWorker();
    }

    public void OpenStandalone(IArchiveEntryInfo archiveEntry)
    {
        ArgumentNullException.ThrowIfNull(archiveEntry);
        Engine.Load(archiveEntry);
        Clock.Stop();
        Clock.DurationSeconds = DurationSeconds;
        IsOpen = true;
        _isPairedWithAudio = false;
        _state.SetOpened(MediaContent.Cdg | MediaContent.Archive, archiveEntry.Key, DurationSeconds);
        RenderAt(0, forceRebuild: false);
        StartSeekWorker();
    }

    public bool Play()
    {
        if (!IsOpen)
        {
            return false;
        }

        if (!_isPairedWithAudio)
        {
            Clock.Start(Clock.PositionSeconds);
        }

        Engine.Play();
        _state.SetPlaybackState(MediaPlayerState.Playing);
        if (!_isPairedWithAudio)
        {
            StartCompletionMonitor();
        }
        return true;
    }

    public bool Pause()
    {
        if (!IsOpen)
        {
            return false;
        }

        if (!_isPairedWithAudio)
        {
            Clock.Pause();
        }

        Engine.Pause();
        StopCompletionMonitor();
        _state.SetPlaybackState(MediaPlayerState.Paused);
        return true;
    }

    public bool Stop()
    {
        if (!IsOpen)
        {
            return false;
        }

        Clock.Stop();
        Engine.Stop();
        StopCompletionMonitor();
        _state.SetPosition(0);
        _state.SetPlaybackState(MediaPlayerState.Stopped);
        return true;
    }

    public bool Seek(double seconds)
    {
        if (!IsOpen)
        {
            return false;
        }

        var target = NormalizeSeekTarget(seconds);
        lock (_seekPumpLock)
        {
            _pendingSeekSeconds = target;
        }

        _ = _seekSignal.Set();
        return true;
    }

    private bool SeekCore(double seconds)
    {
        lock (_transportGate)
        {
            if (!IsOpen)
            {
                return false;
            }

            var target = NormalizeSeekTarget(seconds);
            if (!_isPairedWithAudio)
            {
                Clock.Seek(target);
                if (_state.PlaybackState == MediaPlayerState.Playing)
                {
                    StartCompletionMonitor();
                }
            }

            RenderAt(target, forceRebuild: true);
            _state.SetPosition(target);
            return true;
        }
    }

    private void StartSeekWorker()
    {
        StopSeekWorker();
        lock (_seekPumpLock)
        {
            _pendingSeekSeconds = null;
        }

        _seekWorkerShutdown = false;
        _seekThread = new Thread(SeekWorkerLoop)
        {
            IsBackground = true,
            Name = "CdgSeek"
        };
        _seekThread.Start();
    }

    private void StopSeekWorker()
    {
        Thread? thread = _seekThread;
        if (thread == null)
        {
            return;
        }

        _seekWorkerShutdown = true;
        _ = _seekSignal.Set();
        try
        {
            _ = thread.Join(1000);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CdgPipeline] Seek worker join failed: {ex.Message}");
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Warning, "CdgPipeline", $"Seek worker join failed: {ex.Message}", innerException: ex);
        }

        _seekThread = null;
        lock (_seekPumpLock)
        {
            _pendingSeekSeconds = null;
        }
    }

    private void SeekWorkerLoop()
    {
        while (!_seekWorkerShutdown)
        {
            _ = _seekSignal.WaitOne();

            var lastProcessedTarget = double.NaN;
            while (!_seekWorkerShutdown)
            {
                if (!TryTakePendingSeek(out var target))
                {
                    break;
                }

                var normalizedTarget = NormalizeSeekTarget(target);
                if (!IsMeaningfullyDifferentSeekTarget(normalizedTarget, lastProcessedTarget))
                {
                    if (!HasPendingSeekRequest())
                    {
                        break;
                    }

                    continue;
                }

                try
                {
                    _ = SeekCore(normalizedTarget);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[CdgPipeline] Seek pump error: {ex.Message}");
                    DiagnosticRouter.Raise(MediaDiagnosticLevel.Warning, "CdgPipeline", $"Seek pump error: {ex.Message}", innerException: ex);
                }

                lastProcessedTarget = normalizedTarget;
                WaitForSeekPumpInterval();
                if (!HasPendingSeekRequest())
                {
                    break;
                }
            }
        }
    }

    private bool TryTakePendingSeek(out double target)
    {
        lock (_seekPumpLock)
        {
            if (!_pendingSeekSeconds.HasValue)
            {
                target = 0;
                return false;
            }

            target = _pendingSeekSeconds.Value;
            _pendingSeekSeconds = null;
            return true;
        }
    }

    private bool HasPendingSeekRequest()
    {
        lock (_seekPumpLock)
            return _pendingSeekSeconds.HasValue;
    }

    private void WaitForSeekPumpInterval()
    {
        var started = Stopwatch.GetTimestamp();
        while (!_seekWorkerShutdown && GetElapsedMilliseconds(started, Stopwatch.GetTimestamp()) < CdgSeekIntervalMilliseconds)
            _ = _seekSignal.WaitOne(TimeSpan.FromMilliseconds(10));
    }

    private double NormalizeSeekTarget(double seconds) =>
        !double.IsFinite(seconds) || seconds <= 0
            ? 0
            : DurationSeconds > 0
                ? Math.Clamp(seconds, 0, DurationSeconds)
                : seconds;

    private static bool IsMeaningfullyDifferentSeekTarget(double target, double previousTarget) => double.IsNaN(previousTarget)
            || Math.Abs(target - previousTarget) > CdgSeekTargetChangeThresholdSeconds;

    private static double GetElapsedMilliseconds(long startTimestamp, long endTimestamp) => (endTimestamp - startTimestamp) * 1000.0 / Stopwatch.Frequency;

    public void RenderAt(double seconds, bool forceRebuild)
    {
        if (IsOpen)
            Engine.RenderAtSeconds(seconds, forceRebuild);
    }

    public void Close()
    {
        StopSeekWorker();
        if (!IsOpen)
        {
            return;
        }

        Clock.Stop();
        StopCompletionMonitor();
        Engine.Reset();
        IsOpen = false;
        _isPairedWithAudio = false;
    }

    private void StartCompletionMonitor()
    {
        StopCompletionMonitor();
        if (!IsOpen || _isPairedWithAudio || DurationSeconds <= 0)
            return;

        _ = Interlocked.Exchange(ref _completionSignaled, 0);
        var generation = Interlocked.Increment(ref _completionGeneration);
        var cts = new CancellationTokenSource();
        _completionCts = cts;
        _ = MonitorCompletionAsync(generation, cts);
    }

    private void StopCompletionMonitor()
    {
        CancellationTokenSource? cts = Interlocked.Exchange(ref _completionCts, null);
        if (cts == null)
            return;

        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async Task MonitorCompletionAsync(int generation, CancellationTokenSource cts)
    {
        CancellationToken cancellationToken = cts.Token;
        try
        {
            while (!cancellationToken.IsCancellationRequested && generation == _completionGeneration)
            {
                if (Clock.PositionSeconds >= DurationSeconds - 0.02)
                {
                    CompleteStandalonePlayback(generation);
                    return;
                }

                await Task.Delay(25, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _ = Interlocked.CompareExchange(ref _completionCts, null, cts);
            cts.Dispose();
        }
    }

    private void CompleteStandalonePlayback(int generation)
    {
        if (generation != _completionGeneration || Interlocked.Exchange(ref _completionSignaled, 1) != 0)
            return;

        Clock.Pause();
        Engine.Pause();
        _state.SetPosition(DurationSeconds);
        _state.SetPlaybackState(MediaPlayerState.Stopped);
        PlaybackEnded?.Invoke(this, EventArgs.Empty);
    }

    private static void ThrowIfInvalidPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new FileNotFoundException("CDG file was not found.", path);
    }
}
