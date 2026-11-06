using System.Diagnostics;
using Xunit.Abstractions;

namespace FatzBomb.MediaControl.Linux.Tests;

/// <summary>
/// ENG-051, opt-in (BASS and FFmpeg as for <see cref="LinuxBassNativeTests"/>, plus FFmpeg's command line
/// in <c>/opt/fatzbomb-native/ffmpeg/bin</c> to make the media): a 3 s song looping with a same-name
/// 8 s background video that brightens steadily (grey level = frame number). The background loops on its
/// own, independent of the song: after the song wraps it keeps brightening instead of starting dark again.
/// </summary>
[Collection("File type registry")]
public sealed class LinuxBackgroundLoopNativeTests(ITestOutputHelper output)
{
    private const string FfmpegCli = "/opt/fatzbomb-native/ffmpeg/bin/ffmpeg";

    [Fact]
    public void LoopingSong_LeavesTheBackgroundVideoPlaying()
    {
        if (!LinuxNativeTestPlayer.TryGetFolders(out var bass, out var ffmpeg) || !File.Exists(FfmpegCli))
            return;

        var folder = Path.Combine(Path.GetTempPath(), "fbm-eng051-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var song = Path.Combine(folder, "loop.mp3");
            Run($"-f lavfi -i sine=frequency=440:duration=3 -c:a libmp3lame -b:a 128k \"{song}\"");
            Run($"-f lavfi -i \"nullsrc=size=320x180:rate=30:duration=8,geq=lum='N':cb=128:cr=128\" -c:v mpeg4 -q:v 2 -g 30 -pix_fmt yuv420p \"{Path.Combine(folder, "loop.mp4")}\"");

            using var session = LinuxNativeTestPlayer.Create([bass, ffmpeg]);
            MediaControlPlayer player = session.Player;
            player.LoopMedia = true;
            var loops = 0;
            player.MediaLooped += (_, _) => Interlocked.Increment(ref loops);
            Assert.True(player.Load(song), string.Join(" | ", session.Errors));
            var watch = Stopwatch.StartNew();
            Assert.True(player.Play());

            // At about 4.5 s the song has wrapped once (at 3 s). A background that restarted with it
            // would be about 1.5 s in (grey ~45, blue ~35); one that kept playing is about 4.5 s in
            // (grey ~135, blue ~140).
            Assert.True(SpinWait.SpinUntil(() => watch.Elapsed > TimeSpan.FromSeconds(4.5), TimeSpan.FromSeconds(10)));
            var blue = session.Sink.LastCentreBlue;
            output.WriteLine($"after {watch.Elapsed.TotalSeconds:F1} s: song loops {Volatile.Read(ref loops)}, position {player.Position.TotalSeconds:F1} s, background blue {blue}");
            Assert.True(Volatile.Read(ref loops) >= 1, "the song did not loop");
            Assert.InRange(blue, 90, 255);
            Assert.Empty(session.Errors);
            _ = player.Stop();
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>
    /// The background's own transport: it moves before Play and while the song is paused, stops on an
    /// explicit <c>PauseBackground</c> and moves again on <c>ResumeBackground</c>; with
    /// <c>BackgroundFollowsMedia</c> it waits for Play and pauses with the song, as before.
    /// </summary>
    [Fact]
    public void Background_LoopsOnItsOwn_UntilPausedExplicitly_OrFollowsTheMediaWhenSet()
    {
        if (!LinuxNativeTestPlayer.TryGetFolders(out var bass, out var ffmpeg) || !File.Exists(FfmpegCli))
            return;

        var folder = Path.Combine(Path.GetTempPath(), "fbm-eng051b-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var song = Path.Combine(folder, "song.mp3");
            Run($"-f lavfi -i sine=frequency=440:duration=20 -c:a libmp3lame -b:a 128k \"{song}\"");
            Run($"-f lavfi -i \"nullsrc=size=320x180:rate=30:duration=8,geq=lum='N':cb=128:cr=128\" -c:v mpeg4 -q:v 2 -g 30 -pix_fmt yuv420p \"{Path.Combine(folder, "song.mp4")}\"");

            using (var session = LinuxNativeTestPlayer.Create([bass, ffmpeg]))
            {
                MediaControlPlayer player = session.Player;
                Assert.True(player.Load(song), string.Join(" | ", session.Errors));
                Assert.True(Moves(session), "independent: the background should move before Play");
                Assert.True(player.Play());
                Assert.True(player.Pause());
                Assert.True(Moves(session), "independent: the background should keep moving while the song is paused");
                Assert.True(player.PauseBackground());
                Assert.True(player.IsBackgroundPaused);
                Assert.False(Moves(session), "an explicit background pause should hold it");
                Assert.True(player.Play());
                Assert.False(Moves(session), "Play must not end an explicit background pause");
                Assert.True(player.ResumeBackground());
                Assert.True(Moves(session), "ResumeBackground should move it again");
                Assert.Empty(session.Errors);
                _ = player.Stop();
            }

            using (var session = LinuxNativeTestPlayer.Create([bass, ffmpeg], new Core.Configuration.MediaPlayerConfiguration { BackgroundFollowsMedia = true }))
            {
                MediaControlPlayer player = session.Player;
                Assert.True(player.Load(song), string.Join(" | ", session.Errors));
                Assert.False(Moves(session), "following the media: the background waits for Play");
                Assert.True(player.Play());
                Assert.True(Moves(session), "following the media: it plays with the song");
                Assert.True(player.Pause());
                Assert.False(Moves(session), "following the media: it pauses with the song");
                Assert.Empty(session.Errors);
                _ = player.Stop();
            }
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); } catch (IOException) { }
        }
    }

    // Whether the presented background changes within a second (the clip brightens every frame).
    private static bool Moves(LinuxNativeTestPlayer session)
    {
        Thread.Sleep(400);
        var before = session.Sink.LastCentreBlue;
        Thread.Sleep(1000);
        return Math.Abs(session.Sink.LastCentreBlue - before) > 5;
    }

    private static void Run(string arguments)
    {
        var info = new ProcessStartInfo(FfmpegCli, "-hide_banner -loglevel error -y " + arguments) { UseShellExecute = false };
        info.Environment["LD_LIBRARY_PATH"] = "/opt/fatzbomb-native/ffmpeg/lib";
        using Process process = Process.Start(info)!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }
}
