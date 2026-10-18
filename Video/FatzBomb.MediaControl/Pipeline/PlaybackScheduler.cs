namespace FatzBomb.MediaControl.Pipeline;

/// <summary>
/// Runs one cancellable loop at a time on a dedicated, reused worker thread. Starting a loop
/// cancels the current one; the new loop runs on the same thread as soon as the old one has
/// returned, so loops on one scheduler never overlap.
/// </summary>
/// <remarks>
/// Stage 022A slice 3: the owner's baseline showed every <c>VideoPipeline.SeekCore</c>
/// restarting the decode and presentation loops on new LongRunning threads, about 110 OS
/// threads per minute of scrubbing. The worker is created on the first <see cref="Start"/>,
/// parks between runs, and exits after <see cref="DefaultIdleTimeout"/> without work (or on
/// <see cref="Dispose"/>); the next <see cref="Start"/> creates a new one. A loop that does
/// not return within the <see cref="StopAndWait"/> timeout (a native call that ignores
/// cancellation) keeps its thread, and later starts get a new worker.
/// </remarks>
internal sealed class PlaybackScheduler(string name, TimeSpan? idleTimeout = null) : IDisposable
{
    public static readonly TimeSpan DefaultIdleTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DefaultStopTimeout = TimeSpan.FromSeconds(2);

    private readonly Lock _lock = new();
    private readonly TimeSpan _idleTimeout = idleTimeout ?? DefaultIdleTimeout;
    private Worker? _worker;
    private Run? _pending;
    private Run? _running;
    private bool _disposed;

    /// <summary>Gets how many worker threads this scheduler has created (diagnostics and tests).</summary>
    internal int WorkerThreadsCreated { get; private set; }

    /// <summary>Gets whether a worker thread currently exists (tests).</summary>
    internal bool HasWorker
    {
        get
        {
            lock (_lock)
                return _worker is not null;
        }
    }

    public void Start(Action<CancellationToken> loop)
    {
        ArgumentNullException.ThrowIfNull(loop);
        ObjectDisposedException.ThrowIf(_disposed, this);

        (Run? Pending, Run? Running) cancelled;
        lock (_lock)
        {
            if (_disposed)
                return;

            cancelled = DetachForCancelNoLock();
            _pending = new Run(loop);
            if (_worker is null)
                _worker = CreateWorkerNoLock();
            else
                _ = _worker.Wake.Set();
        }

        Cancel(cancelled);
    }

    /// <summary>Cancels the current loop without waiting for it to return.</summary>
    public void Stop()
    {
        (Run? Pending, Run? Running) cancelled;
        lock (_lock)
            cancelled = DetachForCancelNoLock();

        Cancel(cancelled);
    }

    /// <summary>
    /// Cancels the current loop and waits up to <paramref name="timeout"/> (2 s by default) for it
    /// to return. Called from the loop itself, it only cancels. If the loop does not return in
    /// time, its thread is left to finish it and the next start gets a new worker.
    /// </summary>
    public void StopAndWait(TimeSpan? timeout = null)
    {
        (Run? Pending, Run? Running) cancelled;
        Worker? worker;
        lock (_lock)
        {
            cancelled = DetachForCancelNoLock();
            worker = _worker;
        }

        Cancel(cancelled);
        Run? running = cancelled.Running;
        if (running is null || ReferenceEquals(Thread.CurrentThread, worker?.Thread))
            return;

        TimeSpan waitTimeout = timeout ?? DefaultStopTimeout;
        if (running.Completed.Wait(waitTimeout))
            return;

        lock (_lock)
        {
            // Abandon the stuck worker: it exits once its loop returns, and the next start creates
            // a new one instead of queueing behind it.
            if (ReferenceEquals(_running, running))
                _running = null;
            if (_worker is not null && ReferenceEquals(_worker, worker))
            {
                _ = _worker.Wake.Set();
                _worker = null;
            }
        }

        Report(MediaDiagnosticLevel.Warning, $"[{name}] Worker did not stop within {waitTimeout.TotalMilliseconds:F0}ms; later starts use a new worker thread.", null);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
                return;

            _disposed = true;
        }

