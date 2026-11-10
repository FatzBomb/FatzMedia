using FatzMedia.Video.Core.Abstractions;
using FatzMedia.Video.Core.Configuration;

namespace FatzMedia.Video.Audio;

/// <summary>
/// The FatzAudio engine, the player's default audio engine: audio files and archive entries, CD+G, video
/// audio and soundtracks. A null <see cref="MediaPlayerConfiguration.AudioEngineFactory"/> uses it; set an
/// instance explicitly only to make the choice visible.
/// </summary>
/// <example>
/// <code>
/// var configuration = new MediaPlayerConfiguration { AudioEngineFactory = new FatzAudioEngineFactory() };
/// </code>
/// </example>
public sealed class FatzAudioEngineFactory : IAudioEngineFactory
{
    readonly bool _nullOutput;

    /// <summary>Creates a factory for engines on the system default output device.</summary>
    public FatzAudioEngineFactory()
    {
    }

    /// <summary>Creates a factory whose engines play to FatzAudio's null device (tests).</summary>
    internal FatzAudioEngineFactory(bool nullOutput) => _nullOutput = nullOutput;

    /// <inheritdoc />
    public string Name => "FatzAudio";

    /// <inheritdoc />
    /// <exception cref="DllNotFoundException">The FatzAudio native library is missing.</exception>
    /// <exception cref="InvalidOperationException">The native library is another major.minor version than the binding.</exception>
    public IAudioEngine Create(MediaPlayerConfiguration configuration) => new FatzAudioEngine(configuration, _nullOutput);
}
