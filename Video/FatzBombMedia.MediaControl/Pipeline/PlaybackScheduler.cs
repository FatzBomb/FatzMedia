
using System.Diagnostics;

namespace FatzBombMedia.MediaControl.Pipeline;

internal sealed class PlaybackScheduler(string name) : IDisposable
{
    private readonly Lock _lock = new();
    private CancellationTokenSource? _cts;
    private Task? _task;
    private bool _disposed;

    public void Start(Action<CancellationToken> loop)
    {
        ArgumentNullException.ThrowIfNull(loop);
        ObjectDisposedException.ThrowIf(_disposed, this);

        Stop();

        var cts = new CancellationTokenSource();
        lock (_lock)
        {
            if (_disposed)
            {
                cts.Dispose();
                return;
            }

            _cts = cts;
            _task = Task.Factory.StartNew(
                () => RunLoop(loop, cts.Token),
                cts.Token,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
        }
    }

    public void Stop()
    {
        (CancellationTokenSource? cts, Task? task) = DetachCurrentRun();
        if (cts == null)
        {
            return;
        }

        CancelRun(cts);
        DisposeCancellationSourceWhenTaskCompletes(cts, task);
    }

    public void StopAndWait(TimeSpan? timeout = null)
    {
        (CancellationTokenSource? cts, Task? task) = DetachCurrentRun();
        if (cts == null)
        {
            return;
        }

        CancelRun(cts);
        if (task == null)
        {
            cts.Dispose();
            return;
        }

        if (task.Id == Task.CurrentId)
        {
            DisposeCancellationSourceWhenTaskCompletes(cts, task);
            return;
        }

        var disposeCancellationSource = true;
        try
        {
            TimeSpan waitTimeout = timeout ?? TimeSpan.FromSeconds(2);
            if (!task.Wait(waitTimeout))
            {
                Debug.WriteLine($"[PlaybackScheduler:{name}] Worker did not stop within {waitTimeout.TotalMilliseconds:F0}ms.");
                DiagnosticRouter.Raise(MediaDiagnosticLevel.Warning, "PlaybackScheduler", $"[{name}] Worker did not stop within {waitTimeout.TotalMilliseconds:F0}ms.");
                disposeCancellationSource = false;
                DisposeCancellationSourceWhenTaskCompletes(cts, task);
                return;
            }

            _ = task.Exception;
        }
        catch (AggregateException ex)
        {
            Debug.WriteLine($"[PlaybackScheduler:{name}] Worker stopped with error: {ex.GetBaseException().Message}");
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Warning, "PlaybackScheduler", $"[{name}] Worker stopped with error: {ex.GetBaseException().Message}", innerException: ex);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[PlaybackScheduler:{name}] Worker wait failed: {ex.Message}");
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Warning, "PlaybackScheduler", $"[{name}] Worker wait failed: {ex.Message}", innerException: ex);
        }
        finally
        {
            if (disposeCancellationSource)
            {
                cts.Dispose();
            }
        }
    }

    private (CancellationTokenSource? Cts, Task? Task) DetachCurrentRun()
    {
        lock (_lock)
        {
            CancellationTokenSource? cts = _cts;
            Task? task = _task;
            _cts = null;
            _task = null;
            return (cts, task);
        }
    }

    private static void CancelRun(CancellationTokenSource cts)
    {
        try
        {
            cts.Cancel();
        }
        catch
        {
            // Best-effort cancellation during teardown.
        }
    }

    private static void DisposeCancellationSourceWhenTaskCompletes(CancellationTokenSource cts, Task? task)
    {
        if (task == null)
        {
            cts.Dispose();
            return;
        }

        _ = task.ContinueWith(
            completed =>
            {
                try
                {
                    _ = completed.Exception;
                }
                finally
                {
                    cts.Dispose();
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void ConfigureWorkerThread()
    {
        try
        {
            Thread.CurrentThread.Name ??= $"FatzBombMedia.{name}";

            // Presentation pacing is more sensitive to short stalls than decode buffering. Give the
            // presentation loop a modest priority bump without moving the whole app into real-time
            // scheduling territory.
            if (name.Contains("Presentation", StringComparison.OrdinalIgnoreCase))
            {
                Thread.CurrentThread.Priority = ThreadPriority.AboveNormal;
            }
        }
        catch
        {
            // Thread naming/priority is a best-effort optimization only.
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopAndWait();
    }

    private void RunLoop(Action<CancellationToken> loop, CancellationToken cancellationToken)
    {
        try
        {
            ConfigureWorkerThread();
            loop(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[PlaybackScheduler:{name}] Worker failed: {ex.Message}");
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Error, "PlaybackScheduler", $"[{name}] Worker failed: {ex.Message}", innerException: ex);
        }
    }
}
