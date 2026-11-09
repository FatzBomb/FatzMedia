using FatzMedia.Video.Core.Abstractions;
using FatzMedia.Video.Core.Configuration;

namespace FatzMedia.Video.Pipeline;

/// <summary>
/// Creates the platform video implementation used by a playback session.
/// Core orchestration depends on this seam rather than a concrete decoder/presenter,
/// allowing Windows, macOS, and Linux implementations to be supplied independently.
/// </summary>
interface IVideoEngineFactory
{
    IVideoEngine Create(
        IClockSource clock,
        MediaState state,
        IAudioEngine audioEngine,
        MediaPlayerConfiguration configuration,
        PlaybackSyncState sync);
}
