using System.Diagnostics;
using FatzMedia.Video.Core.Configuration;
using FatzMedia.Video.Core.Media;
using FFmpeg.AutoGen;

namespace FatzMedia.Video.Linux.Tests;

/// <summary>
/// Stage 039A slice 2: the network read-ahead. Demuxer-level cases drive a
/// <see cref="SharedDemuxer"/> directly (the caps, the memory it holds, and the sound fed from the
/// picture's download, which needs an audio engine to play); player-level cases cover stalls, seeks inside
/// and outside the buffer, and Pause, Stop and Close while the network hangs. The timings written
/// to the output are the slice's measurements.
/// </summary>
public sealed partial class LinuxNetworkVideoNativeTests
{
    const string BestVariant = "hls_multi/v2/";

    [Fact]
    public void ReadAhead_StopsAtTheDurationCap_AndResumesAsPacketsAreTaken()
    {
        if (!TryGetEnvironment(out var ffmpeg, out var media) || !File.Exists(Path.Combine(media, MultiVariantPlaylist)))
            return;

        using var server = new LocalHttpMediaServer(media);
        server.Plan = SegmentDelay(SegmentRoundTrip);
        var policy = new NetworkReadAheadPolicy { MaxDuration = TimeSpan.FromSeconds(6) };
        using var demuxer = OpenShared(ffmpeg, server.UriFor(MultiVariantPlaylist), policy, withAudio: true, out var video, out _);
        var afterOpen = server.Requests.Count;
        var readAhead = demuxer.ReadAhead!;

        Assert.Equal(1, TakePackets(demuxer, video, 1));
        Assert.True(SpinWait.SpinUntil(() => readAhead.IsReaderPaused, TimeSpan.FromSeconds(20)), "the reader never stopped");
        var buffered = readAhead.BufferedSeconds;
        var segments = Segments(server.Requests.Skip(afterOpen));
        Thread.Sleep(TimeSpan.FromSeconds(1));
        var segmentsAfterWait = Segments(server.Requests.Skip(afterOpen));

        output.WriteLine($"duration cap 6 s: stopped at {buffered:F2} s, {readAhead.BufferedBytes / 1024} KB; segments fetched after the open: {string.Join(", ", segments)}");
        Assert.InRange(buffered, 6.0, 6.5);
        Assert.Equal(segments, segmentsAfterWait);
        Assert.All(segments, path => Assert.StartsWith("/" + BestVariant, path, StringComparison.Ordinal));

        // Taking 3 s of pictures makes room: the reader fetches on and stops at the cap again.
        Assert.Equal(90, TakePackets(demuxer, video, 90));
        Assert.True(SpinWait.SpinUntil(() => Segments(server.Requests.Skip(afterOpen)).Count > segments.Count, TimeSpan.FromSeconds(10)), "the reader did not resume");
        Assert.True(SpinWait.SpinUntil(() => readAhead.IsReaderPaused && readAhead.BufferedSeconds >= 6, TimeSpan.FromSeconds(20)), $"buffered {readAhead.BufferedSeconds:F2} s");
    }

    [Fact]
    public void ReadAhead_StopsAtTheByteCap()
    {
        if (!TryGetEnvironment(out var ffmpeg, out var media) || !File.Exists(Path.Combine(media, MultiVariantPlaylist)))
            return;

        using var server = new LocalHttpMediaServer(media);
        const long Cap = 2L << 20;
        var policy = new NetworkReadAheadPolicy { MaxBytes = Cap };
        using var demuxer = OpenShared(ffmpeg, server.UriFor(MultiVariantPlaylist), policy, withAudio: true, out var video, out _);
        var readAhead = demuxer.ReadAhead!;

        Assert.Equal(1, TakePackets(demuxer, video, 1));
        Assert.True(SpinWait.SpinUntil(() => readAhead.IsReaderPaused, TimeSpan.FromSeconds(20)), "the reader never stopped");

        output.WriteLine($"byte Cap 2 MB: stopped at {readAhead.BufferedBytes / 1024} KB, {readAhead.BufferedSeconds:F2} s");
        // At most one packet past the Cap (the reader checks before each read).
        Assert.InRange(readAhead.BufferedBytes, Cap, Cap + (512 * 1024));
        Assert.InRange(readAhead.BufferedSeconds, 1, 6);
    }

