using FatzBomb.MediaControl.Core.Abstractions;
using FatzBomb.MediaControl.Core.Configuration;
using FatzBomb.MediaControl.Pipeline;

namespace FatzBomb.MediaControl.Windows.Pipeline;

/// <summary>
/// Windows video implementation factory.
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
