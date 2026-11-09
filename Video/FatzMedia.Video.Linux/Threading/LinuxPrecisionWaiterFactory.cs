using System.Diagnostics;
using FatzMedia.Video.Core.Threading;

namespace FatzMedia.Video.Linux.Threading;

/// <summary>
/// Linux media-loop waiter: a kernel wait (futex/hrtimer backed) for all but the final
/// spin window, then a short spin to the deadline. Core's <see cref="MediaLoopPacer"/> owns
/// deadline policy; this only supplies the primitive.
/// </summary>
sealed class LinuxPrecisionWaiterFactory : IPrecisionWaiterFactory
{
    public IPrecisionWaiter Create() => new LinuxPrecisionWaiter();
}

sealed class LinuxPrecisionWaiter : IPrecisionWaiter
{
    /// <summary>Time left to the deadline that is spun instead of slept.</summary>
    internal static readonly TimeSpan SpinWindow = TimeSpan.FromMilliseconds(1);

    public bool IsHighResolution => true;

    public void Wait(TimeSpan duration, WaitHandle cancellationHandle)
    {
        ArgumentNullException.ThrowIfNull(cancellationHandle);
        if (duration <= TimeSpan.Zero)
            return;

        var deadline = Stopwatch.GetTimestamp() + (long)(duration.TotalSeconds * Stopwatch.Frequency);
        var sleep = duration - SpinWindow;
        if (sleep > TimeSpan.Zero && cancellationHandle.WaitOne(sleep))
            return;

        var spinner = new SpinWait();
        while (Stopwatch.GetTimestamp() < deadline)
        {
            if (cancellationHandle.WaitOne(0))
                return;

            spinner.SpinOnce(sleep1Threshold: -1);
        }
    }

    public void Dispose()
    {
    }
}
