namespace FatzMedia.Video.Core.Abstractions;

/// <summary>
/// Audio engine support for direct HTTP/HTTPS sources (Stage 014A).
/// </summary>
internal interface INetworkAudioSource
{
    /// <summary>
    /// Connects to <paramref name="url"/> with the configured bounded retry policy. Blocks the
    /// caller for at most (attempts x connect timeout + retry delays).
    /// </summary>
    bool OpenUrl(string url);

    /// <summary>Gets the probed capabilities of the open network source, or <see langword="null"/>.</summary>
    MediaSourceCapabilities? NetworkCapabilities { get; }

    /// <summary>Raised from a background thread when buffering state or fill changes.</summary>
    event EventHandler<MediaBufferingChangedEventArgs>? BufferingChanged;

    /// <summary>Raised from a background thread when in-stream metadata changes.</summary>
    event EventHandler<MediaStreamMetadataChangedEventArgs>? StreamMetadataChanged;
}

/// <summary>
/// Reports the capability row of the currently open source.
/// </summary>
internal interface IMediaSourceCapabilityProvider
{
    MediaSourceCapabilities? CurrentCapabilities { get; }
}

/// <summary>
/// Engine support for sources read through FFmpeg's network path (Stage 039A; ENG-034): cancelling
/// an open in progress, buffering changes (network video, Audio-mode network soundtracks, https
/// and HLS audio) and the in-stream titles of network audio.
/// </summary>
internal interface INetworkPlayer
{
    /// <summary>
    /// Cancels the FFmpeg network open in progress, if any: the connect attempt and any retry delay
    /// end at once and the load fails. Thread-safe; called outside the control queue by Close and
    /// Dispose.
    /// </summary>
    void CancelPendingNetworkOpen();

    /// <summary>Raised from a timer thread when buffering state or fill changes.</summary>
    event EventHandler<MediaBufferingChangedEventArgs>? NetworkBufferingChanged;

    /// <summary>
    /// Raised from a timer thread when network audio played through FFmpeg (https, HLS; ENG-034)
    /// reaches a new in-stream title.
    /// </summary>
    event EventHandler<MediaStreamMetadataChangedEventArgs>? NetworkStreamMetadataChanged;
}
