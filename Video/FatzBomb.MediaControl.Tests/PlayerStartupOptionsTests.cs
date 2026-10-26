using FatzBomb.MediaControl.Core.Configuration;
using FatzBomb.MediaControl.Player.Shared;
using FatzBomb.MediaControl.Player.Shared.Hosting;

namespace FatzBomb.MediaControl.Tests;

/// <summary>Stage 044A: the command line shared by the test players.</summary>
public sealed class PlayerStartupOptionsTests
{
    [Fact]
    public void Parse_NoArguments_GivesTheDefaults()
    {
        PlayerStartupOptions options = PlayerStartupOptions.Parse([]);

        Assert.Null(options.MediaPath);
        Assert.Null(options.NetworkUri);
        Assert.Null(options.Renderer);
        Assert.False(options.SoftwareRenderer);
        Assert.Equal(TimeSpan.Zero, options.FadeNext);
        Assert.Null(options.EqualizerGains);
        Assert.Equal(MediaPlayerMode.Full, options.Mode);
        Assert.Empty(options.NativeDirectories);
    }

    [Theory]
    [InlineData("https://example.com/live.m3u8")]
    [InlineData("http://example.com/a.mp4")]
    public void Parse_APositionalUrlIsTheStartupStream(string url)
    {
        PlayerStartupOptions options = PlayerStartupOptions.Parse(["--players=2", url, "second.mp3"]);

        Assert.Null(options.MediaPath);
        Assert.Equal(new Uri(url), options.NetworkUri);
    }

    [Fact]
    public void Parse_AUrlFlagReplacesAPositionalFile()
    {
        PlayerStartupOptions options = PlayerStartupOptions.Parse(["song.mp3", "--url=https://example.com/live.mp3"]);

        Assert.Null(options.MediaPath);
        Assert.Equal(new Uri("https://example.com/live.mp3"), options.NetworkUri);
    }

    [Theory]
    [InlineData("--url=file:///music/a.mp3")]
    [InlineData("--url=ftp://example.com/a.mp3")]
    public void Parse_OnlyHttpAndHttpsUrlsAreStreams(string argument)
    {
        PlayerStartupOptions options = PlayerStartupOptions.Parse([argument, "song.mp3"]);

        Assert.Null(options.NetworkUri);
        Assert.Equal("song.mp3", options.MediaPath);
    }

    [Fact]
    public void Parse_UrlFlagWinsOverAPositionalUrl()
    {
        PlayerStartupOptions options = PlayerStartupOptions.Parse(["https://example.com/a.m3u8", "--url=https://example.com/b.m3u8"]);

        Assert.Null(options.MediaPath);
        Assert.Equal(new Uri("https://example.com/b.m3u8"), options.NetworkUri);
    }

    [Theory]
    [InlineData("--mode=audio", MediaPlayerMode.Audio)]
    [InlineData("--MODE=Background", MediaPlayerMode.Background)]
    [InlineData("--mode=karaoke", MediaPlayerMode.Full)]
    [InlineData("--mode=7", MediaPlayerMode.Full)]
    public void Parse_ReadsTheMode(string argument, MediaPlayerMode expected) =>
        Assert.Equal(expected, PlayerStartupOptions.Parse([argument]).Mode);

    [Fact]
    public void Parse_NativeDirectoriesKeepTheirOrder() =>
        Assert.Equal(["/opt/ffmpeg", "C:\\bass"], PlayerStartupOptions.Parse(["--native=/opt/ffmpeg", "--native=", "--native=\"C:\\bass\""]).NativeDirectories);

    [Fact]
    public void WithoutStartupMedia_KeepsTheSettingsForAnotherPlayer()
    {
        PlayerStartupOptions options = PlayerStartupOptions.Parse(["song.mp3", "--url=https://example.com/a.m3u8", "--prepare-next=next.mp3", "--mode=audio", "--auto-next"]);

        PlayerStartupOptions other = options.WithoutStartupMedia();

        Assert.Null(other.MediaPath);
        Assert.Null(other.NetworkUri);
        Assert.Null(other.PrepareNextPath);
        Assert.Equal(MediaPlayerMode.Audio, other.Mode);
        Assert.True(other.AutoNext);
    }

