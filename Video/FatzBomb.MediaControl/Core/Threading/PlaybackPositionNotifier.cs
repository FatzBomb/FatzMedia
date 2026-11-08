namespace FatzBomb.MediaControl.Core.Threading;

internal sealed class PlaybackPositionNotifier : IDisposable
{
    internal static readonly TimeSpan DefaultInterval = TimeSpan.FromMilliseconds(250);

    private readonly Lock _sync = new();
    private readonly Func<double> _positionSeconds;
    private readonly Func<double> _durationSeconds;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _interval;
    private CancellationTokenSource? _cancellation;
    private Task? _task;
    private int _generation;
    private bool _isRunning;
    private bool _disposed;

    public PlaybackPositionNotifier(
        Func<double> positionSeconds,
        Func<double> durationSeconds,
        TimeProvider? timeProvider = null,
        TimeSpan? interval = null)
    {
        _positionSeconds = positionSeconds ?? throw new ArgumentNullException(nameof(positionSeconds));
        _durationSeconds = durationSeconds ?? throw new ArgumentNullException(nameof(durationSeconds));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _interval = interval ?? DefaultInterval;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_interval, TimeSpan.Zero);
    }

    public event EventHandler<MediaPositionChangedEventArgs>? PositionChanged;

    public void Start()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_isRunning)
                return;

            _isRunning = true;
            var generation = ++_generation;
            _cancellation = new CancellationTokenSource();
            // Read the token now: a Stop() before the task runs clears (and later disposes) the field.
            CancellationToken token = _cancellation.Token;
            _task = Task.Run(() => RunAsync(generation, token));
        }
    }

    public void Stop()
    {
        Task? task;
        CancellationTokenSource? cancellation;
        lock (_sync)
        {
            if (_disposed || !_isRunning)
                return;

            _isRunning = false;
            _generation++;
            cancellation = _cancellation;
            task = _task;
            _cancellation = null;
            _task = null;
            cancellation?.Cancel();
        }

        DisposeCancellationAfterCompletion(task, cancellation);
    }

    public void PublishNow() => PublishCurrentPosition();

    private async Task RunAsync(int generation, CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(_interval, _timeProvider);
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                lock (_sync)
                {
                    if (_disposed || !_isRunning || generation != _generation)
                        return;
                }

                PublishCurrentPosition();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void PublishCurrentPosition()
    {
        if (Volatile.Read(ref _disposed))
            return;

        var position = CleanSeconds(_positionSeconds());
        var duration = CleanSeconds(_durationSeconds());
        HostEventDispatcher.Raise(
            this,
            PositionChanged,
            new MediaPositionChangedEventArgs(
                TimeSpan.FromSeconds(position),
                TimeSpan.FromSeconds(duration)),
            nameof(PositionChanged));
    }

    public void Dispose()
    {
        Task? task;
        CancellationTokenSource? cancellation;
        lock (_sync)
        {
            if (_disposed)
                return;

            _disposed = true;
            _isRunning = false;
            _generation++;
            cancellation = _cancellation;
            task = _task;
            cancellation?.Cancel();
        }

        DisposeCancellationAfterCompletion(task, cancellation);
    }

    private static void DisposeCancellationAfterCompletion(Task? task, CancellationTokenSource? cancellation)
    {
        if (cancellation == null)
            return;

        if (task == null || task.IsCompleted)
        {
            cancellation.Dispose();
            return;
        }

        _ = task.ContinueWith(
            static (_, state) => ((CancellationTokenSource)state!).Dispose(),
            cancellation,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static double CleanSeconds(double seconds) =>
        double.IsFinite(seconds) && seconds > 0 ? seconds : 0;
}
