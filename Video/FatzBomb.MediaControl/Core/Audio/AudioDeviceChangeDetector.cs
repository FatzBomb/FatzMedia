using FatzBomb.MediaControl.Core.Abstractions;

namespace FatzBomb.MediaControl.Core.Audio;

/// <summary>
/// Classifies audio-device state transitions from backend snapshots. Platform
/// monitors supply the snapshots; this comparison logic is platform-neutral.
/// </summary>
public static class AudioDeviceChangeDetector
{
    public static AudioDeviceChangeInfo? Detect(
        long lastSampleRate,
        int lastDevice,
        string? lastDeviceName,
        int currentSampleRate,
        int currentDevice,
        string? currentDeviceName) => Detect(
            lastSampleRate, lastDevice, lastDeviceName, null, null,
            currentSampleRate, currentDevice, currentDeviceName, null, null);

    public static AudioDeviceChangeInfo? Detect(
        long lastSampleRate,
        int lastDevice,
        string? lastDeviceName,
        string? lastDefaultEndpointId,
        string? lastDefaultEndpointName,
        int currentSampleRate,
        int currentDevice,
        string? currentDeviceName,
        string? currentDefaultEndpointId,
        string? currentDefaultEndpointName)
    {
        if (HasDefaultEndpointChanged(lastDefaultEndpointId, lastDefaultEndpointName, currentDefaultEndpointId, currentDefaultEndpointName))
        {
            return new AudioDeviceChangeInfo(
                AudioDeviceChangeKind.DefaultDeviceChanged,
                currentDefaultEndpointId,
                currentDefaultEndpointName ?? currentDeviceName,
                $"Windows default audio output changed from {lastDefaultEndpointName ?? lastDeviceName ?? "(unknown)"} to {currentDefaultEndpointName ?? currentDeviceName ?? "(unknown)"}; leaving playback under BASS/Windows control.",
                0);
        }

        if (currentDevice != lastDevice)
            return new AudioDeviceChangeInfo(AudioDeviceChangeKind.DefaultDeviceChanged, null, currentDeviceName, null, 0);

        if (lastSampleRate > 0 && currentSampleRate == 0)
        {
            return new AudioDeviceChangeInfo(
                AudioDeviceChangeKind.DeviceRemoved,
                null,
                lastDeviceName,
                "BASS reported a zero sample rate; the current output device is no longer available.",
                0);
        }

        if (lastSampleRate == 0 && currentSampleRate > 0)
            return new AudioDeviceChangeInfo(AudioDeviceChangeKind.DeviceAdded, null, currentDeviceName, null, 0);

        if (lastSampleRate > 0 && currentSampleRate > 0 && currentSampleRate != lastSampleRate)
        {
            return new AudioDeviceChangeInfo(
                AudioDeviceChangeKind.DeviceStateChanged,
                null,
                currentDeviceName,
                $"BASS sample rate changed from {lastSampleRate} Hz to {currentSampleRate} Hz.",
                0);
        }

        return !string.Equals(lastDeviceName, currentDeviceName, StringComparison.Ordinal)
            ? new AudioDeviceChangeInfo(AudioDeviceChangeKind.DefaultDeviceChanged, null, currentDeviceName, null, 0)
            : null;
    }

    public static bool HasDefaultEndpointChanged(string? lastEndpointId, string? lastEndpointName, string? currentEndpointId, string? currentEndpointName) =>
        !string.IsNullOrWhiteSpace(lastEndpointId) && !string.IsNullOrWhiteSpace(currentEndpointId)
            ? !string.Equals(lastEndpointId, currentEndpointId, StringComparison.OrdinalIgnoreCase)
            : !string.IsNullOrWhiteSpace(lastEndpointName) && !string.IsNullOrWhiteSpace(currentEndpointName) && !string.Equals(lastEndpointName, currentEndpointName, StringComparison.Ordinal);
}
