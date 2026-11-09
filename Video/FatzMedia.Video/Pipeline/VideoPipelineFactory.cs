using FatzMedia.Video.Core.Abstractions;
using FatzMedia.Video.Core.Configuration;
using FatzMedia.Video.Core.Threading;
using FatzMedia.Video.Core.Video;

namespace FatzMedia.Video.Pipeline;

/// <summary>
/// Portable video implementation factory supplied with platform timing and device services.
/// </summary>
internal sealed class VideoPipelineFactory : IVideoEngineFactory
{
    private readonly IPrecisionWaiterFactory _precisionWaiterFactory;
    private readonly IVideoPipelinePlatformServices _platformServices;
    private readonly RendererVideoDecodeServices _hardwareAccelerationServices;
    private readonly bool _audioAvailable;

    /// <param name="audioAvailable">
    /// False when the audio engine's native libraries (BASS) did not load: video then plays its
    /// picture on the playback clock, as in Background mode, and never touches BASS.
    /// </param>
    public VideoPipelineFactory(
        IPrecisionWaiterFactory precisionWaiterFactory,
        IVideoPipelinePlatformServices platformServices,
        RendererVideoDecodeServices hardwareAccelerationServices,
        bool audioAvailable = true)
    {
        _audioAvailable = audioAvailable;
        _precisionWaiterFactory = precisionWaiterFactory ?? throw new ArgumentNullException(nameof(precisionWaiterFactory));
        _platformServices = platformServices ?? throw new ArgumentNullException(nameof(platformServices));
        _hardwareAccelerationServices = hardwareAccelerationServices ?? throw new ArgumentNullException(nameof(hardwareAccelerationServices));
    }

    /// <summary>Whether video plays its own audio: not in Background mode (043A) and not without BASS (ENG-033).</summary>
    internal static bool UsesVideoAudio(MediaPlayerMode mode, bool audioAvailable) =>
        audioAvailable && MediaPlayerModePolicy.UsesAudioOutput(mode);

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

        return new VideoPipeline(
            clock,
            state,
            configuration,
            bassDeviceIndex,
            sync,
            _precisionWaiterFactory,
            _platformServices,
            _hardwareAccelerationServices,
            UsesVideoAudio(configuration.Mode, _audioAvailable));
    }
}
