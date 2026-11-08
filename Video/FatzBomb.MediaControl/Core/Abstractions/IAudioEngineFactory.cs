using FatzBomb.MediaControl.Core.Configuration;

namespace FatzBomb.MediaControl.Core.Abstractions;

/// <summary>
/// Creates the audio engine that plays audio files and archive entries, instead of the built-in
/// BASS engine (037A slice 2). Set it as <see cref="MediaPlayerConfiguration.AudioEngineFactory"/>;
/// <see langword="null"/> there keeps the BASS engine, the default. The FatzAudio engine package
/// supplies one.
/// </summary>
/// <remarks>
/// Only file and archive audio moves to the engine this creates. Video audio, video soundtracks and
/// network audio through FFmpeg stay on BASS, so the BASS libraries are still loaded and BASS is
/// still initialized on the configured device. When <see cref="Create"/> throws, the player warns
/// and uses the BASS engine.
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
