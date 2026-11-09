using System.Runtime.InteropServices;
using FatzMedia.Video.Core.Threading;
using Microsoft.Win32.SafeHandles;

namespace FatzMedia.Video.Windows.Threading;

/// <summary>
/// Provides a high-resolution, cancellable wait for fixed-cadence presentation/render loops.
/// </summary>
/// <remarks>
/// Loops that pace at source-frame cadence (for example 41.7ms for 23.98fps video) cannot rely on
/// <see cref="WaitHandle.WaitOne(TimeSpan)"/>: that wait is bound by the process timer resolution,
/// which defaults to roughly 15.6ms on Windows. A 41.7ms wait therefore rounds up to about 46.8ms,
/// so the loop runs slower than source cadence and must periodically skip a frame to resync.
///
/// This waiter uses a Windows high-resolution waitable timer
/// (<c>CREATE_WAITABLE_TIMER_HIGH_RESOLUTION</c>, available on Windows 10 1803+) so sub-millisecond
/// pacing is possible without raising the process-global timer resolution via <c>timeBeginPeriod</c>.
/// If the high-resolution timer cannot be created, it transparently falls back to
/// <see cref="WaitHandle.WaitOne(TimeSpan)"/>.
///
/// A single instance is owned by one loop thread and is not safe for concurrent callers.
/// </remarks>
sealed partial class WindowsPrecisionWaiter : IPrecisionWaiter
{
    // CreateWaitableTimerExW flag requesting a high-resolution timer (Windows 10 1803+).
    const uint CreateWaitableTimerHighResolution = 0x00000002;

    // TIMER_ALL_ACCESS = STANDARD_RIGHTS_REQUIRED | SYNCHRONIZE | 0x3.
    const uint TimerAllAccess = 0x1F0003;

    readonly TimerWaitHandle? _timer;
    readonly SafeWaitHandle? _timerHandle;
    readonly WaitHandle[] _waitHandles = new WaitHandle[2];
    bool _disposed;

    public WindowsPrecisionWaiter()
    {
        var handle = CreateWaitableTimerExW(
            IntPtr.Zero,
            IntPtr.Zero,
            CreateWaitableTimerHighResolution,
            TimerAllAccess);

        if (handle.IsInvalid)
        {
            handle.Dispose();
            return;
        }

        _timerHandle = handle;
        _timer = new TimerWaitHandle(handle);
    }

    /// <summary>Gets whether a high-resolution waitable timer is in use (false means fallback).</summary>
    public bool IsHighResolution => _timer is not null;

    /// <summary>
    /// Waits for the supplied duration with high precision, returning early if the cancellation
    /// handle is signaled. Durations at or below zero return immediately.
    /// </summary>
    public void Wait(TimeSpan duration, WaitHandle cancellationHandle)
    {
        ArgumentNullException.ThrowIfNull(cancellationHandle);

        if (_disposed || duration <= TimeSpan.Zero)
            return;

        if (_timer is null || _timerHandle is null)
        {
            _ = cancellationHandle.WaitOne(duration);
            return;
        }

        // Negative due time is relative and expressed in 100ns units.
        var dueTime = -(long)Math.Round(duration.TotalMilliseconds * 10_000.0);
        if (dueTime >= 0)
            dueTime = -1;

        if (!SetWaitableTimer(_timerHandle, in dueTime, 0, IntPtr.Zero, IntPtr.Zero, false))
        {
            _ = cancellationHandle.WaitOne(duration);
            return;
        }

        _waitHandles[0] = cancellationHandle;
        _waitHandles[1] = _timer;

        // Safety timeout just beyond the requested duration so a missed signal can never hang the loop.
        var safetyTimeout = duration + TimeSpan.FromMilliseconds(50);
        _ = WaitHandle.WaitAny(_waitHandles, safetyTimeout);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _timer?.Dispose();
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial SafeWaitHandle CreateWaitableTimerExW(
        IntPtr lpTimerAttributes,
        IntPtr lpTimerName,
        uint dwFlags,
        uint dwDesiredAccess);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWaitableTimer(
        SafeWaitHandle hTimer,
        in long lpDueTime,
        int lPeriod,
        IntPtr pfnCompletionRoutine,
        IntPtr lpArgToCompletionRoutine,
        [MarshalAs(UnmanagedType.Bool)] bool fResume);

    sealed class TimerWaitHandle : WaitHandle
    {
        public TimerWaitHandle(SafeWaitHandle handle) => SafeWaitHandle = handle;
    }
}
