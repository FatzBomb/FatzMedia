using FatzBomb.MediaControl.Core.Abstractions;
using FatzBomb.MediaControl.Core.Audio;
using ManagedBass;
using System.Diagnostics;

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
internal sealed class BassDeviceAudioMonitor(IAudioOutputSnapshotSource source, TimeSpan pollInterval) : IAudioDeviceMonitor
{
    private readonly Lock _gate = new();
    private Thread? _thread;
    private volatile bool _stopRequested;
    private AudioOutputSnapshot? _last;

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
            _last = source.TryRead(out var baseline) ? baseline : null;
            _thread = new Thread(PollLoop) { IsBackground = true, Name = "LinuxAudioDeviceMonitor" };
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

        _ = thread?.Join(pollInterval + TimeSpan.FromSeconds(1));
    }

    public void Dispose() => Stop();

    /// <summary>Runs one comparison; exposed for tests.</summary>
    internal AudioDeviceChangeInfo? Poll()
    {
        if (!source.TryRead(out var current))
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

    private void PollLoop()
    {
        while (!_stopRequested)
        {
            try
            {
                _ = Poll();
            }
            catch (Exception exception)
            {
                DiagnosticRouter.Raise(MediaDiagnosticLevel.Warning, "LinuxAudioDeviceMonitor", $"Device poll failed: {exception.Message}", innerException: exception);
            }

            var deadline = Stopwatch.GetTimestamp() + (long)(pollInterval.TotalSeconds * Stopwatch.Frequency);
            while (!_stopRequested && Stopwatch.GetTimestamp() < deadline)
                Thread.Sleep(50);
        }
    }
}
