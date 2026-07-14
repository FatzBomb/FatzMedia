using FatzBomb.MediaControl.Core.Abstractions;
using FatzBomb.MediaControl.Core.Configuration;

namespace FatzBomb.MediaControl.Pipeline;

/// <summary>
/// Creates the platform video implementation used by a playback session.
/// Core orchestration depends on this seam rather than a concrete decoder/presenter,
/// allowing Windows, macOS, and Linux implementations to be supplied independently.
/// </summary>
internal interface IVideoEngineFactory
{
    IVideoEngine Create(
        IClockSource clock,
        MediaState state,
        IAudioEngine audioEngine,
        MediaPlayerConfiguration configuration,
        PlaybackSyncState sync);
}
