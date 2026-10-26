using FatzBomb.MediaControl.Core.Configuration;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace FatzBomb.MediaControl.Linux.Tests;

/// <summary>
/// Stage 039A slice 2: a network outage longer than the read-ahead. The open connection is reset
/// and every request during the outage refused, as with an unplugged cable; once it ends the
/// reader resumes where it stopped and playback carries on to the real end, with no seek from
/// the host. Without BASS nothing stalls the clock while the picture waits (ENG-034), so the
/// picture skips what played meanwhile; the timeline in the output shows it.
/// </summary>
public sealed partial class LinuxNetworkVideoNativeTests
{
    // Longer than what the player holds ahead (the 1 s read-ahead plus decoded frames, about 4 s),
    // and over well before the 10 s sample ends: nothing stops the clock without BASS.
    private static readonly TimeSpan OutageLength = TimeSpan.FromSeconds(5);

    [Fact]
    public void Hls_OutageLongerThanTheBuffer_ResumesOnItsOwnAndPlaysToTheEnd()
    {
        if (!TryGetEnvironment(out var ffmpeg, out var media) || !File.Exists(Path.Combine(media, HlsPlaylist)))
            return;

        AssertResumesAfterOutage(ffmpeg, media, HlsPlaylist);
    }

    [Fact]
    public void ProgressiveMp4_OutageLongerThanTheBuffer_ResumesOnItsOwnAndPlaysToTheEnd()
    {
        if (!TryGetEnvironment(out var ffmpeg, out var media))
            return;

        AssertResumesAfterOutage(ffmpeg, media, Mp4);
    }

    [Fact]
    public void ServerGoneForGood_EndsThePlaybackAfterTheReconnectWindow()
    {
        if (!TryGetEnvironment(out var ffmpeg, out var media) || !File.Exists(Path.Combine(media, HlsPlaylist)))
            return;

        using var server = new LocalHttpMediaServer(media);
        var gone = false;
        server.Plan = _ => new HttpResponsePlan(Refuse: Volatile.Read(ref gone));

        var readAhead = new NetworkReadAheadPolicy { MaxDuration = TimeSpan.FromSeconds(1), ReconnectFor = TimeSpan.FromSeconds(2) };
        using var session = PlayerSession.Create(ffmpeg, MediaPlayerMode.Full, readAhead: readAhead);
        var ended = new ManualResetEventSlim();
        session.Player.MediaEnded += (_, _) => ended.Set();
        Assert.True(session.Player.Load(MediaInputSource.FromUri(server.UriFor(HlsPlaylist))), session.Errors);
        Assert.True(session.Player.Play());
        Assert.True(SpinWait.SpinUntil(() => session.Sink.Frames >= 30, TimeSpan.FromSeconds(15)), $"frames {session.Sink.Frames}");
        Volatile.Write(ref gone, true);
        server.DropConnections();

        Assert.True(ended.Wait(TimeSpan.FromSeconds(20)), $"no MediaEnded; position {session.Player.Position}; diagnostics: {string.Join(" | ", session.DiagnosticList)}");
        Assert.True(session.Player.Position < TimeSpan.FromSeconds(9), $"ended at {session.Player.Position}");
        Assert.Contains(session.DiagnosticList, line => line.Contains("Network read failed", StringComparison.Ordinal));
    }

