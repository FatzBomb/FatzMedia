namespace FatzBombMedia.MediaControl.Core.Abstractions;

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

public interface IAudioDeviceMonitor : IDisposable
{
    event EventHandler<AudioDeviceChangeInfo>? DeviceChanged;

    bool IsRunning { get; }

    void Start();

    void Stop();
}
