namespace FatzMedia.Video.Core.Abstractions;

/// <summary>
/// Supplies platform-specific timing and hardware-device checks for portable video playback.
/// </summary>
public interface IVideoPipelinePlatformServices
{
    /// <summary>
    /// Gets the platform's current fallback presentation refresh interval in seconds.
    /// </summary>
    double GetPresentationRefreshIntervalSeconds();

}
