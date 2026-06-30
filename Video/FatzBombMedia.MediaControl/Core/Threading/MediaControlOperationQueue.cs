using System.Collections.Concurrent;
using System.Diagnostics;

namespace FatzBombMedia.MediaControl.Core.Threading;

internal sealed class MediaControlOperationQueue : IDisposable
{
    private readonly BlockingCollection<Action> _queue = new(new ConcurrentQueue<Action>());
    private readonly Thread _workerThread;
    private int _disposed;

    public MediaControlOperationQueue(string name)
    {
        _workerThread = new Thread(Run)
        {
            IsBackground = true,
            Name = $"FatzBombMedia.{name}"
        };
        _workerThread.Start();
    }

    public Task<T> Enqueue<T>(Func<T> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);

        if (Volatile.Read(ref _disposed) != 0)
            return Task.FromException<T>(new ObjectDisposedException(nameof(MediaControlOperationQueue)));

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            _queue.Add(() =>
            {
                try
                {
                    _ = completion.TrySetResult(operation());
                }
                catch (Exception ex)
                {
                    _ = completion.TrySetException(ex);
                }
            });
        }
        catch (InvalidOperationException ex)
        {
            _ = completion.TrySetException(new ObjectDisposedException(nameof(MediaControlOperationQueue), ex));
        }

        return completion.Task;
    }

    public Task<T> Enqueue<T>(Func<Task<T>> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);

        return Enqueue(() => operation().GetAwaiter().GetResult());
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

    private void Run()
    {
        try
        {
            foreach (Action operation in _queue.GetConsumingEnumerable())
                operation();
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MediaControlOperationQueue] Worker exited with error: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _queue.CompleteAdding();
        if (!ReferenceEquals(Thread.CurrentThread, _workerThread) && _workerThread.IsAlive)
            _ = _workerThread.Join(TimeSpan.FromSeconds(2));

        _queue.Dispose();
    }
}