    [Fact]
    public void ReadAhead_DefaultPolicy_MemoryUse()
    {
        if (!TryGetEnvironment(out var ffmpeg, out var media) || !File.Exists(Path.Combine(media, MultiVariantPlaylist)))
            return;

        using var server = new LocalHttpMediaServer(media);
        FfmpegAudioReaderNativeTests.ActivateFfmpeg(ffmpeg);
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var before = process.WorkingSet64;
        using var demuxer = OpenShared(ffmpeg, server.UriFor(MultiVariantPlaylist), NetworkReadAheadPolicy.Default, withAudio: true, out var video, out _);
        var readAhead = demuxer.ReadAhead!;

        Assert.Equal(1, TakePackets(demuxer, video, 1));
        Assert.True(SpinWait.SpinUntil(() => readAhead.IsReaderPaused, TimeSpan.FromSeconds(20)), "the reader never stopped");
        process.Refresh();
        var grown = process.WorkingSet64 - before;

        output.WriteLine($"default policy, 1080p 4.5 Mb/s HLS: {readAhead.BufferedSeconds:F1} s ahead, {readAhead.BufferedBytes / 1024} KB of packets; " +
            $"working set grew {grown / (1024 * 1024)} MB including the open");
        Assert.InRange(readAhead.BufferedSeconds, 20, 21);
        Assert.InRange(readAhead.BufferedBytes, 8L << 20, 16L << 20);
    }

    /// <summary>
    /// The sound of network video comes from the picture's read-ahead (ENG-034): read both streams
    /// to the end, and every segment (HLS) or the file (progressive) is downloaded once.
    /// </summary>
    [Theory]
    [InlineData(MultiVariantPlaylist)]
    [InlineData(Mp4)]
    public void ReadAhead_FeedsPictureAndSoundFromOneDownload(string source)
    {
        if (!TryGetEnvironment(out var ffmpegDirectory, out var media) || !File.Exists(Path.Combine(media, source)))
            return;

        using var server = new LocalHttpMediaServer(media);
        using var demuxer = OpenShared(ffmpegDirectory, server.UriFor(source), NetworkReadAheadPolicy.Default, withAudio: true, out var video, out var audio);
        Assert.True(audio >= 0, "no audio stream");
        var afterOpen = server.Requests.Count;

        int videoPackets = 0, audioPackets = 0;
        bool videoEnded = false, audioEnded = false;
        unsafe
        {
            var packet = ffmpeg.av_packet_alloc();
            try
            {
                var clock = Stopwatch.StartNew();
                while (!(videoEnded && audioEnded) && clock.Elapsed < TimeSpan.FromSeconds(30))
                {
                    if (!videoEnded)
                    {
                        var result = demuxer.ReadVideoPacket(packet);
                        videoEnded = result == ffmpeg.AVERROR_EOF;
                        if (result >= 0)
                        {
                            Assert.Equal(video, packet->stream_index);
                            videoPackets++;
                            ffmpeg.av_packet_unref(packet);
                        }
                    }

                    if (!audioEnded)
                    {
                        var result = demuxer.ReadAudioPacket(packet);
                        audioEnded = result == ffmpeg.AVERROR_EOF;
                        if (result >= 0)
                        {
                            Assert.Equal(audio, packet->stream_index);
                            audioPackets++;
                            ffmpeg.av_packet_unref(packet);
                        }
                    }
                }
            }
            finally
            {
                ffmpeg.av_packet_free(&packet);
            }
        }

        HttpRequestRecord[] requests = [.. server.Requests];
        var duplicates = requests.GroupBy(request => (request.Path, request.RangeStart)).Where(group => group.Count() > 1).Select(group => group.Key.Path).ToArray();
        output.WriteLine($"{source}: {videoPackets} picture and {audioPackets} sound packets from {requests.Length} requests ({requests.Length - afterOpen} after the open); repeated: {(duplicates.Length == 0 ? "none" : string.Join(", ", duplicates))}");
        Assert.True(videoEnded && audioEnded, "a stream did not reach its end");
        var seconds = source == Mp4 ? 10 : 30;
        Assert.InRange(videoPackets, (seconds * 30) - 5, (seconds * 30) + 5);
        Assert.InRange(audioPackets, seconds * 46, (seconds * 47) + 5);
        Assert.Empty(duplicates);
        if (source == Mp4)
            Assert.Equal(afterOpen, requests.Length); // the open's connection streams on; no second one
        else
            Assert.Equal(16, Segments(requests).Count(path => path.StartsWith("/" + BestVariant, StringComparison.Ordinal)));
    }

