using FatzBomb.MediaControl.Core.Abstractions;
using FatzBomb.MediaControl.Core.Configuration;
using FatzBomb.MediaControl.Core.Threading;

namespace FatzBomb.MediaControl.Pipeline;

/// <summary>
/// Portable video implementation factory supplied with platform timing and device services.
/// </summary>
internal sealed class VideoPipelineFactory : IVideoEngineFactory
{
    private readonly IPrecisionWaiterFactory _precisionWaiterFactory;
    private readonly IVideoPipelinePlatformServices _platformServices;

    public VideoPipelineFactory(
        IPrecisionWaiterFactory precisionWaiterFactory,
        IVideoPipelinePlatformServices platformServices)
    {
        _precisionWaiterFactory = precisionWaiterFactory ?? throw new ArgumentNullException(nameof(precisionWaiterFactory));
        _platformServices = platformServices ?? throw new ArgumentNullException(nameof(platformServices));
    }

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

        return new VideoPipeline(clock, state, configuration, bassDeviceIndex, sync, _precisionWaiterFactory, _platformServices);
    }
}
