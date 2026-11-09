using FatzMedia.Video.Core.Configuration;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace FatzMedia.Video.Linux.Tests;

/// <summary>
/// ENG-035: seeks on a slow multi-variant HLS source must never block the property reads a host's
/// UI timer makes (the owner's Avalonia smoke froze on the second seek). A "UI" thread reads
/// <c>State</c>, <c>Position</c>, <c>Duration</c> and <c>IsOpen</c> every 200 ms, as the test
/// players do, resizes the surface and sets the sync offset (which redraws the frame) every second,
/// and records the longest call. If a read or a seek hangs, the watchdog writes a
/// managed stack dump of every thread (<c>dotnet-stack report</c>, when installed) to the output.
/// No BASS is loaded, so the audio cursor (<c>VideoAudioEngine</c>) is not exercised here.
/// </summary>
public sealed partial class LinuxNetworkVideoNativeTests
{
    // A UI timer that blocks longer than this drops frames visibly.
    private static readonly TimeSpan GetterBudget = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan SeekSettleTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task HlsMultiVariant_RapidAndSpacedSeeks_KeepTheGettersResponsive()
    {
        if (!TryGetEnvironment(out var ffmpeg, out var media) || !File.Exists(Path.Combine(media, MultiVariantPlaylist)))
            return;

        using var server = new LocalHttpMediaServer(media);
        // 150-1000 ms per segment, varied but repeatable, as a remote server's round trip.
        server.Plan = request => request.Path.EndsWith(".ts", StringComparison.Ordinal)
            ? new HttpResponsePlan(DelayBeforeResponse: TimeSpan.FromMilliseconds(150 + (request.Number * 7919 % 851)))
            : new HttpResponsePlan();
        using var session = PlayerSession.Create(ffmpeg, MediaPlayerMode.Full);
        await StartAsync(session, server);

        using var poller = GetterPoller.Start(session.Player, output);
        var settles = new List<double>();

        // Rapid: a burst of requests (a drag released several times); only the last must settle.
        settles.Add(await SeekBurstAsync(session, [20.0, 6.0, 26.0]));
        // Spaced: forward and back, each settling before the next.
        foreach (var target in new[] { 10.0, 28.0, 2.0, 16.0 })
        {
            settles.Add(await SeekBurstAsync(session, [target]));
            await Task.Delay(TimeSpan.FromMilliseconds(600));
        }

        poller.Stop();
        output.WriteLine($"HLS multi-variant seeks (150-1000 ms per segment): settled in {string.Join(", ", settles.Select(ms => $"{ms:F0}"))} ms; {poller.Describe()}");
        Assert.True(poller.Longest < GetterBudget, $"a getter blocked the UI thread: {poller.Describe()}");
        var frames = session.Sink.Frames;
        Assert.True(SpinWait.SpinUntil(() => session.Sink.Frames > frames + 2, TimeSpan.FromSeconds(10)), "no frames after the seeks");
        Assert.Empty(session.ErrorList);
    }

