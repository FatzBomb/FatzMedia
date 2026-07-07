using FatzBombMedia.MediaControl.Core.Abstractions;
using ManagedBass;
using System.Diagnostics;
using MBass = ManagedBass.Bass;

namespace FatzBombMedia.MediaControl.Core.Audio;

internal sealed class PollingAudioDeviceMonitor : IAudioDeviceMonitor
{
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(1);

    private readonly TimeSpan _pollInterval;
    private readonly Lock _gate = new();
    private Thread? _thread;
    private volatile bool _stopRequested;
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
        _pollInterval = pollInterval;
    }

    public event EventHandler<AudioDeviceChangeInfo>? DeviceChanged;

    public bool IsRunning
    {
        get
        {
            lock (_gate)
                return _thread is { IsAlive: true };
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_thread is { IsAlive: true })
                return;

            _stopRequested = false;
            CaptureBaseline();
            _thread = new Thread(PollLoop)
            {
                IsBackground = true,
                Name = "AudioDeviceMonitor",
            };
            _thread.Start();
        }
    }

    public void Stop()
    {
        Thread? thread;
        lock (_gate)
        {
            _stopRequested = true;
            thread = _thread;
            _thread = null;
        }

        if (thread == null)
            return;

        try
        {
            _ = thread.Join(_pollInterval + TimeSpan.FromSeconds(1));
        }
        catch
        {
        }
    }

    public void Dispose() => Stop();

    internal void OnPollTick() => CheckForChanges();

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

        AudioDeviceChangeInfo? detected = DetectChange(
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

    internal static AudioDeviceChangeInfo? DetectChange(
        long lastSampleRate,
        int lastDevice,
        string? lastDeviceName,
        int currentSampleRate,
        int currentDevice,
        string? currentDeviceName) => DetectChange(
            lastSampleRate, lastDevice, lastDeviceName,
            lastDefaultEndpointId: null, lastDefaultEndpointName: null,
            currentSampleRate, currentDevice, currentDeviceName,
            currentDefaultEndpointId: null, currentDefaultEndpointName: null);

    internal static AudioDeviceChangeInfo? DetectChange(
        long lastSampleRate,
        int lastDevice,
        string? lastDeviceName,
        string? lastDefaultEndpointId,
        string? lastDefaultEndpointName,
        int currentSampleRate,
        int currentDevice,
        string? currentDeviceName,
        string? currentDefaultEndpointId,
        string? currentDefaultEndpointName) =>
            HasDefaultEndpointChanged(lastDefaultEndpointId, lastDefaultEndpointName, currentDefaultEndpointId, currentDefaultEndpointName)
                ? new AudioDeviceChangeInfo(
                    AudioDeviceChangeKind.DefaultDeviceChanged,
                    DeviceId: currentDefaultEndpointId,
                    DeviceName: currentDefaultEndpointName ?? currentDeviceName,
                    FailureMessage: $"Windows default audio output changed from {lastDefaultEndpointName ?? lastDeviceName ?? "(unknown)"} to {currentDefaultEndpointName ?? currentDeviceName ?? "(unknown)"}; leaving playback under BASS/Windows control.",
                    HResult: 0)
                : currentDevice != lastDevice
                    ? new AudioDeviceChangeInfo(
                        AudioDeviceChangeKind.DefaultDeviceChanged,
                        DeviceId: null,
                        DeviceName: currentDeviceName,
                        FailureMessage: null,
                        HResult: 0)
                    : lastSampleRate > 0 && currentSampleRate == 0
                        ? new AudioDeviceChangeInfo(
                            AudioDeviceChangeKind.DeviceRemoved,
                            DeviceId: null,
                            DeviceName: lastDeviceName,
                            FailureMessage: "BASS reported a zero sample rate; the current output device is no longer available.",
                            HResult: 0)
                        : lastSampleRate == 0 && currentSampleRate > 0
                            ? new AudioDeviceChangeInfo(
                                AudioDeviceChangeKind.DeviceAdded,
                                DeviceId: null,
                                DeviceName: currentDeviceName,
                                FailureMessage: null,
                                HResult: 0)
                            : lastSampleRate > 0 && currentSampleRate > 0 && currentSampleRate != lastSampleRate
                                ? new AudioDeviceChangeInfo(
                                    AudioDeviceChangeKind.DeviceStateChanged,
                                    DeviceId: null,
                                    DeviceName: currentDeviceName,
                                    FailureMessage: $"BASS sample rate changed from {lastSampleRate} Hz to {currentSampleRate} Hz.",
                                    HResult: 0)
                                : !string.Equals(lastDeviceName, currentDeviceName, StringComparison.Ordinal)
                                    ? new AudioDeviceChangeInfo(
                                        AudioDeviceChangeKind.DefaultDeviceChanged,
                                        DeviceId: null,
                                        DeviceName: currentDeviceName,
                                        FailureMessage: null,
                                        HResult: 0)
                                    : null;

    internal static bool HasDefaultEndpointChanged(string? lastEndpointId, string? lastEndpointName, string? currentEndpointId, string? currentEndpointName) =>
        !string.IsNullOrWhiteSpace(lastEndpointId) && !string.IsNullOrWhiteSpace(currentEndpointId)
            ? !string.Equals(lastEndpointId, currentEndpointId, StringComparison.OrdinalIgnoreCase)
            : !string.IsNullOrWhiteSpace(lastEndpointName) && !string.IsNullOrWhiteSpace(currentEndpointName) && !string.Equals(lastEndpointName, currentEndpointName, StringComparison.Ordinal);

    private void PollLoop()
    {
        while (!_stopRequested)
        {
            try
            {
                CheckForChanges();
            }
            catch
            {
            }

            TimeSpan interval = _pollInterval;
            var deadline = Stopwatch.GetTimestamp() + (long)(interval.TotalSeconds * Stopwatch.Frequency);
            while (!_stopRequested && Stopwatch.GetTimestamp() < deadline)
                Thread.Sleep(50);
        }
    }
}
