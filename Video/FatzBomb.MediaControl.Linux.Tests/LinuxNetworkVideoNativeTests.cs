using FatzBomb.MediaControl.Core.Configuration;
using FatzBomb.MediaControl.Linux.Runtime;
using FatzBomb.MediaControl.Rendering.Software;
using System.Collections.Concurrent;
using System.Diagnostics;
using Xunit.Abstractions;

namespace FatzBomb.MediaControl.Linux.Tests;

/// <summary>
/// Opt-in native checks for Stage 039A (network video through FFmpeg's network path) against an
/// in-process HTTP server on 127.0.0.1. Set <c>FATZBOMB_LINUX_FFMPEG_DIR</c> to a folder of
/// FFmpeg 9 shared libraries. Media default to <c>/opt/fatzbomb-native/media/h264_1080p.mp4</c> and
/// <c>/opt/fatzbomb-native/media/hls/index.m3u8</c> (override the folder with
/// <c>FATZBOMB_LINUX_MEDIA_DIR</c>). Without them the tests return immediately. No BASS is loaded,
/// so video plays its picture on the playback clock (ENG-033); the timings written to the test
/// output are the 039A measurements.
/// </summary>
[Collection("File type registry")]
public sealed class LinuxNetworkVideoNativeTests(ITestOutputHelper output)
{
    private const string DefaultMediaDirectory = "/opt/fatzbomb-native/media";
    private const string Mp4 = "h264_1080p.mp4";
    private const string HlsPlaylist = "hls/index.m3u8";

    [Fact]
    public void ProgressiveMp4_PresentsFramesAndSeeks()
    {
        if (!TryGetEnvironment(out var ffmpeg, out var media))
            return;

        using var server = new LocalHttpMediaServer(media);
        using var session = PlayerSession.Create(ffmpeg, MediaPlayerMode.Full);
        Uri uri = server.UriFor(Mp4);

        var startup = Stopwatch.StartNew();
        Assert.True(session.Player.Load(MediaInputSource.FromUri(uri)), session.Errors);
        var loadMs = startup.Elapsed.TotalMilliseconds;
        MediaSourceCapabilities? capabilities = session.Player.CurrentSourceCapabilities;
        Assert.NotNull(capabilities);
        Assert.Equal(MediaSourceClass.NetworkFile, capabilities.SourceClass);
        Assert.True(capabilities.IsSeekable);
        Assert.True(capabilities.IsDurationKnown);
        Assert.True(capabilities.IsBuffered);
        Assert.InRange(session.Player.Duration.TotalSeconds, 9.5, 10.5);

        Assert.True(session.Player.Play());
        var framesAtPlay = session.Sink.Frames;
        Assert.True(SpinWait.SpinUntil(() => session.Sink.Frames > framesAtPlay, TimeSpan.FromSeconds(15)), "no frame after Play");
        var firstFrameMs = startup.Elapsed.TotalMilliseconds;
        Assert.True(SpinWait.SpinUntil(() => session.Sink.Frames >= framesAtPlay + 10 && session.Player.Position > TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(15)), $"frames {session.Sink.Frames}");
        Assert.True(session.Sink.LastFrameHasContent, "presented frame looks blank");

        // FFmpeg may serve a forward seek by reading on through the open connection; a backward
        // seek needs a new byte-range request.
        var seekMs = MeasureSeek(session, 6.0);
        Assert.True(session.Player.Position >= TimeSpan.FromSeconds(5.9), $"position after seek {session.Player.Position}");
        var requestsBeforeBackSeek = server.Requests.Count;
        var backSeekMs = MeasureSeek(session, 1.0);
        Assert.InRange(session.Player.Position.TotalSeconds, 0.9, 4);
        Assert.True(server.Requests.Skip(requestsBeforeBackSeek).Any(request => request.RangeStart is > 0), "the backward seek made no byte-range request");
        var rangeRequests = server.Requests.Count(request => request.RangeStart is > 0);

        output.WriteLine($"progressive MP4: load {loadMs:F0} ms, load-to-first-frame {firstFrameMs:F0} ms, seek to 6 s {seekMs:F0} ms, " +
            $"back to 1 s {backSeekMs:F0} ms, requests {server.Requests.Count} ({rangeRequests} with a range)");
        Assert.Empty(session.ErrorList);
        _ = session.Player.Stop();
    }

