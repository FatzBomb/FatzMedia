namespace FatzMedia.Video.Core.Audio;

/// <summary>
/// Applies host-configurable output-latency compensation for devices that report
/// implausibly low latency. The policy is independent of any audio backend.
/// </summary>
static class OutputLatencyHeuristic
{
    const double LowReportThresholdSeconds = 0.010;
    const double UsbDacOffsetSeconds = 0.050;
    const double BluetoothOffsetSeconds = 0.150;

    /// <summary>
    /// Gets whether the backend should query the current device name before applying
    /// compensation. Avoids native device inspection for normal latency reports.
    /// </summary>
    public static bool RequiresDeviceName(double reportedSeconds, bool enabled) =>
        enabled && reportedSeconds < LowReportThresholdSeconds;

    public static double Apply(double reportedSeconds, string? deviceName, bool enabled) =>
        !enabled
            ? reportedSeconds
            : reportedSeconds >= LowReportThresholdSeconds
                ? reportedSeconds
                : string.IsNullOrEmpty(deviceName)
                    ? reportedSeconds
                    : ContainsIgnoreCase(deviceName, "bluetooth") || ContainsIgnoreCase(deviceName, " bts") || ContainsIgnoreCase(deviceName, "(bt)")
                        ? BluetoothOffsetSeconds
                        : ContainsIgnoreCase(deviceName, "usb") ? UsbDacOffsetSeconds : reportedSeconds;

    static bool ContainsIgnoreCase(string haystack, string needle) =>
        haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
}
