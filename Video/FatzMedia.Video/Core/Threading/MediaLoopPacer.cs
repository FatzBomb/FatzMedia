namespace FatzMedia.Video.Core.Threading;

/// <summary>
/// Paces a single media-loop thread against monotonic absolute deadlines.
/// </summary>
/// <remarks>
/// Core owns cadence accumulation, work-time compensation, and late-loop resynchronization.
/// The selected platform only supplies the cancellable wait primitive.
/// </remarks>
public sealed class MediaLoopPacer : IDisposable
{
    private readonly IPrecisionWaiter _waiter;
    private readonly TimeProvider _timeProvider;
    private long _nextDeadlineTimestamp;
    private bool _disposed;

    public MediaLoopPacer(IPrecisionWaiterFactory waiterFactory, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(waiterFactory);

        _waiter = waiterFactory.Create();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _nextDeadlineTimestamp = _timeProvider.GetTimestamp();
    }

    /// <summary>Gets whether the platform wait primitive supports high-resolution waits.</summary>
    public bool IsHighResolution => _waiter.IsHighResolution;

    /// <summary>Resets the fixed-cadence accumulator to the current monotonic timestamp.</summary>
    public void Reset()
    {
        ThrowIfDisposed();
        _nextDeadlineTimestamp = _timeProvider.GetTimestamp();
    }

    /// <summary>
    /// Advances one interval from the prior deadline and waits only for the remaining time.
    /// If work has already passed the deadline, the cadence is reset instead of issuing
    /// catch-up ticks. An optional short yield prevents a persistently late loop from spinning.
    /// </summary>
    public void WaitNext(
        TimeSpan interval,
        WaitHandle cancellationHandle,
        TimeSpan lateYield = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(cancellationHandle);
        if (interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(interval), interval, "The pacing interval must be positive.");
        }
        if (lateYield < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(lateYield), lateYield, "The late-loop yield cannot be negative.");
        }

        _nextDeadlineTimestamp = AddTimestampTicks(
            _nextDeadlineTimestamp,
            ToTimestampTicks(interval));

        var now = _timeProvider.GetTimestamp();
        var remainingTicks = _nextDeadlineTimestamp - now;
        if (remainingTicks > 0)
        {
            _waiter.Wait(ToTimeSpan(remainingTicks), cancellationHandle);
            return;
        }

        _nextDeadlineTimestamp = now;
        if (lateYield > TimeSpan.Zero)
        {
            _waiter.Wait(lateYield, cancellationHandle);
        }

    }

    /// <summary>
    /// Waits for a relative duration and resets the fixed-cadence accumulator afterward.
    /// Use this for media-clock-derived waits whose interval changes every iteration.
    /// </summary>
    public void WaitFor(TimeSpan duration, WaitHandle cancellationHandle)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(cancellationHandle);

        if (duration > TimeSpan.Zero)
        {
            var startedAt = _timeProvider.GetTimestamp();
            var deadline = AddTimestampTicks(startedAt, ToTimestampTicks(duration));
            // Charge deadline-construction overhead to this wait instead of drifting the loop.
            var remainingTicks = deadline - _timeProvider.GetTimestamp();
            if (remainingTicks > 0)
            {
                _waiter.Wait(ToTimeSpan(remainingTicks), cancellationHandle);
            }
        }

        _nextDeadlineTimestamp = _timeProvider.GetTimestamp();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _waiter.Dispose();
    }

    private long ToTimestampTicks(TimeSpan duration)
    {
        var ticks = duration.TotalSeconds * _timeProvider.TimestampFrequency;
        return Math.Max(1L, (long)Math.Round(ticks));
    }

    private TimeSpan ToTimeSpan(long timestampTicks) =>
        TimeSpan.FromSeconds(timestampTicks / (double)_timeProvider.TimestampFrequency);

    private static long AddTimestampTicks(long timestamp, long ticks) =>
        timestamp > long.MaxValue - ticks ? long.MaxValue : timestamp + ticks;

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
