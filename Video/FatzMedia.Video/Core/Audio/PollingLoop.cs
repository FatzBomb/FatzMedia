namespace FatzMedia.Video.Core.Audio;

/// <summary>
/// A background thread that runs a callback, then waits an interval, until stopped. Platform
/// packages use it for their <see cref="Abstractions.IAudioDeviceMonitor"/> polling; the
/// callback's exceptions go to the error handler and never end the loop.
/// </summary>
public sealed class PollingLoop : IDisposable
{
    private readonly string _name;
    private readonly TimeSpan _interval;
    private readonly Action _tick;
    private readonly Action<Exception>? _onError;
    private readonly Lock _gate = new();
    private Thread? _thread;
    private CancellationTokenSource? _cancellation;

    /// <param name="name">The polling thread's name.</param>
    /// <param name="interval">The wait between callbacks (a negative value is treated as zero).</param>
    /// <param name="tick">The callback, run on the polling thread.</param>
    /// <param name="onError">Receives an exception thrown by <paramref name="tick"/>; omitted, it is swallowed.</param>
    public PollingLoop(string name, TimeSpan interval, Action tick, Action<Exception>? onError = null)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(tick);
        _name = name;
        _interval = interval < TimeSpan.Zero ? TimeSpan.Zero : interval;
        _tick = tick;
        _onError = onError;
    }

    /// <summary>Gets whether the polling thread is alive.</summary>
    public bool IsRunning
    {
        get
        {
            lock (_gate)
                return _thread is { IsAlive: true };
        }
    }

    /// <summary>
    /// Starts the thread unless it is already running. <paramref name="beforeStart"/> runs under
    /// the start/stop lock, before the first callback, only when the thread is actually started.
    /// </summary>
    public void Start(Action? beforeStart = null)
    {
        lock (_gate)
        {
            if (_thread is { IsAlive: true })
                return;

            beforeStart?.Invoke();
            var cancellation = new CancellationTokenSource();
            _cancellation = cancellation;
            _thread = new Thread(() => Run(cancellation.Token)) { IsBackground = true, Name = _name };
            _thread.Start();
        }
    }

    /// <summary>Stops the thread and waits up to the interval plus one second for it to end.</summary>
    public void Stop()
    {
        Thread? thread;
        CancellationTokenSource? cancellation;
        lock (_gate)
        {
            thread = _thread;
            cancellation = _cancellation;
            _thread = null;
            _cancellation = null;
        }

        if (thread is null)
            return;

        try
        {
            cancellation?.Cancel();
            // Stopped from the callback itself (a change handler disposing its owner): the loop
            // ends when the callback returns, and joining would only wait out the timeout.
            if (thread != Thread.CurrentThread && thread.Join(_interval + TimeSpan.FromSeconds(1)))
                cancellation?.Dispose();
        }
        catch (Exception)
        {
            // Stopping must never throw into a dispose path.
        }
    }

    public void Dispose() => Stop();

    private void Run(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                _tick();
            }
            catch (Exception exception)
            {
                try
                {
                    _onError?.Invoke(exception);
                }
                catch (Exception)
                {
                    // An error handler must not end the loop.
                }
            }

            if (token.WaitHandle.WaitOne(_interval))
                return;
        }
    }
}