    [Fact]
    public void HlsVod_PresentsFramesAndSeeks()
    {
        if (!TryGetEnvironment(out var ffmpeg, out var media) || !File.Exists(Path.Combine(media, HlsPlaylist)))
            return;

        using var server = new LocalHttpMediaServer(media);
        using var session = PlayerSession.Create(ffmpeg, MediaPlayerMode.Full);

        var startup = Stopwatch.StartNew();
        Assert.True(session.Player.Load(MediaInputSource.FromUri(server.UriFor(HlsPlaylist))), session.Errors);
        var loadMs = startup.Elapsed.TotalMilliseconds;
        MediaSourceCapabilities? capabilities = session.Player.CurrentSourceCapabilities;
        Assert.NotNull(capabilities);
        Assert.Equal(MediaSourceClass.NetworkFile, capabilities.SourceClass);
        Assert.True(capabilities.IsSeekable);
        Assert.False(capabilities.IsLive);
        Assert.InRange(session.Player.Duration.TotalSeconds, 9.5, 10.5);

        Assert.True(session.Player.Play());
        Assert.True(SpinWait.SpinUntil(() => session.Sink.Frames >= 5, TimeSpan.FromSeconds(15)), $"frames {session.Sink.Frames}");
        var firstFrameMs = startup.Elapsed.TotalMilliseconds;
        Assert.True(SpinWait.SpinUntil(() => session.Player.Position > TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(15)));
        Assert.True(session.Sink.LastFrameHasContent, "presented frame looks blank");

        var seekMs = MeasureSeek(session, 7.0);
        Assert.True(session.Player.Position >= TimeSpan.FromSeconds(6.9), $"position after seek {session.Player.Position}");
        output.WriteLine($"HLS VOD: load {loadMs:F0} ms, load-to-first-frame {firstFrameMs:F0} ms, seek to 7 s {seekMs:F0} ms, requests {server.Requests.Count}");
        Assert.Empty(session.ErrorList);
        _ = session.Player.Stop();
    }

    [Fact]
    public void HlsLive_IsNotSeekable_HasNoDuration_AndClosesPromptly()
    {
        if (!TryGetEnvironment(out var ffmpeg, out var media) || !File.Exists(Path.Combine(media, HlsPlaylist)))
            return;

        using var server = new LocalHttpMediaServer(media);
        // The same segments as a live playlist: no #EXT-X-ENDLIST, no VOD type.
        server.Override("hls/live.m3u8", string.Join('\n',
            "#EXTM3U", "#EXT-X-VERSION:3", "#EXT-X-TARGETDURATION:2", "#EXT-X-MEDIA-SEQUENCE:0",
            "#EXTINF:2.0,", "seg000.ts", "#EXTINF:2.0,", "seg001.ts", "#EXTINF:2.0,", "seg002.ts",
            "#EXTINF:2.0,", "seg003.ts", "#EXTINF:2.0,", "seg004.ts", string.Empty));
        using var session = PlayerSession.Create(ffmpeg, MediaPlayerMode.Full);

        var ended = 0;
        session.Player.MediaEnded += (_, _) => Interlocked.Increment(ref ended);
        Assert.True(session.Player.Load(MediaInputSource.FromUri(server.UriFor("hls/live.m3u8"))), session.Errors);
        MediaSourceCapabilities? capabilities = session.Player.CurrentSourceCapabilities;
        Assert.NotNull(capabilities);
        Assert.Equal(MediaSourceClass.NetworkLive, capabilities.SourceClass);
        Assert.False(capabilities.IsSeekable);
        Assert.False(capabilities.IsDurationKnown);
        Assert.Equal(TimeSpan.Zero, session.Player.Duration);

        Assert.True(session.Player.Play());
        Assert.True(SpinWait.SpinUntil(() => session.Sink.Frames >= 5, TimeSpan.FromSeconds(15)), $"frames {session.Sink.Frames}");
        Assert.False(session.Player.Seek(1.0));

        // The live edge runs out after the last listed segment and FFmpeg waits for the playlist to
        // grow; Close must not wait for that.
        Thread.Sleep(TimeSpan.FromSeconds(1));
        var close = Stopwatch.StartNew();
        session.Player.Close();
        output.WriteLine($"HLS live: close during playback {close.Elapsed.TotalMilliseconds:F0} ms");
        Assert.True(close.Elapsed < TimeSpan.FromSeconds(3), $"close took {close.Elapsed}");
        // The reads aborted by Close end the stream, but that is not the media's end.
        Thread.Sleep(TimeSpan.FromMilliseconds(300));
        Assert.Equal(0, Volatile.Read(ref ended));
    }

