using FatzMedia.Video.Core.Abstractions;
using FatzMedia.Video.Core.Configuration;
using FatzMedia.Video.Core.Subtitle;
using FatzMedia.Video.Pipeline;

namespace FatzMedia.Video.Tests;

/// <summary>
/// MIDI/KAR lyrics drawn by the audio engine (FatzAudio) mark the subtitle layer as showing, so the presentation
/// draws them without a background picture and the karaoke control keeps the song. The managed lyric track did
/// this until audit U1 removed it.
/// </summary>
public sealed class EngineDrawnLyricsContentTests
{
    [Fact]
    public void AudioWithEngineDrawnLyrics_HasSubtitleContent()
    {
        var audio = new TestAudioEngine { DrawLyricsWith = static (_, _, _, _) => (null, false) };
        using var coordinator = CreateCoordinator(audio);

        Assert.True(coordinator.OpenAudio("song.kar"));

        Assert.Equal(MediaContent.Audio | MediaContent.Subtitle, coordinator.State.Content);
        Assert.True(LightweightPlayerPolicy.AcceptsLoadedMedia(LightweightPlayerKind.Karaoke, coordinator.State.Content, "song.kar"));
    }

    [Fact]
    public void AudioWithoutLyrics_HasNoSubtitleContent()
    {
        using var coordinator = CreateCoordinator(new TestAudioEngine());

        Assert.True(coordinator.OpenAudio("song.mp3"));

        Assert.Equal(MediaContent.Audio, coordinator.State.Content);
    }

    static PlaybackCoordinator CreateCoordinator(TestAudioEngine audio) =>
        new(
            audio,
            new TestCdgEngine(),
            new SubtitleEngine(),
            new ClosedVideoFactory(),
            MediaPlayerConfiguration.Auto,
            audioAvailable: false);

    sealed class ClosedVideoFactory : IVideoEngineFactory
    {
        public IVideoEngine Create(IClockSource clock, MediaState state, IAudioEngine audioEngine, MediaPlayerConfiguration configuration, PlaybackSyncState sync) =>
            new TestVideoEngine { IsOpen = false };
    }
}
