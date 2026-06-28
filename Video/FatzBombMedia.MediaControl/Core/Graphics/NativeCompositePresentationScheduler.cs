using System.Diagnostics;
using System.Threading;

namespace FatzBombMedia.MediaControl.Core.Graphics;

/// <summary>
/// Owns the native static-composite presentation cadence.
/// Background video frames are presented by their own video cadence; this scheduler is for
/// CDG-only and CDG+static-image invalidations that need coalesced native HWND redraws.
/// </summary>
internal sealed class NativeCompositePresentationScheduler
{
    private const double StaticMaxPresentFps = 30.0;

    private long _lastPresentTimestamp;
    private int _presentQueued;
    private int _dirty;
    private int _allowDuringResize;

    public bool IsDirty => Volatile.Read(ref _dirty) != 0;

    public void RequestPresent(
        bool forceImmediate,
        bool allowDuringResize,
        Func<bool> isDisposed,
        Func<bool> isNativePresentationRequested,
        Func<bool> shouldFreezeDuringResize,
        Action present)
    {
        if (isDisposed() || !isNativePresentationRequested())
        {
            return;
        }

        Volatile.Write(ref _dirty, 1);
        if (allowDuringResize)
        {
            Volatile.Write(ref _allowDuringResize, 1);
        }

        if (shouldFreezeDuringResize() && !allowDuringResize)
        {
            return;
        }

        if (forceImmediate)
        {
            QueuePresent(TimeSpan.Zero, allowDuringResize, isDisposed, isNativePresentationRequested, shouldFreezeDuringResize, present);
            return;
        }

        var now = Stopwatch.GetTimestamp();
        var minTicks = Math.Max(1L, (long)(Stopwatch.Frequency / StaticMaxPresentFps));
        var last = Volatile.Read(ref _lastPresentTimestamp);
        var elapsed = last <= 0 ? minTicks : now - last;

        if (elapsed >= minTicks)
        {
            QueuePresent(TimeSpan.Zero, allowDuringResize, isDisposed, isNativePresentationRequested, shouldFreezeDuringResize, present);
            return;
        }

        var delayTicks = minTicks - elapsed;
        var delay = TimeSpan.FromSeconds(delayTicks / (double)Stopwatch.Frequency);
        QueuePresent(delay, allowDuringResize, isDisposed, isNativePresentationRequested, shouldFreezeDuringResize, present);
    }

    public void Clear()
    {
        Volatile.Write(ref _dirty, 0);
        Volatile.Write(ref _allowDuringResize, 0);
        Volatile.Write(ref _lastPresentTimestamp, 0);
    }

    private void QueuePresent(
        TimeSpan delay,
        bool allowDuringResize,
        Func<bool> isDisposed,
        Func<bool> isNativePresentationRequested,
        Func<bool> shouldFreezeDuringResize,
        Action present)
    {
        if (allowDuringResize)
        {
            Volatile.Write(ref _allowDuringResize, 1);
        }

        if (Interlocked.CompareExchange(ref _presentQueued, 1, 0) != 0)
        {
            return;
        }

        if (delay <= TimeSpan.Zero)
        {
            ThreadPool.QueueUserWorkItem(_ => RunQueuedPresent(isDisposed, isNativePresentationRequested, shouldFreezeDuringResize, present));
            return;
        }

        _ = Task.Delay(delay).ContinueWith(
            _ => RunQueuedPresent(isDisposed, isNativePresentationRequested, shouldFreezeDuringResize, present),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void RunQueuedPresent(
        Func<bool> isDisposed,
        Func<bool> isNativePresentationRequested,
        Func<bool> shouldFreezeDuringResize,
        Action present)
    {
        try
        {
            var allowDuringResize = Volatile.Read(ref _allowDuringResize) != 0;
            if (isDisposed() || !isNativePresentationRequested() || (shouldFreezeDuringResize() && !allowDuringResize))
            {
                return;
            }

            Volatile.Write(ref _dirty, 0);
            Volatile.Write(ref _allowDuringResize, 0);
            present();
            Volatile.Write(ref _lastPresentTimestamp, Stopwatch.GetTimestamp());
        }
        finally
        {
            Volatile.Write(ref _presentQueued, 0);

            // If another layer update arrived while this presentation was running, queue one more
            // coalesced pass. This preserves responsiveness without presenting once per CDG packet.
            if (!isDisposed() && isNativePresentationRequested() && IsDirty)
            {
                RequestPresent(
                    forceImmediate: false,
                    allowDuringResize: false,
                    isDisposed,
                    isNativePresentationRequested,
                    shouldFreezeDuringResize,
                    present);
            }
        }
    }
}
