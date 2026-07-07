using ManagedBass;
using MBass = ManagedBass.Bass;

namespace FatzBombMedia.MediaControl.Core.Audio;

internal static class BassAudioOutputDevices
{
    internal static IReadOnlyList<AudioOutputDeviceInfo> Enumerate()
    {
        List<AudioOutputDeviceInfo> devices = [AudioOutputDeviceInfo.Primary];

        for (var deviceIndex = 1; MBass.GetDeviceInfo(deviceIndex, out DeviceInfo deviceInfo); deviceIndex++)
        {
            if (IsDefaultDeviceListEntry(deviceInfo))
                continue;
            if (!deviceInfo.IsEnabled)
                continue;

            devices.Add(new AudioOutputDeviceInfo(
                deviceIndex,
                string.IsNullOrWhiteSpace(deviceInfo.Name) ? $"Audio Device {deviceIndex}" : deviceInfo.Name,
                deviceInfo.Driver,
                IsPrimary: false,
                deviceInfo.IsEnabled,
                deviceInfo.IsInitialized));
        }

        return devices;
    }

    internal static bool TrySetCurrentDevice(int deviceIndex, string diagnosticCategory, bool raiseDiagnostic = true)
    {
        if (deviceIndex < 0)
            return true;

        try
        {
            MBass.CurrentDevice = deviceIndex;
            return MBass.CurrentDevice == deviceIndex;
        }
        catch (Exception ex)
        {
            if (raiseDiagnostic)
                DiagnosticRouter.Raise(MediaDiagnosticLevel.Error, diagnosticCategory, $"Failed to select BASS device {deviceIndex}: {ex.Message}", innerException: ex);
            return false;
        }
    }

    internal static int ResolveInitializedDeviceIndex(int requestedDeviceIndex)
    {
        if (requestedDeviceIndex >= 0)
            return requestedDeviceIndex;

        var defaultEntry = FindDefaultDeviceListEntry();
        return defaultEntry > 0 ? defaultEntry : MBass.CurrentDevice;
    }

    private static int FindDefaultDeviceListEntry()
    {
        var firstDefaultDevice = -1;

        for (var deviceIndex = 1; MBass.GetDeviceInfo(deviceIndex, out DeviceInfo deviceInfo); deviceIndex++)
        {
            if (IsDefaultDeviceListEntry(deviceInfo))
                return deviceIndex;

            if (firstDefaultDevice < 0 && deviceInfo.IsDefault)
                firstDefaultDevice = deviceIndex;
        }

        return firstDefaultDevice;
    }

    private static bool IsDefaultDeviceListEntry(DeviceInfo deviceInfo) =>
        deviceInfo.IsDefault && string.Equals(deviceInfo.Name, "Default", StringComparison.OrdinalIgnoreCase);
}
