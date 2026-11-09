using FatzMedia.Video.Core.Abstractions;
using FatzMedia.Video.Core.Audio;
using MBass = ManagedBass.Bass;

namespace FatzMedia.Video.Windows.Audio;

sealed class PollingAudioDeviceMonitor : IAudioDeviceMonitor
{
    static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(1);

    readonly PollingLoop _loop;
    long _lastSampleRate;
    int _lastDevice;
    string? _lastDeviceName;
    string? _lastDefaultEndpointId;
    string? _lastDefaultEndpointName;

    public PollingAudioDeviceMonitor()
        : this(DefaultPollInterval)
    {
    }

    internal PollingAudioDeviceMonitor(TimeSpan pollInterval)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(pollInterval, TimeSpan.Zero);
        _loop = new PollingLoop("AudioDeviceMonitor", pollInterval, CheckForChanges);
    }

    public event EventHandler<AudioDeviceChangeInfo>? DeviceChanged;

    public bool IsRunning => _loop.IsRunning;

    public void Start() => _loop.Start(CaptureBaseline);

    public void Stop() => _loop.Stop();

    public void Dispose() => Stop();

    void CaptureBaseline()
    {
        if (!MBass.GetInfo(out var info))
        {
            _lastSampleRate = 0;
            _lastDevice = -1;
            _lastDeviceName = null;
            return;
        }

        _lastSampleRate = info.SampleRate;
        _lastDevice = MBass.CurrentDevice;
        if (MBass.GetDeviceInfo(_lastDevice, out var deviceInfo))
            _lastDeviceName = deviceInfo.Name;

        if (CoreAudioDefaultEndpointReader.TryGetDefaultRenderEndpoint(out var endpointId, out var endpointName))
        {
            _lastDefaultEndpointId = endpointId;
            _lastDefaultEndpointName = endpointName;
        }
    }

    void CheckForChanges()
    {
        if (!MBass.GetInfo(out var info))
            return;

        var currentSampleRate = info.SampleRate;
        var currentDevice = MBass.CurrentDevice;
        var currentDeviceName = _lastDeviceName;
        if (MBass.GetDeviceInfo(currentDevice, out var deviceInfo))
            currentDeviceName = deviceInfo.Name;

        var currentDefaultEndpointId = _lastDefaultEndpointId;
        var currentDefaultEndpointName = _lastDefaultEndpointName;
        if (CoreAudioDefaultEndpointReader.TryGetDefaultRenderEndpoint(out var endpointId, out var endpointName))
        {
            currentDefaultEndpointId = endpointId;
            currentDefaultEndpointName = endpointName;
        }

        var detected = AudioDeviceChangeDetector.Detect(
            _lastSampleRate, _lastDevice, _lastDeviceName,
            _lastDefaultEndpointId, _lastDefaultEndpointName,
            currentSampleRate, currentDevice, currentDeviceName,
            currentDefaultEndpointId, currentDefaultEndpointName);

        if (detected == null)
            return;

        _lastDevice = currentDevice;
        _lastDeviceName = currentDeviceName;
        _lastSampleRate = currentSampleRate;
        _lastDefaultEndpointId = currentDefaultEndpointId;
        _lastDefaultEndpointName = currentDefaultEndpointName;
        DeviceChanged?.Invoke(this, detected);
    }
}
