using FatzBomb.MediaControl.Core.Abstractions;
using ManagedBass;
using MBass = ManagedBass.Bass;

namespace FatzBomb.MediaControl.Core.Audio;

public sealed class BassOutputLatencyProvider(bool enableHeuristic) : IOutputLatencyProvider
{
    private const double HeuristicLowReportThresholdSeconds = 0.010;
    private const double HeuristicUsbDacOffsetSeconds = 0.050;
    private const double HeuristicBluetoothOffsetSeconds = 0.150;

    public BassOutputLatencyProvider()
        : this(enableHeuristic: false)
    {
    }

    public double GetOutputLatencySeconds()
    {
        if (!MBass.GetInfo(out BassInfo info))
            return 0;

        var reportedSeconds = info.Latency / 1000.0;
        return !double.IsFinite(reportedSeconds) || reportedSeconds < 0
            ? 0
            : !enableHeuristic
                ? reportedSeconds
                : reportedSeconds >= HeuristicLowReportThresholdSeconds
                    ? reportedSeconds
                    : !MBass.GetDeviceInfo(MBass.CurrentDevice, out DeviceInfo deviceInfo)
                        ? reportedSeconds
                        : ApplyHeuristic(reportedSeconds, deviceInfo.Name, enableHeuristic: true);
    }

    internal static double ApplyHeuristic(double reportedSeconds, string? deviceName, bool enableHeuristic) =>
        !enableHeuristic
            ? reportedSeconds
            : reportedSeconds >= HeuristicLowReportThresholdSeconds
                ? reportedSeconds
                : string.IsNullOrEmpty(deviceName)
                    ? reportedSeconds
                    : ContainsIgnoreCase(deviceName, "bluetooth") || ContainsIgnoreCase(deviceName, " bts") || ContainsIgnoreCase(deviceName, "(bt)")
                        ? HeuristicBluetoothOffsetSeconds
                        : ContainsIgnoreCase(deviceName, "usb") ? HeuristicUsbDacOffsetSeconds : reportedSeconds;

    private static bool ContainsIgnoreCase(string haystack, string needle) =>
        haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
}
