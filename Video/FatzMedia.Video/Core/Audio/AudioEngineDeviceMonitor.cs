using FatzMedia.Video.Core.Abstractions;

namespace FatzMedia.Video.Core.Audio;

/// <summary>
/// Audio-engine capability: the engine watches the output devices itself and reports each change once.
/// </summary>
interface IAudioDeviceChangeSource
{
    /// <summary>
    /// Raised on a background thread (the engine's own, or its device-list watch) when devices are added or
    /// removed, the default changes, or the engine's output moved to another device.
    /// </summary>
    event EventHandler<AudioDeviceChangeInfo>? DeviceChanged;
}

/// <summary>The player's device monitor: the audio engine's own device events (<see cref="IAudioDeviceChangeSource"/>).</summary>
sealed class AudioEngineDeviceMonitor(IAudioDeviceChangeSource engine) : IAudioDeviceMonitor
{
    readonly Lock _gate = new();
    bool _running;
    bool _disposed;

    public event EventHandler<AudioDeviceChangeInfo>? DeviceChanged;

    public bool IsRunning
    {
        get
        {
            lock (_gate)
                return _running;
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_disposed || _running)
                return;

            _running = true;
            engine.DeviceChanged += OnDeviceChanged;
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (!_running)
                return;

            _running = false;
            engine.DeviceChanged -= OnDeviceChanged;
        }
    }

    public void Dispose()
    {
        Stop();
        lock (_gate)
            _disposed = true;
    }

    void OnDeviceChanged(object? sender, AudioDeviceChangeInfo info) => DeviceChanged?.Invoke(this, info);
}
