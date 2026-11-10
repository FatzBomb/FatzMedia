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

    /// <summary>
    /// True when <see cref="DeviceChanged"/> also reports devices added and removed and every default
    /// change (the FatzAudio engine watches the device list itself): the platform's monitor, which watches
    /// BASS, is then not used, so each change is reported once.
    /// </summary>
    bool ReportsDeviceList => false;
}

/// <summary>
/// The player's device monitor when an <see cref="IAudioEngineFactory"/> engine plays file audio:
/// the engine's own device events (<see cref="IAudioDeviceChangeSource"/>) plus the platform's
/// monitor unless the engine watches the device list itself. Either may be missing.
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
