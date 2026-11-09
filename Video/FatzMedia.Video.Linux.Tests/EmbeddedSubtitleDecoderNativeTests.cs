using FatzMedia.Video.Core.Subtitle;

namespace FatzMedia.Video.Linux.Tests;

/// <summary>
/// Opt-in native check of the embedded subtitle scan (ENG-039 P5-1) with real FFmpeg. Set
/// <c>FATZBOMB_LINUX_FFMPEG_DIR</c> as for <see cref="LinuxNativeSmokeTests"/>; the clip is
/// <c>subtitles.mkv</c> (an H.264 clip with an SRT track) in <c>FATZBOMB_LINUX_SUBTITLE_MEDIA_DIR</c>
/// (default <c>/opt/fatzbomb-native/media</c>, written by <c>scripts/setup-linux-native.sh</c>).
/// Without them the tests return immediately.
/// </summary>
public sealed class EmbeddedSubtitleDecoderNativeTests
{
    const string DefaultMediaDirectory = "/opt/fatzbomb-native/media";

    [Fact]
    public void DecodeTracks_ReadsTheEmbeddedSrtTrack()
    {
        if (ResolveClip() is not { } clip)
            return;

        var track = Assert.Single(EmbeddedSubtitleDecoder.DecodeTracks(clip, CancellationToken.None));
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

    /// <summary>
    /// ENG-052: the scan reads a large MKV once, not twice. Opt-in: set
    /// <c>FATZBOMB_LINUX_LARGE_SUBTITLE_MKV</c> to a large MKV with a text subtitle track (hundreds of MB).
    /// Counts the bytes the process reads (<c>rchar</c> in <c>/proc/self/io</c>, cached reads included, as
    /// Windows' read counter does).
    /// </summary>
    [Fact]
    public void DecodeTracks_ReadsALargeFileAtMostOnce()
    {
        var ffmpeg = Environment.GetEnvironmentVariable("FATZBOMB_LINUX_FFMPEG_DIR");
        var clip = Environment.GetEnvironmentVariable("FATZBOMB_LINUX_LARGE_SUBTITLE_MKV");
        if (string.IsNullOrWhiteSpace(ffmpeg) || string.IsNullOrWhiteSpace(clip) || !File.Exists(clip) || !OperatingSystem.IsLinux())
            return;

        FfmpegAudioReaderNativeTests.ActivateFfmpeg(ffmpeg);
        var size = new FileInfo(clip).Length;
        var before = ReadProcessBytes();
        var tracks = EmbeddedSubtitleDecoder.DecodeTracks(clip, CancellationToken.None);
        var read = ReadProcessBytes() - before;

        Assert.NotEmpty(tracks);
        Assert.True(read < size * 1.2, $"read {read / 1048576.0:F0} MB of a {size / 1048576.0:F0} MB file");
    }

    static long ReadProcessBytes()
    {
        foreach (var line in File.ReadLines("/proc/self/io"))
        {
            if (line.StartsWith("rchar:", StringComparison.Ordinal))
                return long.Parse(line.AsSpan(6).Trim(), System.Globalization.CultureInfo.InvariantCulture);
        }

        return 0;
    }

    static string? ResolveClip()
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