        StopAndWait();
        lock (_lock)
            _ = _worker?.Wake.Set(); // a parked worker sees the disposal and exits
    }

    private Worker CreateWorkerNoLock()
    {
        var worker = new Worker();
        var thread = new Thread(() => WorkerLoop(worker)) { IsBackground = true, Name = $"FatzBombMedia.{name}" };
        try
        {
            // Presentation pacing is more sensitive to short stalls than decode buffering. Give the
            // presentation loop a modest priority bump without moving the whole app into real-time
            // scheduling territory.
            if (name.Contains("Presentation", StringComparison.OrdinalIgnoreCase))
                thread.Priority = ThreadPriority.AboveNormal;
        }
        catch
        {
            // Thread priority is a best-effort optimization only.
        }

        worker.Thread = thread;
        thread.Start(); // throws to the caller before the worker is recorded, if it fails
        WorkerThreadsCreated++;
        return worker;
    }

    private void WorkerLoop(Worker worker)
    {
        while (true)
        {
            Run? run;
            lock (_lock)
            {
                if (!ReferenceEquals(_worker, worker) || (_disposed && _pending is null))
                {
                    ExitNoLock(worker);
                    return;
                }

                run = _pending;
                _pending = null;
                _running = run;
            }

            if (run is not null)
            {
                Execute(run);
                continue;
            }

            if (worker.Wake.WaitOne(_idleTimeout))
                continue;

            // Idle: exit unless a start raced the timeout (it found this worker and only signalled).
            lock (_lock)
            {
                if (_pending is null && ReferenceEquals(_worker, worker))
                {
                    ExitNoLock(worker);
                    return;
                }
            }
        }
    }

    private void ExitNoLock(Worker worker)
    {
        if (ReferenceEquals(_worker, worker))
            _worker = null;
        worker.Wake.Dispose();
    }

    private void Execute(Run run)
    {
        try
        {
            if (!run.Cancellation.IsCancellationRequested)
                run.Loop(run.Cancellation.Token);
        }
        catch (OperationCanceledException) when (run.Cancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Report(MediaDiagnosticLevel.Error, $"[{name}] Worker failed: {ex.Message}", ex);
        }
        finally
        {
            lock (_lock)
            {
                // Not ours any more when StopAndWait abandoned this run after a timeout.
                if (ReferenceEquals(_running, run))
                    _running = null;
            }

            run.Complete();
        }
    }

    /// <summary>Takes the pending and running loops for cancellation outside the lock.</summary>
    private (Run? Pending, Run? Running) DetachForCancelNoLock()
    {
        Run? pending = _pending;
        _pending = null;
        return (pending, _running);
    }

    /// <summary>
    /// Cancels outside the lock, so token callbacks never run while the scheduler lock is held.
    /// A replaced pending run never started, so it is completed here.
    /// </summary>
    private static void Cancel((Run? Pending, Run? Running) runs)
    {
        if (runs.Pending is { } pending)
        {
            pending.Cancel();
            pending.Complete();
        }

        runs.Running?.Cancel();
    }

    // A worker thread must survive a throwing diagnostic subscriber: an exception escaping a
    // raw thread would end the process.
    private static void Report(MediaDiagnosticLevel level, string message, Exception? exception)
    {
        try
        {
            DiagnosticRouter.Raise(level, "PlaybackScheduler", message, innerException: exception);
        }
        catch
        {
        }
    }

    private sealed class Worker
    {
        public Thread? Thread { get; set; }
        public AutoResetEvent Wake { get; } = new(false);
    }

    private sealed class Run(Action<CancellationToken> loop)
    {
        private readonly CancellationTokenSource _cancellation = new();
        private int _completed;

        public Action<CancellationToken> Loop { get; } = loop;
        public CancellationTokenSource Cancellation => _cancellation;
        public ManualResetEventSlim Completed { get; } = new(false);

        public void Cancel()
        {
            try
            {
                _cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Completed and released between being detached and cancelled.
            }
        }

        public void Complete()
        {
            if (Interlocked.Exchange(ref _completed, 1) != 0)
                return;

            Completed.Set();
            _cancellation.Dispose();
        }
    }
}
