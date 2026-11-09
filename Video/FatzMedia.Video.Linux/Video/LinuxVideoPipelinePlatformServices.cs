using FatzMedia.Video.Core.Abstractions;

namespace FatzMedia.Video.Linux.Video;

/// <summary>Supplies host-configured display timing to Core video playback.</summary>
sealed class LinuxVideoPipelinePlatformServices(LinuxPlatformOptions options) : IVideoPipelinePlatformServices
{
    public double GetPresentationRefreshIntervalSeconds() => 1.0 / options.DisplayRefreshRateHz;
}
