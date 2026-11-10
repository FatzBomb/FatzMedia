using FatzMedia.Video.Core.Configuration;

namespace FatzMedia.Video.Core.Abstractions;

/// <summary>
/// Creates the audio engine that plays audio files and archive entries, instead of the built-in
/// BASS engine (037A slice 2). Set it as <see cref="MediaPlayerConfiguration.AudioEngineFactory"/>;
/// <see langword="null"/> there keeps the BASS engine, the default. The FatzAudio engine package
/// supplies one.
/// </summary>
/// <remarks>
/// The engine plays file and archive audio. When it also offers Core's internal output and CD+G seams
/// (the FatzAudio engine does), video audio, soundtracks, network audio through FFmpeg and CD+G go to
/// it as well, and BASS is not initialized. When <see cref="Create"/> throws, the player warns and
/// uses the BASS engine if BASS loaded, otherwise it has no audio.
/// </remarks>
public interface IAudioEngineFactory
{
    /// <summary>Gets the engine's name for diagnostics, for example "FatzAudio".</summary>
    string Name { get; }

    /// <summary>
    /// Creates an engine with its output device ready, for one player. Called once per player
    /// graph, on the thread that creates the player. Throws when the engine cannot start.
    /// </summary>
    IAudioEngine Create(MediaPlayerConfiguration configuration);
}