    /// <summary>A 2 s server stall while about 4 s are buffered: no buffering, no gap in the picture.</summary>
    [Fact]
    public void ReadAhead_ATwoSecondStallWithEnoughBuffered_ShowsNoBufferingAndNoFrameGap()
    {
        if (!TryGetEnvironment(out var ffmpeg, out var media) || !File.Exists(Path.Combine(media, MultiVariantPlaylist)))
            return;

        var stall = TimeSpan.FromSeconds(2);
        var armed = 0;
        var stalledAt = 0L;
        using var server = new LocalHttpMediaServer(media);
        server.Plan = request =>
        {
            if (!request.Path.EndsWith(".ts", StringComparison.Ordinal))
                return new HttpResponsePlan();
            if (Interlocked.CompareExchange(ref armed, 2, 1) != 1)
                return new HttpResponsePlan(DelayBeforeResponse: SegmentRoundTrip);

            Volatile.Write(ref stalledAt, Stopwatch.GetTimestamp());
            return new HttpResponsePlan(DelayBeforeResponse: stall);
        };
        using var session = PlayerSession.Create(ffmpeg, MediaPlayerMode.Full, readAhead: new NetworkReadAheadPolicy { MaxDuration = TimeSpan.FromSeconds(4) });
        Assert.True(session.Player.Load(MediaInputSource.FromUri(server.UriFor(MultiVariantPlaylist))), session.Errors);
        Assert.True(session.Player.Play());
        Assert.True(SpinWait.SpinUntil(() => session.Player.Position > TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(30)), $"position {session.Player.Position}");

        var armedAt = Stopwatch.GetTimestamp();
        Volatile.Write(ref armed, 1);
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref armed) == 2, TimeSpan.FromSeconds(10)), "no segment request after arming");
        var stalledPosition = session.Player.Position;
        Thread.Sleep(stall + TimeSpan.FromSeconds(2));
        var endedAt = Stopwatch.GetTimestamp();

        long[] presented = [.. session.Sink.PresentedAt.Where(time => time >= armedAt && time <= endedAt)];
        var longestGap = presented.Zip(presented.Skip(1), (a, b) => Stopwatch.GetElapsedTime(a, b)).DefaultIfEmpty().Max();
        output.WriteLine($"2 s stall at {stalledPosition.TotalSeconds:F1} s with a 4 s read-ahead: {presented.Length} frames over {Stopwatch.GetElapsedTime(armedAt, endedAt).TotalSeconds:F1} s, " +
            $"longest gap {longestGap.TotalMilliseconds:F0} ms, buffering events [{string.Join(", ", session.BufferingList)}], position now {session.Player.Position.TotalSeconds:F1} s");
        Assert.DoesNotContain(true, session.BufferingList);
        Assert.True(longestGap < TimeSpan.FromMilliseconds(300), $"a {longestGap.TotalMilliseconds:F0} ms gap in the picture");
        Assert.True(Stopwatch.GetElapsedTime(Volatile.Read(ref stalledAt), endedAt) >= stall, "the stall did not happen within the window");
        Assert.Empty(session.ErrorList);
        _ = session.Player.Stop();
    }

    /// <summary>
    /// Seeks inside the buffer make no request; seeks outside refill from the target, fetching its
    /// segment once (one network seek for the preview and the settle).
    /// </summary>
    [Fact]
    public void ReadAhead_SeeksInsideTheBufferMakeNoRequest_SeeksOutsideRefillOnce()
    {
        if (!TryGetEnvironment(out var ffmpeg, out var media) || !File.Exists(Path.Combine(media, MultiVariantPlaylist)))
            return;

        using var server = new LocalHttpMediaServer(media);
        server.Plan = SegmentDelay(SegmentRoundTrip);
        using var session = PlayerSession.Create(ffmpeg, MediaPlayerMode.Full, readAhead: new NetworkReadAheadPolicy { MaxDuration = TimeSpan.FromSeconds(8) });
        Assert.True(session.Player.Load(MediaInputSource.FromUri(server.UriFor(MultiVariantPlaylist))), session.Errors);
        Assert.True(session.Player.Play());
        Assert.True(SpinWait.SpinUntil(() => session.Player.Position > TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30)), $"position {session.Player.Position}");
        // Filled about 8 s ahead: segments up to 8-10 s are in.
        Assert.True(SpinWait.SpinUntil(() => Segments(server.Requests).Contains($"/{BestVariant}seg004.ts"), TimeSpan.FromSeconds(10)), "the read-ahead did not fill");
        Thread.Sleep(TimeSpan.FromMilliseconds(300));

        var before = server.Requests.Count;
        var insideMs = MeasureSeek(session, 6.0);
        var inside = Segments(server.Requests.Skip(before));
        var insidePosition = session.Player.Position;

        before = server.Requests.Count;
        var outsideForwardMs = MeasureSeek(session, 23.0);
        var outsideForward = Segments(server.Requests.Skip(before));

        before = server.Requests.Count;
        var backMs = MeasureSeek(session, 3.0);
        var back = Segments(server.Requests.Skip(before));

        output.WriteLine($"HLS 1080p, 150 ms per segment, 8 s read-ahead: seek inside the buffer (to 6 s) {insideMs:F0} ms, segments after it [{string.Join(", ", inside.Select(SegmentNumber))}]; " +
            $"outside forward (to 23 s) {outsideForwardMs:F0} ms [{string.Join(", ", outsideForward.Select(SegmentNumber))}]; " +
            $"back (to 3 s) {backMs:F0} ms [{string.Join(", ", back.Select(SegmentNumber))}]");
        Assert.True(insidePosition >= TimeSpan.FromSeconds(5.9), $"position after the seek {insidePosition}");
        // Playback goes on fetching ahead, but nothing behind the buffered end is fetched again.
        Assert.DoesNotContain(inside, path => SegmentNumber(path) <= 4);
        // One network seek serves the preview and the settle: the target's segment is fetched once.
        Assert.Equal(1, outsideForward.Count(path => path == $"/{BestVariant}seg011.ts"));
        Assert.Equal(1, back.Count(path => path == $"/{BestVariant}seg001.ts"));
        Assert.Empty(session.ErrorList);
        _ = session.Player.Stop();
    }

    [Fact]
    public void ReadAhead_ProgressiveForwardSeekInsideTheBuffer_MakesNoRequest()
    {
        if (!TryGetEnvironment(out var ffmpeg, out var media))
            return;

        using var server = new LocalHttpMediaServer(media);
        using var session = PlayerSession.Create(ffmpeg, MediaPlayerMode.Full);
        Assert.True(session.Player.Load(MediaInputSource.FromUri(server.UriFor(Mp4))), session.Errors);
        Assert.True(session.Player.Play());
        Assert.True(SpinWait.SpinUntil(() => session.Player.Position > TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(15)), $"position {session.Player.Position}");

        // The whole 10 s clip is within the default read-ahead.
        var requests = server.Requests.Count;
        List<double> inside = [];
        foreach (var target in new[] { 4.0, 6.0, 8.0 })
            inside.Add(MeasureSeek(session, target));
        Assert.Equal(requests, server.Requests.Count);

        var backMs = MeasureSeek(session, 1.0);
        Assert.True(server.Requests.Count > requests, "the seek back made no request");
        output.WriteLine($"progressive MP4: forward seeks inside the buffer {string.Join(", ", inside.Select(ms => $"{ms:F0}"))} ms with no request; " +
            $"back to 1 s (outside, {server.Requests.Count - requests} request) {backMs:F0} ms");
        Assert.Empty(session.ErrorList);
        _ = session.Player.Stop();
    }

    /// <summary>Every segment hangs once the buffer is short: Pause, Stop and Close must not wait for it.</summary>
    [Fact]
    public void ReadAhead_PauseStopAndCloseDuringAStall_ReturnQuickly()
    {
        if (!TryGetEnvironment(out var ffmpeg, out var media) || !File.Exists(Path.Combine(media, MultiVariantPlaylist)))
            return;

        var armed = 0;
        using var server = new LocalHttpMediaServer(media);
        server.Plan = request => !request.Path.EndsWith(".ts", StringComparison.Ordinal)
            ? new HttpResponsePlan()
            : new HttpResponsePlan(DelayBeforeResponse: Volatile.Read(ref armed) == 1 ? TimeSpan.FromSeconds(20) : SegmentRoundTrip);
        using var session = PlayerSession.Create(ffmpeg, MediaPlayerMode.Full, new NetworkRetryPolicy { ConnectTimeout = TimeSpan.FromSeconds(30) }, readAhead: new NetworkReadAheadPolicy { MaxDuration = TimeSpan.FromSeconds(2) });
        Assert.True(session.Player.Load(MediaInputSource.FromUri(server.UriFor(MultiVariantPlaylist))), session.Errors);
        Assert.True(session.Player.Play());
        Assert.True(SpinWait.SpinUntil(() => session.Player.Position > TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30)), $"position {session.Player.Position}");

        Volatile.Write(ref armed, 1);
        Assert.True(SpinWait.SpinUntil(() => session.BufferingList.Contains(true), TimeSpan.FromSeconds(15)), "the buffer never ran dry");
        Thread.Sleep(TimeSpan.FromMilliseconds(500));

        var pause = Stopwatch.StartNew();
        Assert.True(session.Player.Pause());
        var pauseMs = pause.Elapsed.TotalMilliseconds;
        var stop = Stopwatch.StartNew();
        Assert.True(session.Player.Stop());
        var stopMs = stop.Elapsed.TotalMilliseconds;
        Assert.True(session.Player.Play());
        Thread.Sleep(TimeSpan.FromMilliseconds(500));
        var close = Stopwatch.StartNew();
        session.Player.Close();
        var closeMs = close.Elapsed.TotalMilliseconds;

        output.WriteLine($"stalled segments (20 s): Pause {pauseMs:F0} ms, Stop {stopMs:F0} ms, Close after Play {closeMs:F0} ms; buffering events [{string.Join(", ", session.BufferingList)}]");
        Assert.True(pauseMs < 500, $"Pause took {pauseMs:F0} ms");
        Assert.True(stopMs < 1500, $"Stop took {stopMs:F0} ms");
        Assert.True(closeMs < 1500, $"Close took {closeMs:F0} ms");
    }

    /// <summary>A seek outside the buffer waits for a hung segment: Pause and Stop must not wait for it.</summary>
    [Fact]
    public void ReadAhead_PauseAndStopDuringAStalledSeek_ReturnQuickly()
    {
        if (!TryGetEnvironment(out var ffmpeg, out var media) || !File.Exists(Path.Combine(media, MultiVariantPlaylist)))
            return;

        var armed = 0;
        using var server = new LocalHttpMediaServer(media);
        server.Plan = request =>
        {
            if (!request.Path.EndsWith(".ts", StringComparison.Ordinal))
                return new HttpResponsePlan();

            // The segments a seek to 23 s needs hang (seg011 holds 22-24 s).
            return SegmentNumber(request.Path) >= 11 && Volatile.Read(ref armed) >= 1 && Interlocked.Exchange(ref armed, 2) >= 1
                ? new HttpResponsePlan(DelayBeforeResponse: TimeSpan.FromSeconds(20))
                : new HttpResponsePlan(DelayBeforeResponse: SegmentRoundTrip);
        };
        using var session = PlayerSession.Create(ffmpeg, MediaPlayerMode.Full, new NetworkRetryPolicy { ConnectTimeout = TimeSpan.FromSeconds(30) }, readAhead: new NetworkReadAheadPolicy { MaxDuration = TimeSpan.FromSeconds(4) });
        Assert.True(session.Player.Load(MediaInputSource.FromUri(server.UriFor(MultiVariantPlaylist))), session.Errors);
        Assert.True(session.Player.Play());
        Assert.True(SpinWait.SpinUntil(() => session.Player.Position > TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30)), $"position {session.Player.Position}");

        Volatile.Write(ref armed, 1);
        Assert.True(session.Player.Seek(23.0));
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref armed) == 2, TimeSpan.FromSeconds(10)), "the seek made no request");
        Thread.Sleep(TimeSpan.FromMilliseconds(500));

        var pause = Stopwatch.StartNew();
        Assert.True(session.Player.Pause());
        var pauseMs = pause.Elapsed.TotalMilliseconds;
        Assert.True(session.Player.Seek(25.0));
        Thread.Sleep(TimeSpan.FromMilliseconds(500));
        var stop = Stopwatch.StartNew();
        Assert.True(session.Player.Stop());
        var stopMs = stop.Elapsed.TotalMilliseconds;
        var close = Stopwatch.StartNew();
        session.Player.Close();
        var closeMs = close.Elapsed.TotalMilliseconds;

        output.WriteLine($"seek waiting for a hung segment (20 s): Pause {pauseMs:F0} ms, Stop during a second one {stopMs:F0} ms, Close {closeMs:F0} ms");
        Assert.True(pauseMs < 1000, $"Pause took {pauseMs:F0} ms");
        Assert.True(stopMs < 1500, $"Stop took {stopMs:F0} ms");
        Assert.True(closeMs < 1500, $"Close took {closeMs:F0} ms");
    }

    [Fact]
    public void ReadAhead_CloseDuringAFill_ReturnsQuickly()
    {
        if (!TryGetEnvironment(out var ffmpeg, out var media) || !File.Exists(Path.Combine(media, MultiVariantPlaylist)))
            return;

        var hung = 0;
        using var server = new LocalHttpMediaServer(media);
        server.Plan = request =>
        {
            // The open reads the first segments; the read-ahead's fill hangs at 10 s.
            if (!request.Path.EndsWith(".ts", StringComparison.Ordinal) || SegmentNumber(request.Path) < 5)
                return new HttpResponsePlan();

            _ = Interlocked.Exchange(ref hung, 1);
            return new HttpResponsePlan(DelayBeforeResponse: TimeSpan.FromSeconds(20));
        };
        using var session = PlayerSession.Create(ffmpeg, MediaPlayerMode.Full, new NetworkRetryPolicy { ConnectTimeout = TimeSpan.FromSeconds(30) });
        Assert.True(session.Player.Load(MediaInputSource.FromUri(server.UriFor(MultiVariantPlaylist))), session.Errors);
        // Paused after the load: the reader fills on its own and hangs on segment 5.
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref hung) == 1, TimeSpan.FromSeconds(10)), "the reader did not reach the hung segment");
        Thread.Sleep(TimeSpan.FromMilliseconds(300));

        var close = Stopwatch.StartNew();
        session.Player.Close();
        output.WriteLine($"close while the read-ahead waits for a hung segment: {close.Elapsed.TotalMilliseconds:F0} ms");
        Assert.True(close.Elapsed < TimeSpan.FromSeconds(1), $"close took {close.Elapsed}");
    }

    static Func<HttpRequestRecord, HttpResponsePlan> SegmentDelay(TimeSpan delay) =>
        request => request.Path.EndsWith(".ts", StringComparison.Ordinal) ? new HttpResponsePlan(DelayBeforeResponse: delay) : new HttpResponsePlan();

    static List<string> Segments(IEnumerable<HttpRequestRecord> requests) =>
        [.. requests.Where(request => request.Path.EndsWith(".ts", StringComparison.Ordinal)).Select(request => request.Path)];

    static int SegmentNumber(string path) =>
        int.Parse(Path.GetFileNameWithoutExtension(path).AsSpan("seg".Length), System.Globalization.CultureInfo.InvariantCulture);

    static SharedDemuxer OpenShared(string ffmpeg, Uri uri, NetworkReadAheadPolicy policy, bool withAudio, out int video, out int audio)
    {
        FfmpegAudioReaderNativeTests.ActivateFfmpeg(ffmpeg);
        var demuxer = new SharedDemuxer();
        var options = new FfmpegNetworkOptions(NetworkRetryPolicy.Default, NetworkTlsPolicy.Default, policy, CancellationToken.None);
        Assert.True(demuxer.Open(uri.AbsoluteUri, options), demuxer.LastNetworkError);
        video = demuxer.SelectVideoStream();
        demuxer.RegisterVideoStream(video);
        audio = withAudio ? demuxer.SelectAudioStream() : -1;
        if (audio >= 0)
            demuxer.RegisterAudioStream(audio);
        demuxer.DiscardUnregisteredNetworkStreams();
        return demuxer;
    }

    static unsafe int TakePackets(SharedDemuxer demuxer, int stream, int count)
    {
        var packet = ffmpeg.av_packet_alloc();
        try
        {
            var taken = 0;
            while (taken < count && demuxer.ReadVideoPacket(packet) >= 0)
            {
                Assert.Equal(stream, packet->stream_index);
                ffmpeg.av_packet_unref(packet);
                taken++;
            }

            return taken;
        }
        finally
        {
            ffmpeg.av_packet_free(&packet);
        }
    }
}
