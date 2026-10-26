namespace FatzBomb.MediaControl.Core.Network;

/// <summary>One sample of an FFmpeg network reader for <see cref="NetworkBufferingMonitor"/>.</summary>
/// <param name="IsPlaying">Whether playback is running; paused or stopped samples leave the state as it is.</param>
/// <param name="ReadStall">How long the current network read has been blocked.</param>
/// <param name="QueueFill">Data queued ahead of playback, <c>0..1</c> of what ends a buffering spell.</param>
/// <param name="EndOfStream">Whether the reader reached the end of the stream.</param>
/// <param name="PlaybackEnded">
/// Whether playback ran to its end (as opposed to a pause): a buffering spell in progress ends
/// with it (ENG-034: the clock can run out during an outage).
/// </param>
internal readonly record struct NetworkBufferingSample(bool IsPlaying, TimeSpan ReadStall, double QueueFill, bool EndOfStream, bool PlaybackEnded = false);

/// <summary>
/// Samples an FFmpeg network reader every 250 ms (the 014A cadence) and raises buffering changes
/// from the timer thread. Created per open; disposing it stops the samples. A sample that was
/// already past its check when the monitor was disposed can still raise one late change; the
/// player drops changes that arrive after the source closed. While playback is paused the state is
/// left as it is; when playback has ended a buffering spell in progress ends.
/// </summary>
internal sealed class NetworkBufferingMonitor : IDisposable
{
    /// <summary>
    /// The exit fill for a read-ahead sample (slice 2): its fill is already the progress towards
    /// the resume threshold, so buffering ends when it is complete.
    /// </summary>
    public const double ReadAheadExitFill = 1.0;

    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(250);
    private readonly Lock _gate = new();
    private readonly Func<NetworkBufferingSample> _sample;
    private readonly Action<MediaBufferingChangedEventArgs> _raise;
    private readonly Action? _onTick;
    private readonly NetworkStallBufferingTracker _tracker;
    private readonly Timer _timer;
    private bool _disposed;

    /// <param name="sample">Reads the reader's state.</param>
    /// <param name="raise">Raises a change, from the timer thread.</param>
    /// <param name="exitFill">The sample fill that ends buffering (see <see cref="NetworkStallBufferingTracker"/>).</param>
    /// <param name="onTick">Other per-source polling on the same cadence (stream titles), run first on every tick.</param>
    public NetworkBufferingMonitor(Func<NetworkBufferingSample> sample, Action<MediaBufferingChangedEventArgs> raise, double exitFill = NetworkStallBufferingTracker.DefaultExitFill, Action? onTick = null)
    {
        _sample = sample ?? throw new ArgumentNullException(nameof(sample));
        _raise = raise ?? throw new ArgumentNullException(nameof(raise));
        _onTick = onTick;
        _tracker = new NetworkStallBufferingTracker(NetworkStallBufferingTracker.DefaultStallThreshold, exitFill);
        _timer = new Timer(_ => Tick(), null, Interval, Interval);
    }

    private void Tick()
    {
        try
        {
            if (Volatile.Read(ref _disposed))
                return;

            _onTick?.Invoke();
            NetworkBufferingSample sample = _sample();
            MediaBufferingChangedEventArgs? change = null;
            lock (_gate)
            {
                if (_disposed)
                    return;

                if (!sample.IsPlaying)
                {
                    if (sample.PlaybackEnded && _tracker.EndSpell(out var endFill))
                        change = new MediaBufferingChangedEventArgs(false, endFill);
                }
                else if (_tracker.Update(sample.ReadStall, sample.QueueFill, sample.EndOfStream, out var fill))
                {
                    change = new MediaBufferingChangedEventArgs(_tracker.IsBuffering, fill);
                }
            }

            if (change is not null)
                _raise(change);
        }
        catch (Exception ex)
        {
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Warning, "Network", $"Network buffer sampling failed: {ex.Message}", innerException: ex);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
        }

        _timer.Dispose();
    }
}
