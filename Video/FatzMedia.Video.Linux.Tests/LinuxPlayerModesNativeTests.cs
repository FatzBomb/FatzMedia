using FatzMedia.Video.Core.Abstractions;
using FatzMedia.Video.Core.Configuration;
using Xunit.Abstractions;

namespace FatzMedia.Video.Linux.Tests;

/// <summary>
/// Opt-in player-mode checks (043A slices 1 and 2) with real BASS and FFmpeg, headless on a frame
/// sink: what Background and Audio mode accept and refuse, and <c>SetModeAsync</c> keeping position,
/// transport state, volume, tempo and pitch. Media from <c>scripts/setup-linux-native.sh</c>
/// (<c>/opt/fatzbomb-native/media/formats</c>); needs <c>FATZBOMB_LINUX_BASS_DIR</c> and
/// <c>FATZBOMB_LINUX_FFMPEG_DIR</c>; skips without them. The WPF and Avalonia surfaces during a switch
/// are the owner's Windows smoke.
/// </summary>
[Collection("File type registry")]
public sealed class LinuxPlayerModesNativeTests(ITestOutputHelper output)
{
    static readonly string Formats = Path.Combine(LinuxNativeTestPlayer.MediaDir, "formats");
    static readonly string MusicVideo = Path.Combine(Formats, "video-aac.mp4");
    static readonly string SilentVideo = Path.Combine(Formats, "silent-video.mp4");
    static readonly string Song = Path.Combine(Formats, "tone.mp3");
    static readonly string Picture = Path.Combine(Formats, "picture.jpg");

    [Fact]
    public void Background_RefusesAudio_ShowsAPicture_AndLoopsAVideoSilently()
    {
        if (!TryGet(out var bass, out var ffmpeg))
            return;

        using var session = Create(bass, ffmpeg, MediaPlayerMode.Background);
        Assert.False(session.Player.Load(Song));
        Assert.True(session.WaitForDiagnostic("Background mode does not accept"), Dump(session));

        Assert.True(session.Player.Load(Picture), Dump(session));
        Assert.True(SpinWait.SpinUntil(() => session.Sink.Frames > 0, TimeSpan.FromSeconds(10)), "the picture was not presented");

        Assert.True(session.Player.Load(MusicVideo), Dump(session));
        Assert.True(session.Player.LoopMedia, "Background mode must loop");
        Assert.True(session.PlayUntil(0.5, TimeSpan.FromSeconds(10)), $"position {session.Player.Position}");
        Assert.False(session.HasDiagnostic("Global BASS initialized"), "Background mode initialized audio output");
        var frames = session.Sink.Frames;
        Assert.True(SpinWait.SpinUntil(() => session.Sink.Frames > frames + 10, TimeSpan.FromSeconds(10)), "no frames while looping");
        Assert.True(session.Sink.LastFrameHasContent);
    }

    [Fact]
    public void Audio_RefusesPicturesAndSilentVideo_AndPlaysAMusicVideosSound()
    {
        if (!TryGet(out var bass, out var ffmpeg))
            return;

        using var session = Create(bass, ffmpeg, MediaPlayerMode.Audio);
        Assert.False(session.Player.Load(Picture));
        Assert.True(session.WaitForDiagnostic("Audio mode does not accept"), Dump(session));
        Assert.False(session.Player.Load(SilentVideo));
        Assert.True(session.WaitForDiagnostic("no playable audio stream"), Dump(session));

        Assert.True(session.Player.Load(MusicVideo), Dump(session));
        Assert.True(session.PlayUntil(1.0, TimeSpan.FromSeconds(10)), $"position {session.Player.Position}");
        Assert.Equal(0, session.Sink.Frames);
    }

