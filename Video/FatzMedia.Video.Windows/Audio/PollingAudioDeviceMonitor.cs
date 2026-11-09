using FatzMedia.Video.Core.Abstractions;
using FatzMedia.Video.Core.Audio;
using ManagedBass;
using MBass = ManagedBass.Bass;

namespace FatzMedia.Video.Windows.Audio;

internal sealed class PollingAudioDeviceMonitor : IAudioDeviceMonitor
{
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(1);

    private readonly PollingLoop _loop;
    private long _lastSampleRate;
    private int _lastDevice;
    private string? _lastDeviceName;
    private string? _lastDefaultEndpointId;
    private string? _lastDefaultEndpointName;

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

    private void CaptureBaseline()
    {
        if (!MBass.GetInfo(out BassInfo info))
        {
            _lastSampleRate = 0;
            _lastDevice = -1;
            _lastDeviceName = null;
            return;
        }

        _lastSampleRate = info.SampleRate;
        _lastDevice = MBass.CurrentDevice;
        if (MBass.GetDeviceInfo(_lastDevice, out DeviceInfo deviceInfo))
            _lastDeviceName = deviceInfo.Name;

        if (CoreAudioDefaultEndpointReader.TryGetDefaultRenderEndpoint(out var endpointId, out var endpointName))
        {
            _lastDefaultEndpointId = endpointId;
            _lastDefaultEndpointName = endpointName;
        }
    }

    private void CheckForChanges()
    {
        if (!MBass.GetInfo(out BassInfo info))
            return;

        var currentSampleRate = info.SampleRate;
        var currentDevice = MBass.CurrentDevice;
        var currentDeviceName = _lastDeviceName;
        if (MBass.GetDeviceInfo(currentDevice, out DeviceInfo deviceInfo))
            currentDeviceName = deviceInfo.Name;

        var currentDefaultEndpointId = _lastDefaultEndpointId;
        var currentDefaultEndpointName = _lastDefaultEndpointName;
        if (CoreAudioDefaultEndpointReader.TryGetDefaultRenderEndpoint(out var endpointId, out var endpointName))
        {
            currentDefaultEndpointId = endpointId;
            currentDefaultEndpointName = endpointName;
        }

        AudioDeviceChangeInfo? detected = AudioDeviceChangeDetector.Detect(
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
