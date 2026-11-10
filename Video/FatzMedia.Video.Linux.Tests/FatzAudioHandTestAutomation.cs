using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using FatzMedia.Video.Audio;
using FatzMedia.Video.Core.Audio;
using FatzMedia.Video.Core.Configuration;
using Fa = global::FatzMedia.Audio;

namespace FatzMedia.Video.Linux.Tests;

/// <summary>
/// The measurable part of the 037A hand tests (<c>docs/roadmap/updates/037A_5/HandTests.md</c>), run by
/// <c>scripts/smoke-037A.ps1</c>: video audio through FFmpeg into FatzAudio (the soundtrack path), its clock and
/// what it actually played, recorded and searched for clicks. Opt-in, because it takes about two minutes of real
/// time: set <c>FATZMEDIA_HANDTESTS=1</c>. <c>FATZMEDIA_HANDTESTS_OUT</c> names a JSON-lines file for the results;
/// <c>FATZMEDIA_HANDTESTS_DEVICE=real</c> plays on the default output device (you hear test tones) instead of
/// FatzAudio's null device. Needs FFmpeg as <see cref="FatzAudioVideoAudioNativeTests"/> does.
/// </summary>
[Collection(FatzAudioNativeGroup.Name)]
[Trait("Category", "HandTests")]
public sealed class FatzAudioHandTestAutomation : IDisposable
{
    const double ToneHz = 441;      // 100 samples a period at 44.1 kHz: a 1 s file loops without a seam
    const double ToneAmplitude = 0.4;

    readonly string _root = Directory.CreateTempSubdirectory("fbm-handtests-").FullName;
    FatzAudioEngine? _player;
    VideoAudioEngine? _audio;
    CancellationTokenSource? _pump;
    Task? _pumpTask;

    static bool Enabled => Environment.GetEnvironmentVariable("FATZMEDIA_HANDTESTS") == "1" && FatzAudioNativeGroup.CanRun;

    static bool RealDevice => string.Equals(Environment.GetEnvironmentVariable("FATZMEDIA_HANDTESTS_DEVICE"), "real", StringComparison.OrdinalIgnoreCase);

    public void Dispose()
    {
        try
        {
            _pump?.Cancel();
            _pumpTask?.Wait();
        }
        finally
        {
            _pump?.Dispose();
            _audio?.Dispose();
            _player?.Dispose();
            try { Directory.Delete(_root, recursive: true); } catch { }
        }
    }

    [Theory]
    [InlineData(-25)]
    [InlineData(0)]
    [InlineData(25)]
    [InlineData(50)]
    public void A1_SyncAtTempo(int percent)
    {
        var audio = Open(30);
        if (audio is null)
            return;

        var rate = 1 + (percent / 100.0);
        audio.SetTempo(percent);
        Assert.True(audio.Play());
        Thread.Sleep(500);
        // The clock against the wall clock for 12 s: how far it drifts from tempo x time, and how far single
        // readings stray from the straight line (what a video frame would be shown against).
        List<(double Wall, double Position)> samples = [];
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(12))
        {
            samples.Add((watch.Elapsed.TotalSeconds, audio.Position));
            Thread.Sleep(10);
        }

