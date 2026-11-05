using FatzAudio;
using Xunit;

namespace FatzAudio.Tests;

/// <summary>Loudness normalization, scans and the limiter, on the silent null output.</summary>
public class NormalizationTests
{
    private static AudioEngine CreateHeadless(string? cache = null) =>
        AudioEngine.Create(new AudioEngineOptions { NullOutput = true, CacheDirectory = cache });

    private static async Task<Loudness> WaitFinalAsync(AudioStream s)
    {
        for (int i = 0; i < 400; i++)
        {
            var l = s.GetLoudness();
            if (l.IsFinal) return l;
            await Task.Delay(10);
        }
        throw new TimeoutException("no final loudness");
    }

    [Fact]
    public void ScanLoudness_MeasuresSine()
    {
        // TestAudio.SineWav: mono 440 Hz at half scale (-6.02 dBFS peak) measures about -9.7 LUFS.
        string path = Path.Combine(Path.GetTempPath(), $"fa-scan-{Guid.NewGuid():N}.wav");
        File.WriteAllBytes(path, TestAudio.SineWav(seconds: 30));
        try
        {
            using var engine = CreateHeadless();
            var l = engine.ScanLoudness(path);
            Assert.True(l.IsFinal);
            Assert.Equal(LoudnessSource.Scan, l.Source);
            Assert.InRange(l.IntegratedLufs, -10.0, -9.4);
            Assert.InRange(l.TruePeakDbtp, -6.5, -5.5);
            Assert.Equal(0.0, l.GainDb); // normalization off
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ScanLoudness_UsesCacheOnSecondScan()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"fa-cache-{Guid.NewGuid():N}");
        string path = Path.Combine(Path.GetTempPath(), $"fa-scan-{Guid.NewGuid():N}.wav");
        File.WriteAllBytes(path, TestAudio.SineWav(seconds: 5));
        try
        {
            using var engine = CreateHeadless(dir);
            var first = engine.ScanLoudness(path);
            var second = engine.ScanLoudness(path);
            Assert.Equal(LoudnessSource.Scan, first.Source);
            Assert.Equal(LoudnessSource.Cache, second.Source);
            Assert.Equal(first.IntegratedLufs, second.IntegratedLufs, 3);
        }
        finally
        {
            File.Delete(path);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void ScanLoudness_MissingFile_ThrowsNotFound()
    {
        using var engine = CreateHeadless();
        var ex = Assert.Throws<FatzAudioException>(() => engine.ScanLoudness("no-such-file.wav"));
        Assert.Equal(ErrorCode.NotFound, ex.ErrorCode);
    }

    [Fact]
    public async Task Stream_GetsGainForTarget_AndRaisesLoudnessFinal()
    {
        using var engine = CreateHeadless();
        var final = new TaskCompletionSource();
        // Subscribe before analysis can start: with normalization off, opening
        // starts nothing; the first GetLoudness starts the scan.
        using var s = engine.OpenMemory(TestAudio.SineWav(seconds: 10));
        s.LoudnessFinal += (_, _) => final.TrySetResult();
        engine.SetNormalization(NormalizeMode.Track, -23);
        var l = await WaitFinalAsync(s);
        Assert.Equal(LoudnessSource.Scan, l.Source);
        Assert.Equal(-23 - l.IntegratedLufs, l.GainDb, 3);
        await final.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task SetLoudness_WinsAndOptOutGetsNoGain()
    {
        using var engine = CreateHeadless();
        engine.SetNormalization(NormalizeMode.Track, -16);
        using var s = engine.OpenMemory(TestAudio.SineWav(seconds: 5));
        s.SetLoudness(-10, 4, -1);
        var l = s.GetLoudness();
        Assert.Equal(LoudnessSource.App, l.Source);
        Assert.Equal(-6.0, l.GainDb, 3);
        Assert.Equal(4.0, l.RangeLu, 3);

        using var jingle = engine.OpenMemory(TestAudio.SineWav(seconds: 5), new StreamOptions { NoNormalize = true });
        var j = await WaitFinalAsync(jingle);
        Assert.Equal(0.0, j.GainDb);

        Assert.Throws<ArgumentException>(() => s.SetLoudness(double.NaN));
    }

    [Fact]
    public void Settings_AreValidated()
    {
        using var engine = CreateHeadless();
        Assert.Equal(ErrorCode.InvalidArgument,
            Assert.Throws<FatzAudioException>(() => engine.SetNormalization(NormalizeMode.Track, 3)).ErrorCode);
        Assert.Equal(ErrorCode.InvalidArgument,
            Assert.Throws<FatzAudioException>(() => engine.SetLimiter(LimiterMode.On, 2)).ErrorCode);
        engine.SetLimiter(LimiterMode.On, -1);
        engine.SetLimiter(LimiterMode.Auto);
        engine.SetNormalization(NormalizeMode.Off);
    }

    [Fact]
    public void Cache_SizeAndClear()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"fa-lc-{Guid.NewGuid():N}");
        string path = Path.Combine(Path.GetTempPath(), $"fa-lc-{Guid.NewGuid():N}.wav");
        File.WriteAllBytes(path, TestAudio.SineWav(seconds: 30));
        try
        {
            using (var engine = AudioEngine.Create(new AudioEngineOptions { NullOutput = true, CacheDirectory = dir, CacheMaxBytes = 64 * 1024 }))
            {
                Assert.Equal(LoudnessSource.Scan, engine.ScanLoudness(path).Source);
                Assert.Equal(LoudnessSource.Cache, engine.ScanLoudness(path).Source);
                Assert.True(File.Exists(Path.Combine(dir, "loudness.fac")));
                engine.ClearCache();
                Assert.False(File.Exists(Path.Combine(dir, "loudness.fac")));
                Assert.Equal(LoudnessSource.Scan, engine.ScanLoudness(path).Source);
                Assert.Throws<ArgumentOutOfRangeException>(() => engine.SetCacheSize(-1));
                Assert.Equal(ErrorCode.InvalidArgument,
                    Assert.Throws<FatzAudioException>(() => engine.SetCacheSize(1000)).ErrorCode);
                engine.SetCacheSize(0);
            }
            using (var again = AudioEngine.Create(new AudioEngineOptions { NullOutput = true, CacheDirectory = dir }))
                Assert.Equal(LoudnessSource.Cache, again.ScanLoudness(path).Source);   // a new run reads the file
        }
        finally
        {
            File.Delete(path);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void StartWait_IsValidated()
    {
        using var engine = CreateHeadless();
        engine.SetNormalizationStartWait(TimeSpan.FromMilliseconds(250), 6);
        engine.SetNormalizationStartWait(TimeSpan.Zero);
        Assert.Throws<ArgumentOutOfRangeException>(() => engine.SetNormalizationStartWait(TimeSpan.FromSeconds(11)));
        Assert.Throws<ArgumentOutOfRangeException>(() => engine.SetNormalizationStartWait(TimeSpan.FromMilliseconds(-1)));
        Assert.Equal(ErrorCode.InvalidArgument,
            Assert.Throws<FatzAudioException>(() => engine.SetNormalizationStartWait(TimeSpan.Zero, 13)).ErrorCode);
        Assert.Equal(ErrorCode.InvalidArgument,
            Assert.Throws<FatzAudioException>(() => engine.SetNormalizationStartWait(TimeSpan.Zero, -1)).ErrorCode);
    }

    [Fact]
    public async Task StartWait_FirstPlayStartsOnTheFinalValue()
    {
        // A short song is measured long before a 5 s wait runs out: it starts on the final value, so
        // no provisional event is raised, and the clock then runs.
        using var engine = CreateHeadless();
        engine.SetNormalization(NormalizeMode.Track, -16);
        engine.SetNormalizationStartWait(TimeSpan.FromSeconds(5));
        using var output = engine.OpenOutput();
        using var s = engine.OpenMemory(TestAudio.SineWav(seconds: 10));
        int provisional = 0;
        s.LoudnessProvisional += (_, _) => Interlocked.Increment(ref provisional);
        s.PlayOn(output);
        for (int i = 0; i < 500 && s.Clock < TimeSpan.FromMilliseconds(100); i++) await Task.Delay(10);
        Assert.True(s.Clock >= TimeSpan.FromMilliseconds(100));
        Assert.True(s.GetLoudness().IsFinal);
        Assert.Equal(0, Volatile.Read(ref provisional));
    }

    [Fact]
    public async Task StreamWithoutScan_RaisesLoudnessProvisional()
    {
        // App I/O is never scanned, so it never waits and is heard on the running estimate (or nothing yet).
        using var engine = CreateHeadless();
        engine.SetNormalization(NormalizeMode.Track, -16);
        engine.SetNormalizationStartWait(TimeSpan.FromSeconds(5), maxProvisionalBoostDb: 0);
        using var output = engine.OpenOutput();
        using var s = engine.OpenStream(new MemoryStream(TestAudio.SineWav(seconds: 5)));
        var got = new TaskCompletionSource<LoudnessSource>();
        s.LoudnessProvisional += (_, e) => got.TrySetResult(e.Source);
        s.PlayOn(output);
        var source = await got.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(source is LoudnessSource.Estimate or LoudnessSource.None);
        Assert.True(s.GetLoudness().GainDb <= 0.0001);   // only cuts while provisional
    }

    [Fact]
    public async Task NormalizedStream_PlaysToTheEnd()
    {
        using var engine = CreateHeadless();
        engine.SetNormalization(NormalizeMode.Track, -16);
        using var output = engine.OpenOutput();
        using var s = engine.OpenMemory(TestAudio.SineWav(seconds: 0.5));
        var ended = new TaskCompletionSource();
        s.Ended += (_, _) => ended.TrySetResult();
        s.PlayOn(output);
        await ended.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }
}