    [Fact]
    public async Task HlsMultiVariant_SeekDuringAStalledSegment_IsSupersededAndClosesPromptly()
    {
        if (!TryGetEnvironment(out var ffmpeg, out var media) || !File.Exists(Path.Combine(media, MultiVariantPlaylist)))
            return;

        var stall = TimeSpan.FromSeconds(10);
        var armed = 0;
        using var server = new LocalHttpMediaServer(media);
        server.Plan = request =>
        {
            if (!request.Path.EndsWith(".ts", StringComparison.Ordinal))
                return new HttpResponsePlan();

            // The first segment the seek to 24 s asks for hangs (seg012 holds 24-26 s).
            return request.Path.EndsWith("seg012.ts", StringComparison.Ordinal) && Interlocked.CompareExchange(ref armed, 2, 1) == 1
                ? new HttpResponsePlan(DelayBeforeResponse: stall)
                : new HttpResponsePlan(DelayBeforeResponse: SegmentRoundTrip);
        };
        using var session = PlayerSession.Create(ffmpeg, MediaPlayerMode.Full);
        await StartAsync(session, server);

        using var poller = GetterPoller.Start(session.Player, output);
        Volatile.Write(ref armed, 1);
        var clock = Stopwatch.StartNew();
        Assert.True(await session.Player.SeekAsync(24.0).WaitAsync(TimeSpan.FromSeconds(5)), "SeekAsync(24) was refused");
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref armed) == 2, TimeSpan.FromSeconds(10)), "the seek never reached the stalled segment");
        // Long enough for the poller's once-a-second calls to run during the stall.
        await Task.Delay(TimeSpan.FromMilliseconds(1500));

        // A newer seek while the segment hangs: it must not wait for the stall to end.
        var superseding = Stopwatch.StartNew();
        var settledMs = await SeekBurstAsync(session, [8.0]);
        var supersededAfter = clock.Elapsed;
        output.WriteLine($"stalled segment ({stall.TotalSeconds:F0} s): the newer seek settled in {settledMs:F0} ms ({supersededAfter.TotalMilliseconds:F0} ms after the stalled seek); {poller.Describe()}");
        Assert.True(superseding.Elapsed < stall - TimeSpan.FromSeconds(2), $"the newer seek waited for the stalled segment ({superseding.Elapsed})");

        // Again, and this time close while the segment hangs.
        Volatile.Write(ref armed, 1);
        Assert.True(await session.Player.SeekAsync(24.0).WaitAsync(TimeSpan.FromSeconds(5)), "SeekAsync(24) was refused");
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref armed) == 2, TimeSpan.FromSeconds(10)), "the second seek never reached the stalled segment");
        await Task.Delay(TimeSpan.FromMilliseconds(500));
        var close = Stopwatch.StartNew();
        await Task.Run(session.Player.Close).WaitAsync(TimeSpan.FromSeconds(10));
        var closeMs = close.Elapsed.TotalMilliseconds;
        poller.Stop();
        output.WriteLine($"close during the stalled seek: {closeMs:F0} ms; {poller.Describe()}");
        Assert.True(close.Elapsed < TimeSpan.FromSeconds(3), $"close took {close.Elapsed}");
        Assert.True(poller.Longest < GetterBudget, $"a getter blocked the UI thread: {poller.Describe()}");
    }

    /// <summary>
    /// A progressive MP4 (byte ranges): a newer seek gives up the stalled range request of the
    /// older one, and the stream stays usable afterwards (a superseded read is not taken for a
    /// dropped connection).
    /// </summary>
    [Fact]
    public async Task ProgressiveMp4_SeekDuringAStalledRangeRequest_IsSupersededAndPlaysOn()
    {
        if (!TryGetEnvironment(out var ffmpeg, out var media))
            return;

        var armed = 0;
        using var server = new LocalHttpMediaServer(media);
        server.Plan = request => request.RangeStart is > 0 && Interlocked.CompareExchange(ref armed, 2, 1) == 1
            ? new HttpResponsePlan(DelayBeforeResponse: TimeSpan.FromSeconds(10))
            : new HttpResponsePlan();
        using var session = PlayerSession.Create(ffmpeg, MediaPlayerMode.Full);
        Assert.True(await session.Player.LoadAsync(MediaInputSource.FromUri(server.UriFor(Mp4))), session.Errors);
        Assert.True(await session.Player.PlayAsync());
        Assert.True(SpinWait.SpinUntil(() => session.Player.Position > TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(15)), $"position {session.Player.Position}");
        _ = await SeekBurstAsync(session, [7.0]);

        using var poller = GetterPoller.Start(session.Player, output);
        // Back to 1 s needs a new range request, which hangs.
        Volatile.Write(ref armed, 1);
        Assert.True(await session.Player.SeekAsync(1.0).WaitAsync(TimeSpan.FromSeconds(5)), "SeekAsync(1) was refused");
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref armed) == 2, TimeSpan.FromSeconds(10)), "the seek made no range request");
        await Task.Delay(TimeSpan.FromMilliseconds(1500));

        var superseding = Stopwatch.StartNew();
        var settledMs = await SeekBurstAsync(session, [4.0]);
        var frames = session.Sink.Frames;
        Assert.True(SpinWait.SpinUntil(() => session.Sink.Frames > frames + 10 && session.Player.Position > TimeSpan.FromSeconds(4.3), TimeSpan.FromSeconds(10)),
            $"no playback after the superseding seek: frames {session.Sink.Frames - frames}, position {session.Player.Position}");
        poller.Stop();
        output.WriteLine($"stalled range request (10 s): the newer seek settled in {settledMs:F0} ms, playing on at {session.Player.Position.TotalSeconds:F1} s; {poller.Describe()}");
        Assert.True(superseding.Elapsed < TimeSpan.FromSeconds(8), $"the newer seek waited for the stalled request ({superseding.Elapsed})");
        Assert.True(poller.Longest < GetterBudget, $"a getter blocked the UI thread: {poller.Describe()}");
        Assert.DoesNotContain(session.DiagnosticList, line => line.Contains("Network read failed", StringComparison.Ordinal));
        Assert.Empty(session.ErrorList);
    }

    private static async Task StartAsync(PlayerSession session, LocalHttpMediaServer server)
    {
        Assert.True(await session.Player.LoadAsync(MediaInputSource.FromUri(server.UriFor(MultiVariantPlaylist))), session.Errors);
        Assert.True(await session.Player.PlayAsync());
        Assert.True(SpinWait.SpinUntil(() => session.Player.Position > TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30)), $"position {session.Player.Position}");
    }

    /// <summary>
    /// Requests each target through <c>SeekAsync</c> (the UI's call) back to back; every request
    /// must be accepted, and the last must settle (<c>SeekCompleted</c> for it). Returns the time
    /// from the last request to its settle.
    /// </summary>
    private async Task<double> SeekBurstAsync(PlayerSession session, IReadOnlyList<double> targets)
    {
        var settled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var last = targets[^1];
        void OnCompleted(object? sender, MediaSeekCompletedEventArgs e)
        {
            if (Math.Abs(e.RequestedPosition.TotalSeconds - last) < 0.01)
                _ = settled.TrySetResult(true);
        }

        session.Player.SeekCompleted += OnCompleted;
        try
        {
            List<Task<bool>> requests = [];
            var clock = Stopwatch.StartNew();
            foreach (var target in targets)
            {
                clock.Restart();
                requests.Add(session.Player.SeekAsync(target));
            }

            if (await Task.WhenAny(Task.WhenAll(requests), Task.Delay(SeekSettleTimeout)) is not Task<bool[]> accepted)
            {
                DumpStacks("a SeekAsync task did not finish");
                Assert.Fail($"SeekAsync did not finish within {SeekSettleTimeout}");
                return 0;
            }

            Assert.All(await accepted, Assert.True);
            if (await Task.WhenAny(settled.Task, Task.Delay(SeekSettleTimeout)) != settled.Task)
            {
                DumpStacks($"the seek to {last} s did not settle");
                Assert.Fail($"the seek to {last} s did not settle within {SeekSettleTimeout}; position {session.Player.Position}");
            }

            return clock.Elapsed.TotalMilliseconds;
        }
        finally
        {
            session.Player.SeekCompleted -= OnCompleted;
        }
    }

    /// <summary>Writes every managed thread's stack to the test output, through <c>dotnet-stack</c> when it is installed.</summary>
    private void DumpStacks(string reason) => GetterPoller.DumpStacks(output, reason);

    /// <summary>
    /// A "UI thread" that reads the player's state every 200 ms, as the test players' timers do,
    /// and records the longest read; a watchdog dumps the stacks once if a read hangs for 2 s.
    /// </summary>
    private sealed class GetterPoller : IDisposable
    {
        private readonly MediaControlPlayer _player;
        private readonly Xunit.Abstractions.ITestOutputHelper _output;
        private readonly Thread _thread;
        private readonly Thread _watchdog;
        private readonly CancellationTokenSource _stop = new();
        private readonly ConcurrentDictionary<string, long> _longestTicks = new(StringComparer.Ordinal);
        private long _readStarted;
        private int _reads;
        private bool _taller;

        private GetterPoller(MediaControlPlayer player, Xunit.Abstractions.ITestOutputHelper output)
        {
            _player = player;
            _output = output;
            _thread = new Thread(Run) { IsBackground = true, Name = "Test UI poller" };
            _watchdog = new Thread(Watch) { IsBackground = true, Name = "Test UI watchdog" };
        }

        public TimeSpan Longest => TimeSpan.FromTicks(_longestTicks.Values.DefaultIfEmpty(0).Max() * TimeSpan.TicksPerSecond / Stopwatch.Frequency);

        public static GetterPoller Start(MediaControlPlayer player, Xunit.Abstractions.ITestOutputHelper output)
        {
            var poller = new GetterPoller(player, output);
            poller._thread.Start();
            poller._watchdog.Start();
            return poller;
        }

        public string Describe() =>
            $"{Volatile.Read(ref _reads)} UI reads, longest {Longest.TotalMilliseconds:F1} ms ("
            + string.Join(", ", _longestTicks.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key} {pair.Value * 1000.0 / Stopwatch.Frequency:F1}"))
            + ")";

        public void Stop()
        {
            _stop.Cancel();
            _ = _thread.Join(TimeSpan.FromSeconds(30));
            _ = _watchdog.Join(TimeSpan.FromSeconds(5));
        }

        public void Dispose()
        {
            Stop();
            _stop.Dispose();
        }

        public static void DumpStacks(Xunit.Abstractions.ITestOutputHelper output, string reason)
        {
            var tool = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet", "tools", "dotnet-stack");
            if (!File.Exists(tool))
            {
                output.WriteLine($"{reason}; install dotnet-stack (dotnet tool install -g dotnet-stack) for a stack dump");
                return;
            }

            try
            {
                using var process = Process.Start(new ProcessStartInfo(tool, $"report -p {Environment.ProcessId}")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false
                })!;
                Task<string> stdout = process.StandardOutput.ReadToEndAsync();
                if (!process.WaitForExit(TimeSpan.FromSeconds(30)))
                    process.Kill();
                output.WriteLine($"{reason}; stacks:{Environment.NewLine}{stdout.Result}");
            }
            catch (Exception ex)
            {
                output.WriteLine($"{reason}; dotnet-stack failed: {ex.Message}");
            }
        }

        private void Run()
        {
            while (!_stop.IsCancellationRequested)
            {
                Measure("State", () => _ = _player.State);
                Measure("Position", () => _ = _player.Position);
                Measure("Duration", () => _ = _player.Duration);
                Measure("IsOpen", () => _ = _player.IsOpen);
                if (Volatile.Read(ref _reads) % 5 == 0)
                {
                    _taller = !_taller;
                    Measure("Resize", () => _ = _player.ResizePresentationSurface(MediaPresentationSurfaceDescriptor.PrimarySurfaceId, 640, _taller ? 368 : 360));
                    // A sync setter that redraws the current frame (as the sync offset keys do).
                    Measure("SyncOffset", () => _player.AudioSyncOffset = TimeSpan.Zero);
                }

                _ = Interlocked.Increment(ref _reads);
                _ = _stop.Token.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(200));
            }
        }

        private void Measure(string name, Action read)
        {
            var started = Stopwatch.GetTimestamp();
            Volatile.Write(ref _readStarted, started);
            read();
            var elapsed = Stopwatch.GetTimestamp() - started;
            Volatile.Write(ref _readStarted, 0);
            _ = _longestTicks.AddOrUpdate(name, elapsed, (_, previous) => Math.Max(previous, elapsed));
        }

        private void Watch()
        {
            var dumped = false;
            while (!_stop.Token.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(100)))
            {
                var started = Volatile.Read(ref _readStarted);
                if (!dumped && started != 0 && Stopwatch.GetElapsedTime(started) > TimeSpan.FromSeconds(2))
                {
                    dumped = true;
                    DumpStacks(_output, "a UI read has been blocked for 2 s");
                }
            }
        }
    }
}
