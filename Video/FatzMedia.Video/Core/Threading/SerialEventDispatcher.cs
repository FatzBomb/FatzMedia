using System.Diagnostics;
using System.Threading.Channels;

namespace FatzMedia.Video.Core.Threading;

/// <summary>
/// Delivers host notifications one at a time, in the order they were posted, on a worker of its
/// own. A player uses it for <c>MediaDiagnostic</c> so diagnostics raised during a long control
/// operation (network retries, the connect) reach the host while that operation runs, instead of
/// queueing behind it on the control queue (ENG-024 follow-up).
/// </summary>
/// <remarks>
/// <see cref="Dispose"/> stops accepting work, waits a bounded time for what is already queued to
/// be delivered, and then drops the rest: no delivery starts after it returns (short of one the
/// worker was starting as the bound ran out, when a handler blocked it). Called from a delivery on
/// its own worker, it does not wait (it would wait for itself). The worker does not
/// capture the creator's execution context, so a delivery never runs "inside" a control operation
/// or carries a diagnostic owner.
/// </remarks>
sealed class SerialEventDispatcher : IDisposable
{
    readonly Channel<Action> _queue = Channel.CreateUnbounded<Action>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false,
    });
    readonly string _name;
    readonly TimeSpan _drainTimeout;
    readonly Task _worker;
    volatile bool _closed;
    int _deliveringThreadId;
    int _disposed;

    public SerialEventDispatcher(string name, TimeSpan drainTimeout)
    {
        _name = name;
        _drainTimeout = drainTimeout;
        using (ExecutionContext.SuppressFlow())
            _worker = Task.Run(RunAsync);
    }

    /// <summary>
    /// Queues <paramref name="delivery"/>. Returns <see langword="false"/> (and drops it) once the
    /// instance is disposed. A delivery that throws is logged and does not stop the next one.
    /// </summary>
    public bool Post(Action delivery)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        return !_closed && _queue.Writer.TryWrite(delivery);
    }

    /// <summary>Completes when everything posted before this call has been delivered (or dropped).</summary>
    public Task FlushAsync()
    {
        var flushed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!Post(() => flushed.TrySetResult()))
            _ = flushed.TrySetResult();

        return flushed.Task;
    }

    async Task RunAsync()
    {
        await foreach (var delivery in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            if (_closed)
                break;

            Volatile.Write(ref _deliveringThreadId, Environment.CurrentManagedThreadId);
            try
            {
                delivery();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SerialEventDispatcher:{_name}] Delivery failed: {ex.Message}");
            }
            finally
            {
                Volatile.Write(ref _deliveringThreadId, 0);
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _ = _queue.Writer.TryComplete();
        if (Volatile.Read(ref _deliveringThreadId) != Environment.CurrentManagedThreadId)
        {
            try
            {
                if (!_worker.Wait(_drainTimeout))
                    Debug.WriteLine($"[SerialEventDispatcher:{_name}] Undelivered notifications dropped after {_drainTimeout.TotalMilliseconds:0} ms.");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SerialEventDispatcher:{_name}] Worker wait failed: {ex.Message}");
            }
        }

        _closed = true;
    }
}
