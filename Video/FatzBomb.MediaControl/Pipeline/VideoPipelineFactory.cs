using FatzBomb.MediaControl.Core.Abstractions;
using FatzBomb.MediaControl.Core.Configuration;

namespace FatzBomb.MediaControl.Pipeline;

/// <summary>
/// Current Windows video implementation factory. It will move with
/// <see cref="VideoPipeline"/> when the Windows engine project is split out.
/// </summary>
internal sealed class VideoPipelineFactory : IVideoEngineFactory
{
    public IVideoEngine Create(
        IClockSource clock,
        MediaState state,
        IAudioEngine audioEngine,
        MediaPlayerConfiguration configuration,
        PlaybackSyncState sync)
    {
        var bassDeviceIndex = audioEngine is IAudioOutputDeviceProvider deviceProvider
            ? deviceProvider.BassDeviceIndex
            : configuration.AudioOutputDeviceIndex;

        return new VideoPipeline(clock, state, configuration, bassDeviceIndex, sync);
    }
}
