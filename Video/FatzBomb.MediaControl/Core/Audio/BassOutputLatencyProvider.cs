using FatzBomb.MediaControl.Core.Abstractions;
using FatzBomb.MediaControl.Core.Audio;
using ManagedBass;
using MBass = ManagedBass.Bass;

namespace FatzBomb.MediaControl.Core.Audio;

public sealed class BassOutputLatencyProvider(bool enableHeuristic) : IOutputLatencyProvider
{
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
                    : !OutputLatencyHeuristic.RequiresDeviceName(reportedSeconds, enabled: true)
                        ? reportedSeconds
                        : !MBass.GetDeviceInfo(MBass.CurrentDevice, out DeviceInfo deviceInfo)
                            ? reportedSeconds
                            : OutputLatencyHeuristic.Apply(reportedSeconds, deviceInfo.Name, enabled: true);
    }
}
