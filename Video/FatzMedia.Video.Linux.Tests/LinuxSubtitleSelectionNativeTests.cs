using Xunit.Abstractions;

namespace FatzMedia.Video.Linux.Tests;

/// <summary>
/// Opt-in native check for ENG-045 (owner smoke 2026-11-05: "the embedded subtitle choice returns to the
/// default track"): with an MKV of two embedded text tracks, a chosen track and Off stay chosen after the
/// background scan, over time and across a seek; Off chosen right after an open survives the scan.
/// Needs <c>FATZBOMB_LINUX_FFMPEG_DIR</c> and
/// <c>/opt/fatzbomb-native/media/subtitles2.mkv</c> (<c>scripts/setup-linux-native.sh</c>).
/// </summary>
[Collection("File type registry")]
public sealed class LinuxSubtitleSelectionNativeTests(ITestOutputHelper output)
{
    static readonly string Media = Path.Combine(LinuxNativeTestPlayer.MediaDir, "subtitles2.mkv");

    [Fact]
    public void ChosenTrackAndOff_StayChosen_AfterTheScanAndASeek()
    {
        if (!LinuxNativeTestPlayer.TryGetFfmpegFolder(out var ffmpeg) || !File.Exists(Media))
            return;

        using var test = LinuxNativeTestPlayer.Create([ffmpeg]);
        var player = test.Player;
        var scans = 0;
        player.SubtitleTracksChanged += (_, _) => Interlocked.Increment(ref scans);

        Assert.True(player.Load(Media));
        Assert.True(player.Play());
        Assert.True(SpinWait.SpinUntil(() => player.SubtitleTracks.Count(t => t.IsEmbedded) == 2, TimeSpan.FromSeconds(10)), "two embedded tracks");
        var second = player.SubtitleTracks.Where(t => t.IsEmbedded).Last().Id;

        Assert.True(player.SelectSubtitleTrack(second));
        Thread.Sleep(1500);
        Assert.Equal(second, player.SelectedSubtitleTrackId);

        Assert.True(player.SelectSubtitleTrack(null));
        Assert.True(player.Seek(TimeSpan.FromSeconds(5)));
        Thread.Sleep(1500);
        Assert.Null(player.SelectedSubtitleTrackId);
        output.WriteLine($"scan notices: {scans}");
    }

    [Fact]
    public void Off_ChosenRightAfterTheOpen_SurvivesTheScan()
    {
        if (!LinuxNativeTestPlayer.TryGetFfmpegFolder(out var ffmpeg) || !File.Exists(Media))
            return;

        using var test = LinuxNativeTestPlayer.Create([ffmpeg]);
        var player = test.Player;
        Assert.True(player.Load(Media));
        Assert.True(player.SelectSubtitleTrack(null));
        Assert.True(player.Play());
        Assert.True(SpinWait.SpinUntil(() => player.SubtitleTracks.Count(t => t.IsEmbedded) == 2, TimeSpan.FromSeconds(10)), "two embedded tracks");
        Thread.Sleep(1000);
        Assert.Null(player.SelectedSubtitleTrackId);
    }
}
