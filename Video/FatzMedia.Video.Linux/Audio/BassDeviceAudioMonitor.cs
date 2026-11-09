using FatzMedia.Video.Core.Abstractions;
using FatzMedia.Video.Core.Audio;
using ManagedBass;

namespace FatzMedia.Video.Linux.Audio;

/// <summary>One snapshot of the BASS output state.</summary>
readonly record struct AudioOutputSnapshot(int SampleRate, int Device, string? DeviceName, string? DefaultDeviceName);

/// <summary>Reads BASS output state; replaced in tests.</summary>
interface IAudioOutputSnapshotSource
{
    bool TryRead(out AudioOutputSnapshot snapshot);
}

/// <summary>
/// BASS-only snapshot source. On Linux BASS enumerates ALSA devices (and PulseAudio/PipeWire
/// through their ALSA plugins); the device flagged default stands in for the Windows default
/// endpoint.
/// </summary>
sealed class BassAudioOutputSnapshotSource : IAudioOutputSnapshotSource
{
    public bool TryRead(out AudioOutputSnapshot snapshot)
    {
        snapshot = default;
        if (!Bass.GetInfo(out var info))
            return false;

        var device = Bass.CurrentDevice;
        var deviceName = Bass.GetDeviceInfo(device, out var current) ? current.Name : null;
        string? defaultName = null;
        for (var index = 1; Bass.GetDeviceInfo(index, out var candidate); index++)
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
sealed class BassDeviceAudioMonitor : IAudioDeviceMonitor
{
    readonly IAudioOutputSnapshotSource _source;
    readonly PollingLoop _loop;
    AudioOutputSnapshot? _last;

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

        var previous = _last;
        _last = current;
        if (previous is not { } last)
            return null;

        var change = AudioDeviceChangeDetector.Detect(
            last.SampleRate, last.Device, last.DeviceName, null, last.DefaultDeviceName,
            current.SampleRate, current.Device, current.DeviceName, null, current.DefaultDeviceName);
        if (change is not null)
            DeviceChanged?.Invoke(this, change);

        return change;
    }
}
