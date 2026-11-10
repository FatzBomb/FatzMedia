using FatzMedia.Video.Core.Abstractions;
using FatzMedia.Video.Core.Audio;
using FatzMedia.Video.Core.Configuration;
using FatzMedia.Video.Core.Threading;
using FatzMedia.Video.Core.Video;

namespace FatzMedia.Video.Pipeline;

/// <summary>
/// Portable video implementation factory supplied with platform timing and device services.
/// </summary>
sealed class VideoPipelineFactory : IVideoEngineFactory
{
    readonly IPrecisionWaiterFactory _precisionWaiterFactory;
    readonly IVideoPipelinePlatformServices _platformServices;
    readonly RendererVideoDecodeServices _hardwareAccelerationServices;
    readonly bool _audioAvailable;

    /// <param name="precisionWaiterFactory">Creates the precise waiters that pace frame presentation.</param>
    /// <param name="platformServices">The platform's timing and device services for the pipeline.</param>
    /// <param name="hardwareAccelerationServices">What the renderer offers for decoding on its own device.</param>
    /// <param name="audioAvailable">
    /// False when the player has no audio engine: video then plays its picture on the playback
    /// clock, as in Background mode.
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

    /// <summary>Whether video plays its own audio: not in Background mode (043A) and not without an audio engine (ENG-033).</summary>
    internal static bool UsesVideoAudio(MediaPlayerMode mode, bool audioAvailable) =>
        audioAvailable && MediaPlayerModePolicy.UsesAudioOutput(mode);

    public IVideoEngine Create(
        IClockSource clock,
        MediaState state,
        IAudioEngine audioEngine,
        MediaPlayerConfiguration configuration,
        PlaybackSyncState sync)
    {
        // Video audio plays on the audio engine's device; an engine without one leaves video silent.
        Func<IPushAudioOutput>? createAudioOutput = UsesVideoAudio(configuration.Mode, _audioAvailable)
            && audioEngine is IPushAudioOutputSource outputSource
                ? outputSource.CreatePushAudioOutput
                : null;

        return new VideoPipeline(
            clock,
            state,
            configuration,
            sync,
            _precisionWaiterFactory,
            _platformServices,
            _hardwareAccelerationServices,
            createAudioOutput);
    }
}
