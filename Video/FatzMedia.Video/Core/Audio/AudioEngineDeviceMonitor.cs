using FatzMedia.Video.Core.Abstractions;

namespace FatzMedia.Video.Core.Audio;

/// <summary>
/// Audio-engine capability (037A slice 2): the engine follows the output device itself and reports
/// each move. An engine created by an <see cref="IAudioEngineFactory"/> implements it when it can.
/// </summary>
interface IAudioDeviceChangeSource
{
    /// <summary>Raised on the engine's own thread when its output moved to another device.</summary>
    event EventHandler<AudioDeviceChangeInfo>? DeviceChanged;
}

/// <summary>
/// The player's device monitor when an <see cref="IAudioEngineFactory"/> engine plays file audio:
/// the engine's own device moves (<see cref="IAudioDeviceChangeSource"/>) plus the platform's
/// monitor, which still watches BASS for video audio. Either may be missing.
/// </summary>
sealed class AudioEngineDeviceMonitor(IAudioDeviceChangeSource? engine, IAudioDeviceMonitor? platform) : IAudioDeviceMonitor
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
            engine?.DeviceChanged += OnDeviceChanged;
            platform?.DeviceChanged += OnDeviceChanged;
        }

        platform?.Start();
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (!_running)
                return;

            _running = false;
            engine?.DeviceChanged -= OnDeviceChanged;
            platform?.DeviceChanged -= OnDeviceChanged;
        }

        platform?.Stop();
    }

    public void Dispose()
    {
        Stop();
        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
        }

        platform?.Dispose();
    }

    void OnDeviceChanged(object? sender, AudioDeviceChangeInfo info) => DeviceChanged?.Invoke(this, info);
}
