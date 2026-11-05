using FatzBomb.MediaControl.Core.Configuration;
using System.Collections.Concurrent;
using Xunit.Abstractions;

namespace FatzBomb.MediaControl.Linux.Tests;

/// <summary>
/// Opt-in checks against real internet streams (039A follow-ups, slice 3): an https radio station plays
/// on the live clock and reports its titles, and a public HLS stream plays in Background mode. Set
/// <c>FATZBOMB_LINUX_NETWORK=1</c> plus <see cref="LinuxNativeTestPlayer"/>'s BASS and FFmpeg folders;
/// optionally <c>FATZBOMB_LINUX_NETWORK_CA_FILE</c> (a proxy's CA bundle; FFmpeg tunnels through the
/// proxy in <c>http_proxy</c>). Certificate failures are tested against a local server in
/// <see cref="LinuxNetworkVideoNativeTests"/>, not here: behind a TLS-intercepting proxy a bad remote
/// certificate never reaches the player.
/// </summary>
[Collection("File type registry")]
public sealed class LinuxInternetStreamNativeTests(ITestOutputHelper output)
{
    private static readonly Uri Radio = new("https://stream.radioparadise.com/mp3-128");
    private static readonly Uri Hls = new("https://test-streams.mux.dev/x36xhzz/x36xhzz.m3u8");

    [Fact]
    public void HttpsRadio_PlaysLive_AndReportsTitles()
    {
        if (!TryGet(out var bass, out var ffmpeg, out var tls))
            return;

        using var session = LinuxNativeTestPlayer.Create([bass, ffmpeg], new MediaPlayerConfiguration(VideoDecodingMethod.Software) { NetworkTls = tls }, attachSurface: false);
        var titles = new ConcurrentQueue<string>();
        session.Player.StreamMetadataChanged += (_, e) => titles.Enqueue($"{e.Artist} - {e.Title}");
        Assert.True(session.Player.Load(MediaInputSource.FromUri(Radio)), Dump(session));
        Assert.True(session.PlayUntil(3.0, TimeSpan.FromSeconds(30)), $"position {session.Player.Position}; {Dump(session)}");
        var start = session.Player.Position;
        Thread.Sleep(5000);
        var advanced = (session.Player.Position - start).TotalSeconds;
        output.WriteLine($"radio: {advanced:F2} s played in 5 s; titles so far: {string.Join(" | ", titles)}");
        Assert.InRange(advanced, 4.0, 6.0);
        if (titles.IsEmpty)
            output.WriteLine("no title within the run (the station may not have changed songs); not a failure");
        _ = session.Player.Stop();
    }

    [Fact]
    public void PublicHls_PlaysInBackgroundMode()
    {
        if (!TryGet(out var bass, out var ffmpeg, out var tls))
            return;

        using var session = LinuxNativeTestPlayer.Create([bass, ffmpeg], new MediaPlayerConfiguration(VideoDecodingMethod.Software) { Mode = MediaPlayerMode.Background, NetworkTls = tls });
        Assert.True(session.Player.Load(MediaInputSource.FromUri(Hls)), Dump(session));
        Assert.True(session.PlayUntil(2.0, TimeSpan.FromSeconds(45)), $"position {session.Player.Position}; {Dump(session)}");
        var frames = session.Sink.Frames;
        Assert.True(SpinWait.SpinUntil(() => session.Sink.Frames > frames + 30, TimeSpan.FromSeconds(30)), $"frames {session.Sink.Frames}");
        Assert.True(session.Sink.LastFrameHasContent);
        output.WriteLine($"HLS: {session.Sink.Frames} frames, position {session.Player.Position}");
    }

    private static bool TryGet(out string bass, out string ffmpeg, out NetworkTlsPolicy? tls)
    {
        tls = Environment.GetEnvironmentVariable("FATZBOMB_LINUX_NETWORK_CA_FILE") is { Length: > 0 } ca
            ? new NetworkTlsPolicy { CertificateAuthorityFile = ca }
            : null;
        return LinuxNativeTestPlayer.TryGetFolders(out bass, out ffmpeg)
            && Environment.GetEnvironmentVariable("FATZBOMB_LINUX_NETWORK") == "1";
    }

    private static string Dump(LinuxNativeTestPlayer session) => string.Join(" | ", session.Errors.Concat(session.Diagnostics.TakeLast(6)));
}
