namespace FatzMedia.Video.Core.Abstractions;

/// <summary>
/// Internal seam through which the media engine surfaces typed failure information
/// to the host-facing <see cref="PlayerCore"/> without leaking pipeline
/// internals into the public <see cref="IPlayerSession"/> contract.
/// </summary>
internal interface IMediaErrorSource
{
    /// <summary>
    /// Raised when a high-value failure occurs inside the engine. Carries a
    /// <see cref="MediaErrorInfo"/> payload with a categorized code and message.
    /// May fire from background threads; subscribers must marshal as needed.
    /// </summary>
    event Action<MediaErrorInfo>? ErrorOccurred;
}
