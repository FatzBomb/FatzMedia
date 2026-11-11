using FatzMedia.Video.Core.Abstractions;
using FatzMedia.Video.Core.Configuration;

namespace FatzMedia.Video.Linux.Tests;

/// <summary>An audio engine factory that cannot start, so a player under test has no audio (picture only).</summary>
sealed class UnavailableAudioEngineFactory : IAudioEngineFactory
{
    public string Name => "Unavailable";

    public IAudioEngine Create(MediaPlayerConfiguration configuration) =>
        throw new InvalidOperationException("No audio engine in this test.");
}
