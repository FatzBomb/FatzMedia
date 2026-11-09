using FatzMedia.Video.Core.Abstractions;
using MBass = ManagedBass.Bass;

namespace FatzMedia.Video.Core.Audio;

public sealed class BassOutputLatencyProvider(bool enableHeuristic) : IOutputLatencyProvider
{
    public BassOutputLatencyProvider()
        : this(enableHeuristic: false)
    {
    }

    public double GetOutputLatencySeconds()
    {
        if (!MBass.GetInfo(out var info))
            return 0;

        var reportedSeconds = info.Latency / 1000.0;
        return !double.IsFinite(reportedSeconds) || reportedSeconds < 0
            ? 0
            : !enableHeuristic
                ? reportedSeconds
                : !OutputLatencyHeuristic.RequiresDeviceName(reportedSeconds, enabled: true)
                    ? reportedSeconds
                    : !MBass.GetDeviceInfo(MBass.CurrentDevice, out var deviceInfo)
                        ? reportedSeconds
                        : OutputLatencyHeuristic.Apply(reportedSeconds, deviceInfo.Name, enabled: true);
    }
}