    [Fact]
    public async Task SetModeAsync_FullToAudioAndBack_KeepsPositionStateAndEffects()
    {
        if (!TryGet(out var bass, out var ffmpeg))
            return;

        using var session = Create(bass, ffmpeg, MediaPlayerMode.Full);
        var player = session.Player;
        Assert.True(player.Load(MusicVideo), Dump(session));
        Assert.True(session.PlayUntil(1.0, TimeSpan.FromSeconds(10)));
        Assert.True(player.Seek(TimeSpan.FromSeconds(4)));
        player.Effects.Volume = 0.6;
        player.Effects.Tempo = 110;
        player.Effects.Pitch = 2;
        Thread.Sleep(500);

        // Playing: Full -> Audio keeps the place and the settings.
        var before = player.Position;
        Assert.True(await player.SetModeAsync(MediaPlayerMode.Audio));
        Assert.Equal(MediaPlayerMode.Audio, player.Mode);
        AssertKept(session, before, MediaPlayerState.Playing, "Full -> Audio while playing");

        // Paused: Audio -> Full stays paused at the same place.
        Assert.True(player.Pause());
        Thread.Sleep(300);
        before = player.Position;
        Assert.True(await player.SetModeAsync(MediaPlayerMode.Full));
        AssertKept(session, before, MediaPlayerState.Paused, "Audio -> Full while paused");
        var frames = session.Sink.Frames;
        Assert.True(player.Play());
        Assert.True(SpinWait.SpinUntil(() => session.Sink.Frames > frames, TimeSpan.FromSeconds(10)), "no video after returning to Full");

        // Stopped stays stopped.
        Assert.True(player.Stop());
        Assert.True(SpinWait.SpinUntil(() => player.State == MediaPlayerState.Stopped, TimeSpan.FromSeconds(5)));
        Assert.True(await player.SetModeAsync(MediaPlayerMode.Audio));
        Thread.Sleep(500);
        Assert.Equal(MediaPlayerState.Stopped, player.State);
        output.WriteLine(string.Join(Environment.NewLine, session.Diagnostics.Where(static l => l.Contains("PlayerMode", StringComparison.Ordinal))));
    }

    [Fact]
    public async Task SetModeAsync_ToBackground_LoopsTheVideo_AndClosesAnAudioFile()
    {
        if (!TryGet(out var bass, out var ffmpeg))
            return;

        using var session = Create(bass, ffmpeg, MediaPlayerMode.Full);
        var player = session.Player;
        Assert.True(player.Load(MusicVideo), Dump(session));
        player.LoopMedia = false;
        Assert.True(session.PlayUntil(0.5, TimeSpan.FromSeconds(10)));
        Assert.True(await player.SetModeAsync(MediaPlayerMode.Background));
        Assert.True(player.LoopMedia, "Background mode must loop");
        var frames = session.Sink.Frames;
        Assert.True(SpinWait.SpinUntil(() => session.Sink.Frames > frames + 10, TimeSpan.FromSeconds(10)), "no frames in Background mode");

        Assert.True(await player.SetModeAsync(MediaPlayerMode.Full));
        Assert.True(player.Load(Song), Dump(session));
        Assert.True(session.PlayUntil(0.5, TimeSpan.FromSeconds(10)));
        Assert.True(await player.SetModeAsync(MediaPlayerMode.Background));
        Assert.True(session.WaitForDiagnostic("Background mode could not reopen"), Dump(session));
        output.WriteLine(string.Join(Environment.NewLine, session.Diagnostics.Where(static l => l.Contains("mode", StringComparison.OrdinalIgnoreCase))));
    }

    static void AssertKept(LinuxNativeTestPlayer session, TimeSpan before, MediaPlayerState state, string step)
    {
        var player = session.Player;
        Assert.True(SpinWait.SpinUntil(() => player.State == state, TimeSpan.FromSeconds(5)), $"{step}: state {player.State}");
        var drift = Math.Abs((player.Position - before).TotalSeconds);
        Assert.True(drift < (state == MediaPlayerState.Playing ? 1.5 : 0.5), $"{step}: position {before} -> {player.Position}");
        Assert.Equal(0.6, player.Effects.Volume, 3);
        Assert.Equal(110, player.Effects.Tempo);
        Assert.Equal(2, player.Effects.Pitch);
    }

    static bool TryGet(out string bass, out string ffmpeg) =>
        LinuxNativeTestPlayer.TryGetFolders(out bass, out ffmpeg)
        && File.Exists(MusicVideo) && File.Exists(SilentVideo) && File.Exists(Song) && File.Exists(Picture);

    static LinuxNativeTestPlayer Create(string bass, string ffmpeg, MediaPlayerMode mode) =>
        LinuxNativeTestPlayer.Create([bass, ffmpeg], new MediaPlayerConfiguration(VideoDecodingMethod.Software) { Mode = mode });

    static string Dump(LinuxNativeTestPlayer session) => string.Join(" | ", session.Errors.Concat(session.Diagnostics.TakeLast(6)));
}
