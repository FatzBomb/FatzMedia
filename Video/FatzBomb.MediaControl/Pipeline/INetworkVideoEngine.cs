using FatzBomb.MediaControl.Core.Media;

namespace FatzBomb.MediaControl.Pipeline;

/// <summary>
/// Network video support of the video pipeline (Stage 039A): an http/https open through FFmpeg's
/// network path, the probed capability row, and buffering changes.
/// </summary>
internal interface INetworkVideoEngine
{
    /// <summary>
    /// Makes one attempt to open <paramref name="url"/> (retries belong to the caller). Bounded by
    /// the policy's connect timeout and cancelled by <see cref="FfmpegNetworkOptions.Cancellation"/>.
    /// </summary>
    bool OpenNetwork(string url, FfmpegNetworkOptions options);

    /// <summary>Gets the capability row of the open network source, or <see langword="null"/>.</summary>
    MediaSourceCapabilities? NetworkCapabilities { get; }

    /// <summary>Gets FFmpeg's reason for the last failed network open, or <see langword="null"/>.</summary>
    string? LastNetworkError { get; }

    /// <summary>Raised from a timer thread when the network buffering state or fill changes.</summary>
    event EventHandler<MediaBufferingChangedEventArgs>? NetworkBufferingChanged;
}
