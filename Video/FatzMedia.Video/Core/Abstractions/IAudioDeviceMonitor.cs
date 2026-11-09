namespace FatzMedia.Video.Core.Abstractions;

public enum AudioDeviceChangeKind
{
    DefaultDeviceChanged = 0,
    DeviceStateChanged = 1,
    DeviceRemoved = 2,
    DeviceAdded = 3,
    Unrecoverable = 4,
}

public sealed record AudioDeviceChangeInfo(
    AudioDeviceChangeKind Kind,
    string? DeviceId,
    string? DeviceName,
    string? FailureMessage,
    int HResult);

[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1716", Justification = "Stop is the natural name; a C#-only API where the VB keyword clash does not matter and a rename would break every host.")]

public interface IAudioDeviceMonitor : IDisposable
{
    event EventHandler<AudioDeviceChangeInfo>? DeviceChanged;

    bool IsRunning { get; }

    void Start();

    void Stop();
}
