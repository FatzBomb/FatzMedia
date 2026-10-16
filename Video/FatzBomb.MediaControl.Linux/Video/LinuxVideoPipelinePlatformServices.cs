using FatzBomb.MediaControl.Core.Abstractions;

namespace FatzBomb.MediaControl.Linux.Video;

/// <summary>Supplies host-configured display timing to Core video playback.</summary>
internal sealed class LinuxVideoPipelinePlatformServices(LinuxPlatformOptions options) : IVideoPipelinePlatformServices
{
    public double GetPresentationRefreshIntervalSeconds() => 1.0 / options.DisplayRefreshRateHz;
}