    [Fact]
    public async Task CloseDuringConnect_ToAHungServer_ReturnsQuickly()
    {
        if (!TryGetEnvironment(out var ffmpeg, out _))
            return;

        using var hung = new SilentTcpServer();
        using var session = PlayerSession.Create(ffmpeg, MediaPlayerMode.Full, new NetworkRetryPolicy { MaxRetries = 2, ConnectTimeout = TimeSpan.FromSeconds(20) });

        Task<bool> load = Task.Run(() => session.Player.Load(MediaInputSource.FromUri(hung.Uri, MediaNetworkContent.Video)));
        await Task.Delay(TimeSpan.FromMilliseconds(500));
        Assert.False(load.IsCompleted, "the load did not wait for the hung server");

        var close = Stopwatch.StartNew();
        session.Player.Close();
        var closeMs = close.Elapsed.TotalMilliseconds;
        Assert.Same(load, await Task.WhenAny(load, Task.Delay(TimeSpan.FromSeconds(3))));
        Assert.False(await load);
        output.WriteLine($"close during connect: {closeMs:F0} ms (load finished {close.Elapsed.TotalMilliseconds:F0} ms after Close)");
        Assert.True(close.Elapsed < TimeSpan.FromSeconds(2), $"close took {close.Elapsed}");
        Assert.Contains(session.DiagnosticList, line => line.Contains("cancelled", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ServerDroppingMidStream_TriggersRetryAndBufferingEvents()
    {
        if (!TryGetEnvironment(out var ffmpeg, out var media))
            return;

        var fileLength = new FileInfo(Path.Combine(media, Mp4)).Length;
        using var server = new LocalHttpMediaServer(media);
        // The sample keeps its index at the end, so FFmpeg reads the head, then the index, then
        // streams from the start. That playback response is cut at 45 %; the reconnect it causes
        // is answered after 1.5 s.
        var cut = false;
        var delayed = false;
        server.Plan = request =>
        {
            if (!cut && IsPlaybackRequest(request, fileLength))
            {
                cut = true;
                return new HttpResponsePlan(TruncateAfterBytes: fileLength * 45 / 100);
            }

            if (cut && !delayed && request.RangeStart > fileLength / 4)
            {
                delayed = true;
                return new HttpResponsePlan(DelayBeforeResponse: TimeSpan.FromSeconds(1.5));
            }

            return new HttpResponsePlan();
        };

        using var session = PlayerSession.Create(ffmpeg, MediaPlayerMode.Full, new NetworkRetryPolicy { MaxRetries = 3 });
        Assert.True(session.Player.Load(MediaInputSource.FromUri(server.UriFor(Mp4))), session.Errors);
        Assert.True(session.Player.Play());

        Assert.True(SpinWait.SpinUntil(() => session.BufferingList.Contains(true), TimeSpan.FromSeconds(20)),
            $"no buffering start; diagnostics: {string.Join(" | ", session.DiagnosticList)}");
        var stallStart = Stopwatch.StartNew();
        Assert.True(SpinWait.SpinUntil(() => session.BufferingList.LastOrDefault() == false && session.BufferingList.Contains(false), TimeSpan.FromSeconds(10)), "buffering never ended");
        var framesAfterStall = session.Sink.Frames;
        Assert.True(SpinWait.SpinUntil(() => session.Sink.Frames > framesAfterStall + 5, TimeSpan.FromSeconds(10)), "no frames after the reconnect");
        Assert.Contains(session.DiagnosticList, line => line.Contains("Network retry", StringComparison.Ordinal));

        output.WriteLine($"mid-stream drop: buffering events [{string.Join(", ", session.BufferingList)}], stall ended after {stallStart.Elapsed.TotalMilliseconds:F0} ms; " +
            $"retry diagnostics: {string.Join(" | ", session.DiagnosticList.Where(line => line.Contains("Network", StringComparison.Ordinal)))}");
        _ = session.Player.Stop();
    }

    [Fact]
    public void ServerGoneMidStream_WithoutRetries_EndsThePlayback()
    {
        if (!TryGetEnvironment(out var ffmpeg, out var media))
            return;

        var fileLength = new FileInfo(Path.Combine(media, Mp4)).Length;
        using var server = new LocalHttpMediaServer(media);
        server.Plan = request => IsPlaybackRequest(request, fileLength)
            ? new HttpResponsePlan(TruncateAfterBytes: fileLength * 30 / 100)
            : new HttpResponsePlan();

        using var session = PlayerSession.Create(ffmpeg, MediaPlayerMode.Full);
        var ended = new ManualResetEventSlim();
        session.Player.MediaEnded += (_, _) => ended.Set();
        Assert.True(session.Player.Load(MediaInputSource.FromUri(server.UriFor(Mp4))), session.Errors);
        Assert.True(session.Player.Play());

        Assert.True(ended.Wait(TimeSpan.FromSeconds(20)), $"no MediaEnded; position {session.Player.Position}; diagnostics: {string.Join(" | ", session.DiagnosticList)}");
        Assert.True(session.Player.Position < TimeSpan.FromSeconds(9), $"ended at {session.Player.Position}");
        Assert.Contains(session.DiagnosticList, line => line.Contains("Network read failed", StringComparison.Ordinal));
    }

    [Fact]
    public void BackgroundMode_LoopsASeekableNetworkVideo()
    {
        if (!TryGetEnvironment(out var ffmpeg, out var media))
            return;

        using var server = new LocalHttpMediaServer(media);
        using var session = PlayerSession.Create(ffmpeg, MediaPlayerMode.Background);
        var looped = new ManualResetEventSlim();
        session.Player.MediaLooped += (_, _) => looped.Set();

        Assert.True(session.Player.Load(MediaInputSource.FromUri(server.UriFor(Mp4))), session.Errors);
        Assert.True(session.Player.LoopMedia);
        Assert.True(session.Player.Play());
        Assert.True(SpinWait.SpinUntil(() => session.Sink.Frames >= 5, TimeSpan.FromSeconds(15)), $"frames {session.Sink.Frames}");
        Assert.True(session.Player.Seek(8.5));
        Assert.True(looped.Wait(TimeSpan.FromSeconds(10)), $"no loop; position {session.Player.Position}");
        var framesAfterLoop = session.Sink.Frames;
        Assert.True(SpinWait.SpinUntil(() => session.Sink.Frames > framesAfterLoop + 5, TimeSpan.FromSeconds(10)), "no frames after the loop");
        Assert.Empty(session.ErrorList);
        _ = session.Player.Stop();
    }

    [Fact]
    public void AudioMode_WithoutBass_RejectsNetworkVideoClearly()
    {
        if (!TryGetEnvironment(out var ffmpeg, out var media))
            return;

        using var server = new LocalHttpMediaServer(media);
        using var session = PlayerSession.Create(ffmpeg, MediaPlayerMode.Audio, attachSurface: false);
        Assert.False(session.Player.Load(MediaInputSource.FromUri(server.UriFor(Mp4))));
        Assert.True(SpinWait.SpinUntil(() => session.ErrorList.Any(line => line.Contains("Audio engine unavailable", StringComparison.Ordinal)), TimeSpan.FromSeconds(5)),
            $"errors: {session.Errors}; diagnostics: {string.Join(" | ", session.DiagnosticList)}");
        Assert.Empty(server.Requests);
    }

    [Fact]
    public void Https_SelfSignedServer_IsRejectedByDefault_WithTheCertificateError()
    {
        if (!TryGetEnvironment(out var ffmpeg, out var media))
            return;

        using var certificate = SelfSignedCertificate.Create();
        using var server = new LocalHttpMediaServer(media, certificate: certificate.Certificate);
        // Retries are on: a rejected certificate must not be retried.
        using var session = PlayerSession.Create(ffmpeg, MediaPlayerMode.Full, new NetworkRetryPolicy { MaxRetries = 2, InitialDelay = TimeSpan.Zero });

        Assert.False(session.Player.Load(MediaInputSource.FromUri(server.UriFor(Mp4))));

        Assert.True(SpinWait.SpinUntil(() => session.ErrorList.Count > 0, TimeSpan.FromSeconds(5)), "no MediaError");
        output.WriteLine($"rejected: {session.Errors}");
        Assert.Contains(session.ErrorList, line => line.Contains("certificate verification failed for 127.0.0.1", StringComparison.Ordinal)
            && line.Contains("certificate verify failed", StringComparison.Ordinal));
        Assert.Contains(session.DiagnosticList, line => line.StartsWith("Error Network: Network stream unavailable after 1 attempt(s): certificate verification failed for 127.0.0.1", StringComparison.Ordinal));
        Assert.Empty(server.Requests);
        // One handshake per cursor at most (the picture; the audio cursor opens only after it).
        Assert.Equal(1, server.Handshakes);
    }

    [Fact]
    public void Https_SelfSignedServer_IsAccepted_WhenTheHostAllowsInvalidCertificates()
    {
        if (!TryGetEnvironment(out var ffmpeg, out var media))
            return;

        using var certificate = SelfSignedCertificate.Create();
        using var server = new LocalHttpMediaServer(media, certificate: certificate.Certificate);
        using var session = PlayerSession.Create(ffmpeg, MediaPlayerMode.Full, tls: new NetworkTlsPolicy { AllowInvalidCertificates = true });

        AssertPlays(session, server.UriFor(Mp4));
        Assert.Contains(session.DiagnosticList, line => line.Contains("Certificate verification is off", StringComparison.Ordinal));
    }

    [Fact]
    public void Https_SelfSignedServer_IsAccepted_WithItsCertificateAsTheCaFile()
    {
        if (!TryGetEnvironment(out var ffmpeg, out var media))
            return;

        using var certificate = SelfSignedCertificate.Create();
        using var server = new LocalHttpMediaServer(media, certificate: certificate.Certificate);
        using var session = PlayerSession.Create(ffmpeg, MediaPlayerMode.Full, tls: new NetworkTlsPolicy { CertificateAuthorityFile = certificate.PemPath });

        AssertPlays(session, server.UriFor(Mp4));
    }

    [Fact]
    public void Https_HlsSegments_UseTheHostsCaFileToo()
    {
        if (!TryGetEnvironment(out var ffmpeg, out var media) || !File.Exists(Path.Combine(media, HlsPlaylist)))
            return;

        // FFmpeg's HLS demuxer opens segments without the open's TLS options; the engine adds them.
        using var certificate = SelfSignedCertificate.Create();
        using var server = new LocalHttpMediaServer(media, certificate: certificate.Certificate);
        using var session = PlayerSession.Create(ffmpeg, MediaPlayerMode.Full, tls: new NetworkTlsPolicy { CertificateAuthorityFile = certificate.PemPath });

        AssertPlays(session, server.UriFor(HlsPlaylist));
        Assert.Contains(server.Requests, request => request.Path.EndsWith(".ts", StringComparison.Ordinal));
    }

    [Fact]
    public void Https_WithAMissingCaFile_FailsWithTheCertificateError()
    {
        if (!TryGetEnvironment(out var ffmpeg, out var media))
            return;

        using var certificate = SelfSignedCertificate.Create();
        using var server = new LocalHttpMediaServer(media, certificate: certificate.Certificate);
        using var session = PlayerSession.Create(ffmpeg, MediaPlayerMode.Full, tls: new NetworkTlsPolicy { CertificateAuthorityFile = "/nonexistent/fbm-ca.pem" });

        Assert.False(session.Player.Load(MediaInputSource.FromUri(server.UriFor(Mp4))));
        Assert.True(SpinWait.SpinUntil(() => session.ErrorList.Count > 0, TimeSpan.FromSeconds(5)), "no MediaError");
        output.WriteLine($"missing CA file: {session.Errors}");
        Assert.Contains(session.ErrorList, line => line.Contains("certificate verification failed for 127.0.0.1", StringComparison.Ordinal)
            && line.Contains("SSL_CTX_load_verify_locations", StringComparison.Ordinal));
    }

    [Fact]
    public void Http_IsUnaffectedByTheTlsPolicy()
    {
        if (!TryGetEnvironment(out var ffmpeg, out var media))
            return;

        // A CA file that does not exist would fail any https open; plain http never reads it.
        using var server = new LocalHttpMediaServer(media);
        using var session = PlayerSession.Create(ffmpeg, MediaPlayerMode.Full, tls: new NetworkTlsPolicy { CertificateAuthorityFile = "/nonexistent/fbm-ca.pem" });

        AssertPlays(session, server.UriFor(Mp4));
        Assert.DoesNotContain(session.DiagnosticList, line => line.Contains("certificate", StringComparison.OrdinalIgnoreCase));
    }

    private static void AssertPlays(PlayerSession session, Uri uri)
    {
        Assert.True(session.Player.Load(MediaInputSource.FromUri(uri)), session.Errors);
        Assert.True(session.Player.Play());
        Assert.True(SpinWait.SpinUntil(() => session.Sink.Frames >= 5, TimeSpan.FromSeconds(15)), $"frames {session.Sink.Frames}");
        Assert.True(session.Sink.LastFrameHasContent, "presented frame looks blank");
        Assert.Empty(session.ErrorList);
        _ = session.Player.Stop();
    }

    /// <summary>The request that streams the sample from its start (after the head and index reads).</summary>
    private static bool IsPlaybackRequest(HttpRequestRecord request, long fileLength) =>
        request.Path.EndsWith(Mp4, StringComparison.Ordinal) && request.Number > 1 && (request.RangeStart ?? 0) < fileLength / 10;

    /// <summary>Seek latency: from the Seek call to <c>SeekCompleted</c> (the picture settled at the target).</summary>
    private static double MeasureSeek(PlayerSession session, double target)
    {
        using var completed = new ManualResetEventSlim();
        void OnCompleted(object? sender, MediaSeekCompletedEventArgs e) => completed.Set();
        session.Player.SeekCompleted += OnCompleted;
        try
        {
            var seek = Stopwatch.StartNew();
            Assert.True(session.Player.Seek(target));
            Assert.True(completed.Wait(TimeSpan.FromSeconds(15)), $"seek to {target}: no SeekCompleted, position {session.Player.Position}");
            var elapsed = seek.Elapsed.TotalMilliseconds;
            var frames = session.Sink.Frames;
            Assert.True(SpinWait.SpinUntil(() => session.Sink.Frames > frames + 2, TimeSpan.FromSeconds(10)), "no frames after the seek");
            return elapsed;
        }
        finally
        {
            session.Player.SeekCompleted -= OnCompleted;
        }
    }

    private static bool TryGetEnvironment(out string ffmpeg, out string media)
    {
        ffmpeg = Environment.GetEnvironmentVariable("FATZBOMB_LINUX_FFMPEG_DIR") ?? string.Empty;
        media = Environment.GetEnvironmentVariable("FATZBOMB_LINUX_MEDIA_DIR") is { Length: > 0 } configured ? configured : DefaultMediaDirectory;
        return OperatingSystem.IsLinux() && !string.IsNullOrWhiteSpace(ffmpeg) && File.Exists(Path.Combine(media, Mp4));
    }

    /// <summary>A player with no BASS, the software renderer and a counting frame sink.</summary>
    private sealed class PlayerSession : IDisposable
    {
        private readonly string _appBase;
        private readonly MediaPresentationFrameSinkHandle? _handle;
        private readonly ConcurrentQueue<string> _errors = new();
        private readonly ConcurrentQueue<string> _diagnostics = new();
        private readonly ConcurrentQueue<bool> _buffering = new();

        private PlayerSession(string appBase, MediaControlPlayer player, CountingFrameSink sink, MediaPresentationFrameSinkHandle? handle)
        {
            _appBase = appBase;
            Player = player;
            Sink = sink;
            _handle = handle;
            player.MediaError += (_, args) => _errors.Enqueue(args.Message);
            player.MediaDiagnostic += (_, args) => _diagnostics.Enqueue($"{args.Level} {args.Category}: {args.Message}");
            player.BufferingChanged += (_, args) => _buffering.Enqueue(args.IsBuffering);
        }

        public MediaControlPlayer Player { get; }

        public CountingFrameSink Sink { get; }

        public IReadOnlyList<string> ErrorList => [.. _errors];

        public string Errors => string.Join(" | ", _errors);

        public IReadOnlyList<string> DiagnosticList => [.. _diagnostics];

        public IReadOnlyList<bool> BufferingList => [.. _buffering];

        public static PlayerSession Create(string ffmpeg, MediaPlayerMode mode, NetworkRetryPolicy? retry = null, bool attachSurface = true, NetworkTlsPolicy? tls = null)
        {
            // An empty application folder: the only search path is the FFmpeg folder, so BASS is missing.
            var appBase = Directory.CreateTempSubdirectory("fbm-net-video-").FullName;
            var bootstrapper = new LinuxNativeLibraryBootstrapper(new LinuxNativeOperations(), appBase, LinuxNativeLibraryLocator.CurrentRuntimeIdentifier);
            var factory = new LinuxMediaControlPlatformFactory(new SoftwarePresentationCoordinatorFactory(), LinuxPlatformOptions.Default, bootstrapper);
            var configuration = new MediaPlayerConfiguration(VideoDecodingMethod.Software) { Mode = mode, NetworkRetry = retry, NetworkTls = tls };
            var player = new MediaControlPlayer(factory, configuration, [ffmpeg]);
            var sink = new CountingFrameSink();
            MediaPresentationFrameSinkHandle? handle = null;
            if (attachSurface)
            {
                handle = new MediaPresentationFrameSinkHandle(sink);
                Assert.True(player.AttachPresentationSurface(new MediaPresentationSurfaceDescriptor(
                    MediaPresentationSurfaceDescriptor.PrimarySurfaceId,
                    MediaPresentationSurfaceRole.Primary,
                    handle.CreateSurfaceDescriptor(),
                    640,
                    360)));
            }

            return new PlayerSession(appBase, player, sink, handle);
        }

        public void Dispose()
        {
            if (_handle is not null)
                _ = Player.DetachPresentationSurface(MediaPresentationSurfaceDescriptor.PrimarySurfaceId);
            Player.Dispose();
            _handle?.Dispose();
            Directory.Delete(_appBase, recursive: true);
        }
    }

    private sealed class CountingFrameSink : IMediaPresentationFrameSink
    {
        private int _frames;
        private volatile bool _lastFrameHasContent;

        public int Frames => Volatile.Read(ref _frames);

        public bool LastFrameHasContent => _lastFrameHasContent;

        public void PresentFrame(ReadOnlySpan<byte> bgraPixels, int width, int height, int stride)
        {
            ReadOnlySpan<byte> row = bgraPixels.Slice(height / 2 * stride, width * 4);
            var distinct = new HashSet<byte>();
            foreach (var value in row)
            {
                if (distinct.Add(value) && distinct.Count > 8)
                    break;
            }

            _lastFrameHasContent = distinct.Count > 8;
            _ = Interlocked.Increment(ref _frames);
        }
    }
}
