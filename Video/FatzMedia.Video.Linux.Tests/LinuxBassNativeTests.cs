using System.Diagnostics;
using FatzMedia.Video.Core.Abstractions;
using Xunit.Abstractions;

namespace FatzMedia.Video.Linux.Tests;

/// <summary>
/// Opt-in native check for 026B (Linux with BASS and FFmpeg): native loading reports both folders,
/// audio files play, pause, seek and stop through the system's sound server, and a video presents
/// frames on its audio clock. Set <c>FATZBOMB_LINUX_BASS_DIR</c> to a folder with <c>libbass.so</c>
/// and <c>libbass_fx.so</c> (and optionally <c>libbassflac.so</c>), <c>FATZBOMB_LINUX_FFMPEG_DIR</c>
/// to the FFmpeg 9 libraries, and have a sound server (PulseAudio or PipeWire, a null sink is enough).
/// <c>scripts/setup-linux-native.sh</c> prepares all of it and the media. Without them the tests
/// return immediately.
/// </summary>
[Collection("File type registry")]
public sealed class LinuxBassNativeTests(ITestOutputHelper output)
{
    const string MediaDir = LinuxNativeTestPlayer.MediaDir;

    [Fact]
    public void NativeLoad_ReportsBassAndFfmpeg()
    {
        if (!LinuxNativeTestPlayer.TryGetFolders(out var bass, out var ffmpeg))
            return;

        using var session = LinuxNativeTestPlayer.Create([bass, ffmpeg], attachSurface: false);
        var player = session.Player;
        output.WriteLine(player.LastNativeLoadMessage);
        foreach (var line in player.NativeDependencies.Describe())
            output.WriteLine(line);
        Assert.True(player.NativeDependencies.IsAudioAvailable, string.Join(" ", player.NativeDependencies.Describe()));
        Assert.True(player.NativeDependencies.RequiredLoaded, string.Join(" ", player.NativeDependencies.Describe()));
        Assert.Contains(bass, player.LastNativeLoadMessage, StringComparison.Ordinal);
        Assert.Contains(ffmpeg, player.LastNativeLoadMessage, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("tone.mp3")]
    [InlineData("tone.flac")]
    public void AudioFile_PlaysPausesSeeksAndStops(string name)
    {
        var file = Path.Combine(MediaDir, name);
        if (!LinuxNativeTestPlayer.TryGetFolders(out var bass, out var ffmpeg) || !File.Exists(file))
            return;

        using var session = LinuxNativeTestPlayer.Create([bass, ffmpeg], attachSurface: false);
        var player = session.Player;
        Assert.True(player.Load(file), string.Join(" | ", session.Errors));
        Assert.True(player.Play());
        Assert.True(SpinWait.SpinUntil(() => player.Position > TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(10)), $"position {player.Position}");

        // Real time on the sound server: about one second of position per second.
        var start = player.Position;
        var watch = Stopwatch.StartNew();
        Thread.Sleep(2000);
        var rate = (player.Position - start).TotalSeconds / watch.Elapsed.TotalSeconds;
        output.WriteLine($"{name}: playback rate {rate:F3}x");
        Assert.InRange(rate, 0.9, 1.1);

        Assert.True(player.Pause());
        Thread.Sleep(300);
        var paused = player.Position;
        Thread.Sleep(700);
        Assert.InRange((player.Position - paused).TotalMilliseconds, -50, 50);
        Assert.Equal(MediaPlayerState.Paused, player.State);

        Assert.True(player.Seek(TimeSpan.FromSeconds(6)));
        Assert.True(SpinWait.SpinUntil(() => Math.Abs(player.Position.TotalSeconds - 6) < 0.5, TimeSpan.FromSeconds(5)), $"after seek {player.Position}");
        Assert.True(player.Play());
        Assert.True(SpinWait.SpinUntil(() => player.Position > TimeSpan.FromSeconds(6.5), TimeSpan.FromSeconds(5)), $"after resume {player.Position}");

        Assert.True(player.Stop());
        Assert.True(SpinWait.SpinUntil(() => player.State == MediaPlayerState.Stopped, TimeSpan.FromSeconds(5)), $"state {player.State}");
        Assert.Empty(session.Errors);
    }

    [Fact]
    public void Video_PresentsFramesOnTheAudioClock()
    {
        var sample = Environment.GetEnvironmentVariable("FATZBOMB_LINUX_SAMPLE_VIDEO") is { Length: > 0 } configured ? configured : Path.Combine(MediaDir, "h264_1080p.mp4");
        if (!LinuxNativeTestPlayer.TryGetFolders(out var bass, out var ffmpeg) || !File.Exists(sample))
            return;

        using var session = LinuxNativeTestPlayer.Create([bass, ffmpeg]);
        var player = session.Player;
        var sink = session.Sink;
        Assert.True(player.Load(sample), string.Join(" | ", session.Errors));
        Assert.True(player.Play());
        Assert.True(SpinWait.SpinUntil(() => sink.Frames >= 5 && player.Position > TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(15)), $"frames {sink.Frames}, position {player.Position}");

        // The clock follows the audio on the sound server; frames keep coming at the clip's rate.
        var startPosition = player.Position;
        var startFrames = sink.Frames;
        var watch = Stopwatch.StartNew();
        Thread.Sleep(3000);
        var rate = (player.Position - startPosition).TotalSeconds / watch.Elapsed.TotalSeconds;
        var fps = (sink.Frames - startFrames) / watch.Elapsed.TotalSeconds;
        output.WriteLine($"video: clock rate {rate:F3}x, {fps:F1} frames per second presented");
        Assert.InRange(rate, 0.9, 1.1);
        Assert.True(fps > 10, $"only {fps:F1} frames per second");
        Assert.True(sink.LastFrameHasContent, "presented frame looks blank");
        Assert.Empty(session.Errors);
        _ = player.Stop();
    }
}