    [Fact]
    public void NativePaths_KeepExistingDirectoriesInOrderWithoutDuplicates()
    {
        var first = Directory.CreateTempSubdirectory("fatzbomb-native-a").FullName;
        var second = Directory.CreateTempSubdirectory("fatzbomb-native-b").FullName;
        try
        {
            IReadOnlyList<string> paths = PlayerNativePaths.Combine(
                [second, Path.Combine(first, "missing")],
                [first, second + Path.DirectorySeparatorChar, ""]);

            Assert.Equal([second, first], paths);
        }
        finally
        {
            Directory.Delete(first);
            Directory.Delete(second);
        }
    }

    [Fact]
    public void Parse_ReadsEverySharedFlag()
    {
        PlayerStartupOptions options = PlayerStartupOptions.Parse(
        [
            "--RESET-PITCH-ON-LOAD",
            "--reset-tempo-on-load",
            "--visual-transform-demo",
            "--trim-silence",
            "--no-hdr-tonemap",
            "--hdr-tonemap-cpu",
            "--hdr-output",
            "--prepare-next=\"C:\\Music\\next.mp3\"",
            "--auto-next",
            "--fade-next=2.5",
            "--net-retries=3",
            "--net-ca-file=ca.pem",
            "--net-allow-invalid-certs",
            "--eq=1, -2.5,x,4",
            "--software",
            "--no-auto-hide",
            "song.mp3",
        ]);

        Assert.True(options.ResetPitchOnLoad);
        Assert.True(options.ResetTempoOnLoad);
        Assert.True(options.VisualTransformDemo);
        Assert.True(options.TrimSilence);
        Assert.True(options.DisableHdrToneMapping);
        Assert.True(options.PreferCpuHdrToneMapping);
        Assert.True(options.HdrOutput);
        Assert.Equal("C:\\Music\\next.mp3", options.PrepareNextPath);
        Assert.True(options.AutoNext);
        Assert.Equal(TimeSpan.FromSeconds(2.5), options.FadeNext);
        Assert.Equal(3, options.NetworkRetries);
        Assert.Equal("ca.pem", options.NetworkCaFile);
        Assert.True(options.NetworkAllowInvalidCertificates);
        Assert.Equal([1, -2.5, 0, 4], options.EqualizerGains);
        Assert.True(options.SoftwareRenderer);
        Assert.False(options.AutoHideSurfaces);
        Assert.Equal("song.mp3", options.MediaPath);
        Assert.True(PlayerStartupOptions.Parse([]).AutoHideSurfaces);
    }

    [Fact]
    public void Parse_RendererLastWinsAndOtherValuesFirstWins()
    {
        PlayerStartupOptions options = PlayerStartupOptions.Parse(
        [
            "--renderer=gpu",
            "--renderer=\"warp\"",
            "--prepare-next=",
            "--prepare-next=a.mp3",
            "--prepare-next=b.mp3",
            "--url=not a uri",
            "--url=https://example.com/live.m3u8",
            "--url=http://example.com/other.mp3",
        ]);

        Assert.Equal("warp", options.Renderer);
        Assert.Equal("a.mp3", options.PrepareNextPath);
        Assert.Equal(new Uri("https://example.com/live.m3u8"), options.NetworkUri);
    }

    [Theory]
    [InlineData("--fade-next=-3", 0)]
    [InlineData("--fade-next=90", PlayerStartupOptions.MaxFadeNextSeconds)]
    [InlineData("--fade-next=NaN", 0)]
    [InlineData("--fade-next=soon", 0)]
    public void Parse_FadeNextIsClampedToTheEngineRange(string argument, double expectedSeconds) =>
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), PlayerStartupOptions.Parse([argument]).FadeNext);

    [Fact]
    public void Parse_InvalidRetriesAreZero() =>
        Assert.Equal(0, PlayerStartupOptions.Parse(["--net-retries=many"]).NetworkRetries);

    [Fact]
    public void HostHelpers_FindFlagsAndValues()
    {
        string[] args = ["--No-Auto-Hide", "--players=2", "--players=\"3\""];

        Assert.True(PlayerStartupOptions.HasFlag(args, "--no-auto-hide"));
        Assert.False(PlayerStartupOptions.HasFlag(args, "--players"));
        Assert.Equal(["2", "3"], PlayerStartupOptions.Values(args, "--players="));
    }
}
