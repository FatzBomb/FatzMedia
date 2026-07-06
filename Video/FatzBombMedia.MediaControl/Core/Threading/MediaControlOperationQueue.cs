using System.Diagnostics;
using System.Threading.Channels;

namespace FatzBombMedia.MediaControl.Core.Threading;

internal sealed class MediaControlOperationQueue : IDisposable
{
    private readonly Channel<Func<Task>> _queue = Channel.CreateUnbounded<Func<Task>>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false
    });
    private readonly AsyncLocal<bool> _isWorkerContext = new();
    private readonly Task _workerTask;
    private readonly string _name;
    private int _disposed;

    public MediaControlOperationQueue(string name)
    {
        _name = string.IsNullOrWhiteSpace(name) ? "MediaControl" : name;
        _workerTask = Task.Run(RunAsync);
    }

    public Task<T> Enqueue<T>(Func<T> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);

        if (Volatile.Read(ref _disposed) != 0)
            return Task.FromException<T>(new ObjectDisposedException(nameof(MediaControlOperationQueue)));

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

        if (!_queue.Writer.TryWrite(() =>
        {
            try
            {
                _ = completion.TrySetResult(operation());
            }
            catch (Exception ex)
            {
                _ = completion.TrySetException(ex);
            }

            return Task.CompletedTask;
        }))
        {
            _ = completion.TrySetException(new ObjectDisposedException(nameof(MediaControlOperationQueue)));
        }

        return completion.Task;
    }

    public Task<T> Enqueue<T>(Func<Task<T>> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);

        if (Volatile.Read(ref _disposed) != 0)
            return Task.FromException<T>(new ObjectDisposedException(nameof(MediaControlOperationQueue)));

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

        if (!_queue.Writer.TryWrite(async () =>
        {
            try
            {
                _ = completion.TrySetResult(await operation().ConfigureAwait(false));
            }
            catch (Exception ex)
            {
                _ = completion.TrySetException(ex);
            }
        }))
        {
            _ = completion.TrySetException(new ObjectDisposedException(nameof(MediaControlOperationQueue)));
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

    private async Task RunAsync()
    {
        _isWorkerContext.Value = true;
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
                    Debug.WriteLine($"[MediaControlOperationQueue:{_name}] Operation failed after completion handling: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MediaControlOperationQueue:{_name}] Worker exited with error: {ex.Message}");
        }
        finally
        {
            _isWorkerContext.Value = false;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _ = _queue.Writer.TryComplete();
        if (_isWorkerContext.Value)
        {
            _ = ObserveWorkerCompletionAsync();
            return;
        }

        try
        {
            if (!_workerTask.Wait(TimeSpan.FromSeconds(2)))
            {
                Debug.WriteLine($"[MediaControlOperationQueue:{_name}] Worker did not stop within 2000ms.");
                _ = ObserveWorkerCompletionAsync();
                return;
            }

            _ = _workerTask.Exception;
        }
        catch (AggregateException ex)
        {
            Debug.WriteLine($"[MediaControlOperationQueue:{_name}] Worker stopped with error: {ex.GetBaseException().Message}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MediaControlOperationQueue:{_name}] Worker wait failed: {ex.Message}");
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
            Debug.WriteLine($"[MediaControlOperationQueue:{_name}] Worker stopped with error: {ex.Message}");
        }
    }
}
