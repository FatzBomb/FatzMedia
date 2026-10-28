
using FatzBomb.MediaControl.Core.Abstractions;

namespace FatzBomb.MediaControl.Pipeline;

internal sealed class CdgPipeline : ISeekPumpHost, IDisposable
{
    // CDG seek is already relatively cheap, but callers may still send many seek requests while
    // scrubbing. The shared seek pump keeps only the latest target and renders it at a small
    // internal cadence. This is intentionally not public API; hosts should simply call Seek(time).
    private const int CdgSeekIntervalMilliseconds = 50;

    private readonly MediaState _state;
    private readonly Lock _transportGate = new();
    private readonly SeekPump _seekPump;
    private CancellationTokenSource? _completionCts;
    private bool _isPairedWithAudio;
    private int _completionGeneration;
    private int _completionSignaled;
    private bool _disposed;

    public CdgPipeline(ICdgEngine cdgEngine, IClockSource clock, MediaState state)
    {
        Engine = cdgEngine ?? throw new ArgumentNullException(nameof(cdgEngine));
        Clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _seekPump = new SeekPump(this, "CdgSeek", "CdgPipeline");
    }

    private IClockSource Clock { get; }
    public ICdgEngine Engine { get; }
    public bool IsOpen { get; private set; }
    public double DurationSeconds => Engine.TotalDurationSeconds;
    public event EventHandler? PlaybackEnded;

    public void OpenPaired(string path)
    {
        ThrowIfInvalidPath(path);
        Engine.Load(path);
        IsOpen = true;
        _isPairedWithAudio = true;
        _state.SetDuration(Math.Max(_state.DurationSeconds, DurationSeconds));
        _state.SetPosition(0);
        StartSeekWorker();
    }

    public void OpenPaired(IArchiveEntryInfo archiveEntry)
    {
        ArgumentNullException.ThrowIfNull(archiveEntry);
        Engine.Load(archiveEntry);
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

        _seekPump.Request(NormalizeSeekTarget(seconds));
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

    private void StartSeekWorker() => _seekPump.Start();

    private void StopSeekWorker() => _seekPump.Stop();

    double ISeekPumpHost.NormalizeSeekTarget(double seconds) => NormalizeSeekTarget(seconds);

    // CDG has no preview/settle split: each preview is a full render. The settle that follows
    // always targets the last preview (within the change threshold), so it only ends the seek
    // sequence; rendering again would draw the same frame twice.
    bool ISeekPumpHost.RunSeek(double target, bool asPreview, bool allowPreviewFallback)
    {
        if (asPreview)
            return SeekCore(target);

        _seekPump.EndSequenceIfSettled();
        return true;
    }

    // No nearby-frame previews, so the settle never resumes from a shown frame.
    double ISeekPumpHost.CurrentFrameTime => double.NaN;

    void ISeekPumpHost.WaitForPumpInterval() => _seekPump.WaitInterval(CdgSeekIntervalMilliseconds);

    private double NormalizeSeekTarget(double seconds) =>
        !double.IsFinite(seconds) || seconds <= 0
            ? 0
            : DurationSeconds > 0
                ? Math.Clamp(seconds, 0, DurationSeconds)
                : seconds;

    public void RenderAt(double seconds, bool forceRebuild)
    {
        if (IsOpen)
            Engine.RenderAtSeconds(seconds, forceRebuild);
    }

    /// <summary>Closes the CD+G and frees the seek worker's wait handle.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        Close();
        _seekPump.Dispose();
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
