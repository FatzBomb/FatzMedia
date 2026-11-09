using System.Diagnostics;
using System.Threading.Channels;

namespace FatzMedia.Video.Core.Threading;

internal sealed class PlayerOperationQueue : IDisposable
{
    // ENG-024: which operation (and which event handler inside it) the current code runs in. A
    // scope ends when its operation or handler returns, so work it started that outlives it (a
    // task, a timer, a UI-thread callback that captured the execution context) is no longer
    // "inside" and is serialized like any other caller.
    private static readonly AsyncLocal<OperationScope?> CurrentScope = new();

    private readonly Channel<Func<Task>> _queue = Channel.CreateUnbounded<Func<Task>>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false
    });
    private readonly Task _workerTask;
    private readonly string _name;
    private int _disposed;
    private int _exclusiveTurnThreadId;

    /// <summary>
    /// Gets whether the calling code runs inside one of this queue's operations: on the worker
    /// (or, for an asynchronous operation, in its flow) while the operation runs, in an event
    /// handler it raises, or on the thread that holds an exclusive turn. Work started from there
    /// that runs on another thread, or after the operation or handler returned, is outside.
    /// </summary>
    internal bool IsExecutingOperation =>
        Volatile.Read(ref _exclusiveTurnThreadId) == Environment.CurrentManagedThreadId || IsInScope(this);

    public PlayerOperationQueue(string name)
    {
        _name = string.IsNullOrWhiteSpace(name) ? "MediaControl" : name;
        _workerTask = Task.Run(RunAsync);
    }

    public Task<T> Enqueue<T>(Func<T> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);

        if (Volatile.Read(ref _disposed) != 0)
            return Task.FromException<T>(new ObjectDisposedException(nameof(PlayerOperationQueue)));

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

        if (!_queue.Writer.TryWrite(() =>
        {
            // A synchronous operation runs on the worker thread only.
            OperationScope? previous = CurrentScope.Value;
            var scope = new OperationScope(parent: null, this, Environment.CurrentManagedThreadId);
            CurrentScope.Value = scope;
            try
            {
                _ = completion.TrySetResult(operation());
            }
            catch (OperationCanceledException ex)
            {
                _ = completion.TrySetCanceled(ex.CancellationToken);
            }
            catch (Exception ex)
            {
                _ = completion.TrySetException(ex);
            }
            finally
            {
                scope.End();
                CurrentScope.Value = previous;
            }

            return Task.CompletedTask;
        }))
        {
            _ = completion.TrySetException(new ObjectDisposedException(nameof(PlayerOperationQueue)));
        }

        return completion.Task;
    }

    public Task<T> Enqueue<T>(Func<Task<T>> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);

        if (Volatile.Read(ref _disposed) != 0)
            return Task.FromException<T>(new ObjectDisposedException(nameof(PlayerOperationQueue)));

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

        if (!_queue.Writer.TryWrite(async () =>
        {
            // An asynchronous operation continues on pool threads after an await, so its scope
            // follows its flow (any thread) until it completes. Set inside this async lambda, the
            // value does not leak back into the worker loop.
            var scope = new OperationScope(parent: null, this, threadId: 0);
            CurrentScope.Value = scope;
            try
            {
                _ = completion.TrySetResult(await operation().ConfigureAwait(false));
            }
            catch (OperationCanceledException ex)
            {
                _ = completion.TrySetCanceled(ex.CancellationToken);
            }
            catch (Exception ex)
            {
                _ = completion.TrySetException(ex);
            }
            finally
            {
                scope.End();
            }
        }))
        {
            _ = completion.TrySetException(new ObjectDisposedException(nameof(PlayerOperationQueue)));
        }

        return completion.Task;
    }

    public Task Enqueue(Action operation)
    {
        ArgumentNullException.ThrowIfNull(operation);

        return Enqueue(() =>
        {
            operation();
            return true;
        });
    }

    /// <summary>
    /// Runs <paramref name="operation"/> synchronously, serialized with every other queued
    /// operation so synchronous and asynchronous callers cannot interleave. When already executing
    /// inside an operation of this queue the operation runs directly (no re-queueing, so re-entrant
    /// calls from within a queued operation cannot deadlock). Otherwise an exclusive turn is queued,
    /// then the operation runs on the calling thread while the worker waits. This preserves
    /// caller-thread event behavior without allowing later operations to interleave.
    /// </summary>
    public T Execute<T>(Func<T> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return BeginExclusiveTurn().Run(operation);
    }

    /// <summary>
    /// Queues an exclusive turn without waiting for it, so a caller can take its place in the
    /// queue under a short lock and wait outside it (ENG-024). The caller must either
    /// <see cref="ExclusiveTurn.Run"/> it or have <see cref="ExclusiveTurn.TryWait"/> return
    /// <see langword="false"/>; a granted turn that is never run blocks the queue.
    /// Inside an operation of this queue the turn is inline: it is granted at once and runs directly.
    /// </summary>
    public ExclusiveTurn BeginExclusiveTurn()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(PlayerOperationQueue));

        if (IsExecutingOperation)
            return new ExclusiveTurn(this, inline: true);

        var turn = new ExclusiveTurn(this, inline: false);
        if (!_queue.Writer.TryWrite(turn.RunOnWorkerAsync))
            throw new ObjectDisposedException(nameof(PlayerOperationQueue));

        return turn;
    }

    /// <summary>
    /// Marks the start of an event handler call (see <see cref="HostEventDispatcher"/>). Inside an
    /// operation the handler still counts as inside it, but only on its own thread and only until
    /// it returns: what it hands to another thread, or what runs after it returned, does not.
    /// Outside any operation this does nothing.
    /// </summary>
    internal static HandlerScope EnterHandlerScope()
    {
        OperationScope? parent = CurrentScope.Value;
        if (parent is null || !parent.IsActive)
            return default;

        var scope = new OperationScope(parent, queue: null, Environment.CurrentManagedThreadId);
        CurrentScope.Value = scope;
        return new HandlerScope(scope, parent);
    }

    private static bool IsInScope(PlayerOperationQueue queue)
    {
        var threadId = Environment.CurrentManagedThreadId;
        for (OperationScope? scope = CurrentScope.Value; scope is not null; scope = scope.Parent)
        {
            // An ended scope, or one bound to another thread, cuts the chain: what was captured
            // inside it is not inside the scopes below it either.
            if (!scope.IsActive || (scope.ThreadId != 0 && scope.ThreadId != threadId))
                return false;

            if (ReferenceEquals(scope.Queue, queue))
                return true;
        }

        return false;
    }

    private async Task RunAsync()
    {
        try
        {
            await foreach (Func<Task> operation in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                try
                {
                    await operation().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[PlayerOperationQueue:{_name}] Operation failed after completion handling: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Error, "PlayerOperationQueue", $"[{_name}] Worker exited with error: {ex.Message}", innerException: ex);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _ = _queue.Writer.TryComplete();
        if (IsInScope(this))
        {
            // Disposed from one of its own operations: the worker finishes after it returns.
            _ = ObserveWorkerCompletionAsync();
            return;
        }

        try
        {
            if (!_workerTask.Wait(TimeSpan.FromSeconds(2)))
            {
                DiagnosticRouter.Raise(MediaDiagnosticLevel.Warning, "PlayerOperationQueue", $"[{_name}] Worker did not stop within 2000ms.");
                _ = ObserveWorkerCompletionAsync();
                return;
            }

            _ = _workerTask.Exception;
        }
        catch (AggregateException ex)
        {
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Warning, "PlayerOperationQueue", $"[{_name}] Worker stopped with error: {ex.GetBaseException().Message}", innerException: ex);
        }
        catch (Exception ex)
        {
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Warning, "PlayerOperationQueue", $"[{_name}] Worker wait failed: {ex.Message}", innerException: ex);
        }
    }

    private async Task ObserveWorkerCompletionAsync()
    {
        try
        {
            await _workerTask.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Warning, "PlayerOperationQueue", $"[{_name}] Worker stopped with error: {ex.Message}", innerException: ex);
        }
    }

    /// <param name="threadId">The only thread the scope applies on, or 0 for any thread.</param>
    internal sealed class OperationScope(OperationScope? parent, PlayerOperationQueue? queue, int threadId)
    {
        private volatile bool _active = true;

        public OperationScope? Parent { get; } = parent;

        /// <summary>The queue whose operation this is, or <see langword="null"/> for an event handler.</summary>
        public PlayerOperationQueue? Queue { get; } = queue;

        public int ThreadId { get; } = threadId;

        public bool IsActive => _active;

        public void End() => _active = false;
    }

    /// <summary>Ends an event handler's scope (see <see cref="EnterHandlerScope"/>).</summary>
    internal readonly struct HandlerScope : IDisposable
    {
        private readonly OperationScope? _scope;
        private readonly OperationScope? _parent;

        internal HandlerScope(OperationScope scope, OperationScope? parent)
        {
            _scope = scope;
            _parent = parent;
        }

        public void Dispose()
        {
            if (_scope is null)
                return;

            _scope.End();
            CurrentScope.Value = _parent;
        }
    }

    /// <summary>
    /// One exclusive turn on the queue: the worker waits while the owner runs an operation on its
    /// own thread. A turn not yet granted can be abandoned (<see cref="TryWait"/>), and the worker
    /// then skips it.
    /// </summary>
    internal sealed class ExclusiveTurn
    {
        private const int Pending = 0;
        private const int Granted = 1;
        private const int Abandoned = 2;

        private readonly PlayerOperationQueue _queue;
        private readonly bool _inline;
        private readonly TaskCompletionSource _granted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _state;

        internal ExclusiveTurn(PlayerOperationQueue queue, bool inline)
        {
            _queue = queue;
            _inline = inline;
        }

        internal async Task RunOnWorkerAsync()
        {
            if (Interlocked.CompareExchange(ref _state, Granted, Pending) != Pending)
                return;

            _ = _granted.TrySetResult();
            await _completed.Task.ConfigureAwait(false);
        }

        /// <summary>
        /// Waits up to <paramref name="timeout"/> for the turn. <see langword="true"/>: the turn is
        /// granted and the caller must <see cref="Run"/> it. <see langword="false"/>: it was not
        /// granted in time and is abandoned; the worker skips it.
        /// </summary>
        public bool TryWait(TimeSpan timeout)
        {
            if (_inline || _granted.Task.Wait(timeout))
                return true;

            if (Interlocked.CompareExchange(ref _state, Abandoned, Pending) == Pending)
                return false;

            // Granted while we gave up: take it.
            _granted.Task.Wait();
            return true;
        }

        /// <summary>Waits for the turn (without a bound), runs <paramref name="operation"/> on this thread, and ends the turn.</summary>
        public T Run<T>(Func<T> operation)
        {
            ArgumentNullException.ThrowIfNull(operation);
            if (_inline)
                return operation();

            try
            {
                _granted.Task.GetAwaiter().GetResult();
                Volatile.Write(ref _queue._exclusiveTurnThreadId, Environment.CurrentManagedThreadId);
                try
                {
                    return operation();
                }
                finally
                {
                    Volatile.Write(ref _queue._exclusiveTurnThreadId, 0);
                }
            }
            finally
            {
                _ = _completed.TrySetResult();
            }
        }
    }
}
