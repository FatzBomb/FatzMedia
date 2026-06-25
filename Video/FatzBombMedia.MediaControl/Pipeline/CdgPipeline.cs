namespace FatzBombMedia.MediaControl.Pipeline;

using FatzBombMedia.MediaControl.Core.Abstractions;
using System.Diagnostics;
using System.IO;

internal sealed class CdgPipeline
{
    private readonly ICdgEngine _cdgEngine;
    private readonly MediaState _state;
    private CancellationTokenSource? _completionCts;
    private bool _isOpen;
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

    public CdgPipeline(ICdgEngine cdgEngine, PlaybackClock clock, MediaState state)
    {
        _cdgEngine = cdgEngine ?? throw new ArgumentNullException(nameof(cdgEngine));
        Clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _state = state ?? throw new ArgumentNullException(nameof(state));
    }

    public PlaybackClock Clock { get; }
    public ICdgEngine Engine => _cdgEngine;
    public bool IsOpen => _isOpen;
    public double DurationSeconds => _cdgEngine.TotalDurationSeconds;
    public event EventHandler? PlaybackEnded;

    public void OpenPaired(string path, int audioStreamHandle)
    {
        ThrowIfInvalidPath(path);
        _cdgEngine.LoadPaired(path, audioStreamHandle);
        _isOpen = true;
        _isPairedWithAudio = true;
        _state.SetDuration(Math.Max(_state.DurationSeconds, DurationSeconds));
        _state.SetPosition(0);
        StartSeekWorker();
    }

    public void OpenPaired(IArchiveEntryInfo archiveEntry, int audioStreamHandle)
    {
        ArgumentNullException.ThrowIfNull(archiveEntry);
        _cdgEngine.LoadPaired(archiveEntry, audioStreamHandle);
        _isOpen = true;
        _isPairedWithAudio = true;
        _state.SetDuration(Math.Max(_state.DurationSeconds, DurationSeconds));
        _state.SetPosition(0);
        StartSeekWorker();
    }

    public void OpenStandalone(string path)
    {
        ThrowIfInvalidPath(path);
        _cdgEngine.Load(path);
        Clock.Stop();
        Clock.DurationSeconds = DurationSeconds;
        _isOpen = true;
        _isPairedWithAudio = false;
        _state.SetOpened(MediaContent.Cdg, path, DurationSeconds);
        RenderAt(0);
        StartSeekWorker();
    }

    public void OpenStandalone(IArchiveEntryInfo archiveEntry)
    {
        ArgumentNullException.ThrowIfNull(archiveEntry);
        _cdgEngine.Load(archiveEntry);
        Clock.Stop();
        Clock.DurationSeconds = DurationSeconds;
        _isOpen = true;
        _isPairedWithAudio = false;
        _state.SetOpened(MediaContent.Cdg | MediaContent.Archive, archiveEntry.Key, DurationSeconds);
        RenderAt(0);
        StartSeekWorker();
    }

    public bool Play()
    {
        if (!_isOpen)
        {
            return false;
        }

        if (!_isPairedWithAudio)
        {
            Clock.Start(Clock.PositionSeconds);
        }

        _cdgEngine.Play();
        _state.SetPlaybackState(MediaPlayerState.Playing);
        if (!_isPairedWithAudio)
        {
            StartCompletionMonitor();
        }
        return true;
    }

    public bool Pause()
    {
        if (!_isOpen)
        {
            return false;
        }

        if (!_isPairedWithAudio)
        {
            Clock.Pause();
        }

        _cdgEngine.Pause();
        StopCompletionMonitor();
        _state.SetPlaybackState(MediaPlayerState.Paused);
        return true;
    }

    public bool Stop()
    {
        if (!_isOpen)
        {
            return false;
        }

        Clock.Stop();
        _cdgEngine.Stop();
        StopCompletionMonitor();
        _state.SetPosition(0);
        _state.SetPlaybackState(MediaPlayerState.Stopped);
        return true;
    }

    public bool Seek(double seconds)
    {
        if (!_isOpen)
        {
            return false;
        }

        var target = NormalizeSeekTarget(seconds);
        lock (_seekPumpLock)
        {
            _pendingSeekSeconds = target;
        }

        _seekSignal.Set();
        return true;
    }

    private bool SeekCore(double seconds)
    {
        lock (_transportGate)
        {
            if (!_isOpen)
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
        var thread = _seekThread;
        if (thread == null)
        {
            return;
        }

        _seekWorkerShutdown = true;
        _seekSignal.Set();
        try
        {
            thread.Join(1000);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CdgPipeline] Seek worker join failed: {ex.Message}");
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
            _seekSignal.WaitOne();

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
                    SeekCore(normalizedTarget);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[CdgPipeline] Seek pump error: {ex.Message}");
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
        {
            return _pendingSeekSeconds.HasValue;
        }
    }

    private void WaitForSeekPumpInterval()
    {
        var started = Stopwatch.GetTimestamp();
        while (!_seekWorkerShutdown
            && GetElapsedMilliseconds(started, Stopwatch.GetTimestamp()) < CdgSeekIntervalMilliseconds)
        {
            _seekSignal.WaitOne(TimeSpan.FromMilliseconds(10));
        }
    }

    private double NormalizeSeekTarget(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds <= 0)
        {
            return 0;
        }

        return DurationSeconds > 0 ? Math.Clamp(seconds, 0, DurationSeconds) : seconds;
    }

    private static bool IsMeaningfullyDifferentSeekTarget(double target, double previousTarget)
    {
        return double.IsNaN(previousTarget)
            || Math.Abs(target - previousTarget) > CdgSeekTargetChangeThresholdSeconds;
    }

    private static double GetElapsedMilliseconds(long startTimestamp, long endTimestamp)
    {
        return (endTimestamp - startTimestamp) * 1000.0 / Stopwatch.Frequency;
    }

    public void RenderAt(double seconds) => RenderAt(seconds, forceRebuild: false);

    public void RenderAt(double seconds, bool forceRebuild)
    {
        if (_isOpen)
        {
            _cdgEngine.RenderAtSeconds(seconds, forceRebuild);
        }
    }

    public void Close()
    {
        StopSeekWorker();
        if (!_isOpen)
        {
            return;
        }

        Clock.Stop();
        StopCompletionMonitor();
        _cdgEngine.Reset();
        _isOpen = false;
        _isPairedWithAudio = false;
    }

    private void StartCompletionMonitor()
    {
        StopCompletionMonitor();
        if (!_isOpen || _isPairedWithAudio || DurationSeconds <= 0)
        {
            return;
        }

        Interlocked.Exchange(ref _completionSignaled, 0);
        var generation = Interlocked.Increment(ref _completionGeneration);
        var cts = new CancellationTokenSource();
        _completionCts = cts;
        _ = MonitorCompletionAsync(generation, cts.Token);
    }

    private void StopCompletionMonitor()
    {
        var cts = _completionCts;
        _completionCts = null;
        if (cts == null)
        {
            return;
        }

        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async Task MonitorCompletionAsync(int generation, CancellationToken cancellationToken)
    {
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
    }

    private void CompleteStandalonePlayback(int generation)
    {
        if (generation != _completionGeneration || Interlocked.Exchange(ref _completionSignaled, 1) != 0)
        {
            return;
        }

        Clock.Pause();
        _cdgEngine.Pause();
        _state.SetPosition(DurationSeconds);
        _state.SetPlaybackState(MediaPlayerState.Stopped);
        PlaybackEnded?.Invoke(this, EventArgs.Empty);
    }

    private static void ThrowIfInvalidPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            throw new FileNotFoundException("CDG file was not found.", path);
        }
    }
}
