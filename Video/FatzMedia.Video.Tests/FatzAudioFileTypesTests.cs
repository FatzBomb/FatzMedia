using FatzMedia.Video.Audio;

namespace FatzMedia.Video.Tests;

[Collection("File type registry")]
public sealed class FatzAudioFileTypesTests
{
    [Fact]
    public void AudioExtensions_AddTheDot_AndLeaveOutVideoCdgAndPlaylists() =>
        Assert.Equal(
            [".mp3", ".FLAC", ".kar", ".mod"],
            FatzAudioFileTypes.AudioExtensions(["mp3", ".FLAC", "mp4", "MKV", "cdg", "m3u8", " ", "kar", "mod", "wmv"]));
}