        var (slope, intercept) = Fit(samples);
        var driftMs = (samples[^1].Position - samples[0].Position - (rate * (samples[^1].Wall - samples[0].Wall))) * 1000;
        var jitterMs = samples.Max(s => Math.Abs(s.Position - (intercept + (slope * s.Wall)))) * 1000;
        var backwards = samples.Zip(samples.Skip(1)).Count(static pair => pair.Second.Position < pair.First.Position - 0.0005);
        var pass = Math.Abs(driftMs) < 30 && jitterMs < 15 && backwards == 0;
        Report($"A1 sync at tempo {rate:0.00}", pass,
            $"drift {driftMs:+0.0;-0.0} ms over 12 s (limit 30), largest jitter {jitterMs:0.0} ms (limit 15), went back {backwards} times",
            new() { ["tempo"] = rate, ["driftMs"] = driftMs, ["jitterMs"] = jitterMs, ["backwards"] = backwards });
        Assert.True(pass);
    }

    [Fact]
    public void A2_Seeks_LandOnTheTarget_AndDoNotClick()
    {
        var audio = Open(60);
        if (audio is null)
            return;

        using var recording = Record();
        Assert.True(audio.Play());
        Thread.Sleep(500);
        var random = new Random(7);
        List<string> problems = [];
        var worstLandingMs = 0.0;
        for (var i = 0; i < 20; i++)
        {
            var paused = i >= 15;
            if (paused)
                Assert.True(audio.Pause());
            var target = Math.Round(2 + (random.NextDouble() * 50), 3);
            Assert.True(audio.Seek(target, resumePlayback: !paused));
            var landing = (audio.Position - target) * 1000;
            worstLandingMs = Math.Max(worstLandingMs, Math.Abs(landing));
            if (paused)
                Assert.True(audio.Play());
            Thread.Sleep(400);
            var moved = audio.Position - target;
            if (Math.Abs(landing) > 50 || moved is < 0.2 or > 0.6)
                problems.Add($"seek {i + 1} to {target:0.000}: landed {landing:+0;-0} ms off, then moved {moved:0.000} s in 0.4 s");
        }

        var clicks = recording.StopAndCountClicks();
        var pass = problems.Count == 0 && clicks.Count == 0;
        Report("A2 seeks (15 playing, 5 paused)", pass,
            $"worst landing {worstLandingMs:0} ms (limit 50); clicks heard: {clicks.Count}{Describe(clicks)}" + (problems.Count > 0 ? "; " + string.Join("; ", problems) : ""),
            new() { ["worstLandingMs"] = worstLandingMs, ["clicks"] = clicks.Count });
        Assert.True(pass);
    }

    [Fact]
    public void A3_Loops_WrapGaplessly_WithoutClicks()
    {
        var audio = Open(1.0);
        if (audio is null)
            return;

        audio.LoopPlayback = true;
        using var recording = Record();
        Assert.True(audio.Play());
        var watch = Stopwatch.StartNew();
        var outOfRange = 0;
        while (watch.Elapsed < TimeSpan.FromSeconds(5.5))
        {
            if (audio.Position is < 0 or > 1.0)
                outOfRange++;
            Thread.Sleep(10);
        }

        var wraps = audio.CompletedLoopCount;
        var clicks = recording.StopAndCountClicks();
        var gaps = recording.Gaps;
        var pass = wraps is >= 4 and <= 6 && outOfRange == 0 && clicks.Count == 0 && gaps == 0;
        Report("A3 loop wraps (1 s file, 5.5 s)", pass,
            $"{wraps} wraps (expected 5), position out of range {outOfRange} times, clicks {clicks.Count}{Describe(clicks)}, silent gaps {gaps}",
            new() { ["wraps"] = wraps, ["clicks"] = clicks.Count, ["gaps"] = gaps });
        Assert.True(pass);
    }

    [Fact]
    public void A4_PauseResume_HoldsTheClock_WithoutClicks()
    {
        var audio = Open(30);
        if (audio is null)
            return;

        using var recording = Record();
        Assert.True(audio.Play());
        var random = new Random(3);
        var worstHoldMs = 0.0;
        var stuck = 0;
        for (var i = 0; i < 10; i++)
        {
            Thread.Sleep(300 + random.Next(400));
            Assert.True(audio.Pause());
            Thread.Sleep(80);
            var held = audio.Position;
            Thread.Sleep(250);
            worstHoldMs = Math.Max(worstHoldMs, Math.Abs(audio.Position - held) * 1000);
            Assert.True(audio.Play());
            Thread.Sleep(300);
            if (audio.Position - held < 0.15)
                stuck++;
        }

        var clicks = recording.StopAndCountClicks();
        var pass = worstHoldMs < 2 && stuck == 0 && clicks.Count == 0;
        Report("A4 pause/resume (10 times)", pass,
            $"clock moved at most {worstHoldMs:0.0} ms while paused (limit 2), did not resume {stuck} times, clicks {clicks.Count}{Describe(clicks)}",
            new() { ["worstHoldMs"] = worstHoldMs, ["clicks"] = clicks.Count });
        Assert.True(pass);
    }

    [Fact]
    public void A5_SteadyMinute_NoClicksOrDropouts()
    {
        var audio = Open(65);
        if (audio is null)
            return;

        using var recording = Record();
        Assert.True(audio.Play());
        Thread.Sleep(TimeSpan.FromSeconds(60));
        var clicks = recording.StopAndCountClicks();
        var gaps = recording.Gaps;
        var pass = clicks.Count == 0 && gaps == 0;
        Report("A5 one steady minute", pass,
            $"clicks {clicks.Count}{Describe(clicks)}, silent gaps {gaps} (the picture's load on two screens is the hand test)",
            new() { ["clicks"] = clicks.Count, ["gaps"] = gaps });
        Assert.True(pass);
    }

    VideoAudioEngine? Open(double seconds)
    {
        if (!Enabled || FatzAudioVideoAudioNativeTests.FfmpegDirectory() is not { } ffmpeg)
            return null;

        FatzAudioVideoAudioNativeTests.ActivateFfmpeg(ffmpeg);
        var wav = FatzAudioTestMedia.WriteWav(_root, $"tone-{Guid.NewGuid():N}.wav", new FatzAudioTestMedia.Section(seconds, ToneHz, ToneAmplitude));
        _player = (FatzAudioEngine)new FatzAudioEngineFactory(nullOutput: !RealDevice).Create(new MediaPlayerConfiguration
        {
            Normalization = new NormalizationOptions { CacheDirectory = string.Empty },
        });
        _player.Normalize = false; // no gain glide: the recording is the tone as pushed
        var audio = _audio = new VideoAudioEngine(((IPushAudioOutputSource)_player).CreatePushAudioOutput(), null);
        Assert.True(audio.Open(wav));
        Assert.True(audio.PrebufferAudio());
        _pump = new CancellationTokenSource();
        var token = _pump.Token;
        _pumpTask = Task.Run(() =>
        {
            while (!token.IsCancellationRequested)
            {
                if (!audio.DecodeAndPushAudio())
                    Thread.Sleep(5);
            }
        });
        return audio;
    }

    OutputRecording Record() => new(_player!.OutputDevice);

    static string Describe(List<double> clicks) =>
        clicks.Count == 0 ? "" : " at " + string.Join(", ", clicks.Take(8).Select(static t => t.ToString("0.000 s", CultureInfo.InvariantCulture)));

    static (double Slope, double Intercept) Fit(List<(double X, double Y)> points)
    {
        var meanX = points.Average(static p => p.X);
        var meanY = points.Average(static p => p.Y);
        var slope = points.Sum(p => (p.X - meanX) * (p.Y - meanY)) / points.Sum(p => (p.X - meanX) * (p.X - meanX));
        return (slope, meanY - (slope * meanX));
    }

    static void Report(string name, bool pass, string detail, Dictionary<string, object> metrics)
    {
        var line = JsonSerializer.Serialize(new { name, pass, detail, metrics, device = RealDevice ? "default output" : "null output" });
        if (Environment.GetEnvironmentVariable("FATZMEDIA_HANDTESTS_OUT") is { Length: > 0 } path)
            File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8); // the collection runs one test at a time
    }

    /// <summary>
    /// What the output device played, recorded as 32-bit float WAV, and searched for clicks: for the test tone, a
    /// jump in the second difference of more than ten times what a sine of that pitch and level can make, or a
    /// run of digital silence inside the sound (a dropout).
    /// </summary>
    sealed class OutputRecording : IDisposable
    {
        readonly MemoryStream _wav = new();
        readonly Fa.AudioEncoder _encoder;
        bool _stopped;

        public OutputRecording(Fa.OutputDevice device)
        {
            _encoder = Fa.AudioEncoder.Create(_wav, new Fa.EncoderOptions { BitsPerSample = 32 }, leaveOpen: true);
            _encoder.Record(device);
        }

        /// <summary>Silent runs of 5 ms or more between the first and the last sound.</summary>
        public int Gaps { get; private set; }

        /// <summary>Stops and returns the time of each click, in seconds into the recording.</summary>
        public List<double> StopAndCountClicks()
        {
            _stopped = true;
            _encoder.Close();
            var bytes = _wav.ToArray();
            if (Environment.GetEnvironmentVariable("FATZMEDIA_HANDTESTS_DUMP") is { Length: > 0 } dump) File.WriteAllBytes(dump, bytes);
            var (samples, rate, channels) = ReadWav(bytes);
            var left = new float[samples.Length / channels];
            for (var i = 0; i < left.Length; i++)
                left[i] = samples[i * channels];

            var step = 2 * Math.PI * ToneHz / rate;
            var limit = Math.Max(0.02, 10 * ToneAmplitude * step * step);
            List<double> clicks = [];
            var lastClick = -rate;
            for (var i = 2; i < left.Length; i++)
            {
                var d2 = left[i] - (2 * left[i - 1]) + left[i - 2];
                if (Math.Abs(d2) > limit && i - lastClick > rate / 100)
                {
                    clicks.Add(i / (double)rate);
                    lastClick = i;
                }
            }

            // Pauses are silent on purpose: a gap is silence between two sounds that is shorter than 60 ms (a
            // pause in these tests lasts 250 ms or more).
            var first = Array.FindIndex(left, static v => Math.Abs(v) > 0.01);
            var last = Array.FindLastIndex(left, static v => Math.Abs(v) > 0.01);
            var run = 0;
            for (var i = Math.Max(0, first); i <= last && first >= 0; i++)
            {
                if (Math.Abs(left[i]) < 1e-6)
                {
                    run++;
                    continue;
                }

                if (run >= rate / 200 && run < rate * 0.06)
                    Gaps++;
                run = 0;
            }

            return clicks;
        }

        public void Dispose()
        {
            if (!_stopped)
                _encoder.Dispose();
            _wav.Dispose();
        }

        // A WAV may carry other chunks (JUNK) before "fmt ": walk them.
        static (float[] Samples, int Rate, int Channels) ReadWav(byte[] bytes)
        {
            int channels = 0, rate = 0;
            var offset = 12;
            while (offset + 8 <= bytes.Length)
            {
                var id = Encoding.ASCII.GetString(bytes, offset, 4);
                var size = BitConverter.ToInt32(bytes, offset + 4);
                if (id == "fmt ")
                {
                    channels = BitConverter.ToInt16(bytes, offset + 10);
                    rate = BitConverter.ToInt32(bytes, offset + 12);
                }
                else if (id == "data")
                {
                    var count = Math.Min(size, bytes.Length - offset - 8) / sizeof(float);
                    var samples = new float[count];
                    Buffer.BlockCopy(bytes, offset + 8, samples, 0, count * sizeof(float));
                    return (samples, rate, Math.Max(1, channels));
                }

                offset += 8 + size + (size & 1);
            }

            return ([], rate, Math.Max(1, channels));
        }
    }
}
