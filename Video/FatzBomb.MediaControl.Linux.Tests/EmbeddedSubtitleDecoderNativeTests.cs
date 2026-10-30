using FatzBomb.MediaControl.Core.Subtitle;

namespace FatzBomb.MediaControl.Linux.Tests;

/// <summary>
/// Opt-in native check of the embedded subtitle scan (ENG-039 P5-1) with real FFmpeg. Set
/// <c>FATZBOMB_LINUX_FFMPEG_DIR</c> as for <see cref="LinuxNativeSmokeTests"/>; the clip is
/// <c>subtitles.mkv</c> (an H.264 clip with an SRT track) in <c>FATZBOMB_LINUX_SUBTITLE_MEDIA_DIR</c>
/// (default <c>/opt/fatzbomb-native/media</c>, written by <c>scripts/setup-linux-native.sh</c>).
/// Without them the tests return immediately.
/// </summary>
public sealed class EmbeddedSubtitleDecoderNativeTests
{
    private const string DefaultMediaDirectory = "/opt/fatzbomb-native/media";

    [Fact]
    public void DecodeTracks_ReadsTheEmbeddedSrtTrack()
    {
        if (ResolveClip() is not { } clip)
            return;

        EmbeddedSubtitleTrack track = Assert.Single(EmbeddedSubtitleDecoder.DecodeTracks(clip, CancellationToken.None));
        Assert.Equal("SUBRIP", track.CodecName);
        Assert.Equal(4, track.Cues.Count);
        Assert.Equal("First line", track.Cues[0].Text);
    }

    [Fact]
    public void DecodeTracks_StopsWhenCancelled()
    {
        if (ResolveClip() is not { } clip)
            return;

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        // The token is checked before every packet: a cancelled scan reads none and throws.
        _ = Assert.ThrowsAny<OperationCanceledException>(() => EmbeddedSubtitleDecoder.DecodeTracks(clip, cancellation.Token));
    }

    private static string? ResolveClip()
    {
        var ffmpeg = Environment.GetEnvironmentVariable("FATZBOMB_LINUX_FFMPEG_DIR");
        var mediaDirectory = Environment.GetEnvironmentVariable("FATZBOMB_LINUX_SUBTITLE_MEDIA_DIR") is { Length: > 0 } configured ? configured : DefaultMediaDirectory;
        var clip = Path.Combine(mediaDirectory, "subtitles.mkv");
        if (string.IsNullOrWhiteSpace(ffmpeg) || !File.Exists(clip) || !OperatingSystem.IsLinux())
            return null;

        FfmpegAudioReaderNativeTests.ActivateFfmpeg(ffmpeg);
        return clip;
    }
}