    [Fact]
    public void Hls_SeekDuringAnOutage_PlaysFromTheTargetOnceTheNetworkIsBack()
    {
        if (!TryGetEnvironment(out var ffmpeg, out var media) || !File.Exists(Path.Combine(media, HlsPlaylist)))
            return;

        using var server = new LocalHttpMediaServer(media);
        var down = false;
        server.Plan = _ => new HttpResponsePlan(Refuse: Volatile.Read(ref down));

        using var session = PlayerSession.Create(ffmpeg, MediaPlayerMode.Full, readAhead: new NetworkReadAheadPolicy { MaxDuration = TimeSpan.FromSeconds(1) });
        var ended = new ManualResetEventSlim();
        session.Player.MediaEnded += (_, _) => ended.Set();
        Assert.True(session.Player.Load(MediaInputSource.FromUri(server.UriFor(HlsPlaylist))), session.Errors);
        Assert.True(session.Player.Play());
        Assert.True(SpinWait.SpinUntil(() => session.Player.Position >= TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(15)), $"position {session.Player.Position}");

        Volatile.Write(ref down, true);
        server.DropConnections();
        _ = session.Player.Seek(6.5);
        Thread.Sleep(TimeSpan.FromSeconds(2));
        var framesBeforeBack = session.Sink.Frames;
        Volatile.Write(ref down, false);

        Assert.True(SpinWait.SpinUntil(() => session.Sink.Frames > framesBeforeBack + 5, TimeSpan.FromSeconds(10)),
            $"no frames after the network came back; diagnostics: {string.Join(" | ", session.DiagnosticList.Where(line => !line.Contains("FFmpeg (", StringComparison.Ordinal)))}");
        Assert.True(ended.Wait(TimeSpan.FromSeconds(15)), $"no MediaEnded; position {session.Player.Position}");
        output.WriteLine($"seek during an outage: diagnostics {string.Join(" | ", session.DiagnosticList.Where(line => line.Contains("Network", StringComparison.Ordinal) && !line.Contains("FFmpeg (", StringComparison.Ordinal)))}");
        Assert.DoesNotContain(session.DiagnosticList, line => line.Contains("Network read failed", StringComparison.Ordinal));
        Assert.Empty(session.ErrorList);
    }

    private void AssertResumesAfterOutage(string ffmpeg, string media, string path)
    {
        using var server = new LocalHttpMediaServer(media);
        var clock = Stopwatch.StartNew();
        long outageStart = long.MaxValue;
        server.Plan = _ =>
        {
            var now = clock.ElapsedTicks;
            var start = Interlocked.Read(ref outageStart);
            // About three times the sample's bitrate, so the download stays a few seconds ahead at most.
            return new HttpResponsePlan(Refuse: now >= start && now < start + OutageLength.Ticks * Stopwatch.Frequency / TimeSpan.TicksPerSecond, BytesPerSecond: 1_500_000);
        };

        // 1 s ahead: the outage outlasts the buffer, so playback must wait, then go on by itself.
        using var session = PlayerSession.Create(ffmpeg, MediaPlayerMode.Full, readAhead: new NetworkReadAheadPolicy { MaxDuration = TimeSpan.FromSeconds(1) });
        var ended = new ManualResetEventSlim();
        var timeline = new ConcurrentQueue<string>();
        session.Player.MediaEnded += (_, _) => { timeline.Enqueue($"{clock.Elapsed.TotalSeconds:F1}s ended"); ended.Set(); };
        session.Player.BufferingChanged += (_, args) => timeline.Enqueue($"{clock.Elapsed.TotalSeconds:F1}s buffering={args.IsBuffering} fill={args.BufferFill:F2} pos={session.Player.Position.TotalSeconds:F1}");
        Assert.True(session.Player.Load(MediaInputSource.FromUri(server.UriFor(path))), session.Errors);
        Assert.True(session.Player.Play());
        Assert.True(SpinWait.SpinUntil(() => session.Player.Position >= TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(15)), $"position {session.Player.Position}");
        _ = Interlocked.Exchange(ref outageStart, clock.ElapsedTicks);
        server.DropConnections();

        Assert.True(ended.Wait(TimeSpan.FromSeconds(30)), $"no MediaEnded; position {session.Player.Position}; diagnostics: {string.Join(" | ", session.DiagnosticList)}");
        var endPosition = session.Player.Position;
        output.WriteLine($"{path}: outage of {OutageLength.TotalSeconds:F0} s from {Interlocked.Read(ref outageStart) / (double)Stopwatch.Frequency:F1}s; ended at {endPosition}; " +
            $"timeline: {string.Join(" | ", timeline)}; diagnostics: {string.Join(" | ", session.DiagnosticList.Where(line => line.Contains("Network", StringComparison.Ordinal) && !line.Contains("FFmpeg (", StringComparison.Ordinal)))}");
        Assert.True(endPosition >= TimeSpan.FromSeconds(9.5), $"ended early at {endPosition}");
        Assert.Contains(true, session.BufferingList);
        Assert.Contains(session.DiagnosticList, line => line.Contains("Network back", StringComparison.Ordinal));
        Assert.DoesNotContain(session.DiagnosticList, line => line.Contains("Network read failed", StringComparison.Ordinal));
        Assert.Empty(session.ErrorList);
    }
}
