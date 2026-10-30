using FatzBomb.MediaControl.Core.Abstractions;
using FatzBomb.MediaControl.Core.Audio;
using ManagedBass;

namespace FatzBomb.MediaControl.Linux.Audio;

/// <summary>One snapshot of the BASS output state.</summary>
internal readonly record struct AudioOutputSnapshot(int SampleRate, int Device, string? DeviceName, string? DefaultDeviceName);

/// <summary>Reads BASS output state; replaced in tests.</summary>
internal interface IAudioOutputSnapshotSource
{
    bool TryRead(out AudioOutputSnapshot snapshot);
}

/// <summary>
/// BASS-only snapshot source. On Linux BASS enumerates ALSA devices (and PulseAudio/PipeWire
/// through their ALSA plugins); the device flagged default stands in for the Windows default
/// endpoint.
/// </summary>
internal sealed class BassAudioOutputSnapshotSource : IAudioOutputSnapshotSource
{
    public bool TryRead(out AudioOutputSnapshot snapshot)
    {
        snapshot = default;
        if (!Bass.GetInfo(out BassInfo info))
            return false;

        var device = Bass.CurrentDevice;
        string? deviceName = Bass.GetDeviceInfo(device, out DeviceInfo current) ? current.Name : null;
        string? defaultName = null;
        for (var index = 1; Bass.GetDeviceInfo(index, out DeviceInfo candidate); index++)
        {
            if (candidate.IsDefault)
            {
                defaultName = candidate.Name;
                break;
            }
        }

        snapshot = new AudioOutputSnapshot(info.SampleRate, device, deviceName, defaultName);
        return true;
    }
}

/// <summary>
/// Polling output-device monitor built on BASS state and the portable Core change detector.
/// </summary>
internal sealed class BassDeviceAudioMonitor : IAudioDeviceMonitor
{
    private readonly IAudioOutputSnapshotSource _source;
    private readonly PollingLoop _loop;
    private AudioOutputSnapshot? _last;

    public BassDeviceAudioMonitor(IAudioOutputSnapshotSource source, TimeSpan pollInterval)
    {
        _source = source;
        _loop = new PollingLoop(
            "LinuxAudioDeviceMonitor",
            pollInterval,
            () => _ = Poll(),
            exception => DiagnosticRouter.Raise(MediaDiagnosticLevel.Warning, "LinuxAudioDeviceMonitor", $"Device poll failed: {exception.Message}", innerException: exception));
    }

    public event EventHandler<AudioDeviceChangeInfo>? DeviceChanged;

    public bool IsRunning => _loop.IsRunning;

    public void Start() => _loop.Start(() => _last = _source.TryRead(out var baseline) ? baseline : null);

    public void Stop() => _loop.Stop();

    public void Dispose() => Stop();

    /// <summary>Runs one comparison; exposed for tests.</summary>
    internal AudioDeviceChangeInfo? Poll()
    {
        if (!_source.TryRead(out var current))
            return null;

        AudioOutputSnapshot? previous = _last;
        _last = current;
        if (previous is not { } last)
            return null;

        AudioDeviceChangeInfo? change = AudioDeviceChangeDetector.Detect(
            last.SampleRate, last.Device, last.DeviceName, null, last.DefaultDeviceName,
            current.SampleRate, current.Device, current.DeviceName, null, current.DefaultDeviceName);
        if (change is not null)
            DeviceChanged?.Invoke(this, change);

        return change;
    }
}
