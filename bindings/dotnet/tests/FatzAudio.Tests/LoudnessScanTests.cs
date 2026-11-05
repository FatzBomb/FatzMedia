using FatzAudio;
using Xunit;

namespace FatzAudio.Tests;

/// <summary>Loudness scans of audio the app feeds (<see cref="LoudnessScan"/>) and streams that name their song.</summary>
public class LoudnessScanTests
{
    private const int Rate = 44100;

    private static AudioEngine Create(string? cache) =>
        AudioEngine.Create(new AudioEngineOptions { NullOutput = true, CacheDirectory = cache });

    private static string TempDir() => Path.Combine(Path.GetTempPath(), $"fa-feed-{Guid.NewGuid():N}");

    private static void FeedPackets(LoudnessScan scan, byte[] wav, int from = 0, int to = -1)
    {
        // 16-bit mono PCM after the 44-byte header, in 8 KB packets.
        int end = to < 0 ? wav.Length : to;
        for (int at = 44 + from; at < end; at += 8192)
            Assert.True(scan.PushPacket(wav.AsSpan(at, Math.Min(8192, end - at))));
    }

    private static CodecParameters Pcm16 => new("pcm_s16le", Rate, 1);

    [Fact]
    public void FedScan_MatchesScanByPath_AndSharesItsCacheEntry()
    {
        string dir = TempDir();
        string path = Path.Combine(Path.GetTempPath(), $"fa-feed-{Guid.NewGuid():N}.wav");
        byte[] wav = TestAudio.SineWav(seconds: 20);
        File.WriteAllBytes(path, wav);
        try
        {
            using var engine = Create(dir);
            var id = AudioFingerprint.Compute(path);
            using (var scan = engine.OpenLoudnessScan(Pcm16, id, new LoudnessScanOptions { Duration = TimeSpan.FromSeconds(20) }))
            {
                Assert.True(scan.WantsMore);
                FeedPackets(scan, wav);
                var fed = scan.Finish();
                Assert.Equal(LoudnessSource.Scan, fed.Source);
                Assert.True(fed.IsFinal);
                Assert.InRange(fed.IntegratedLufs, -10.0, -9.4);
                Assert.Equal(fed, scan.Finish());            // again: the same result
            }

            // The file's own scan finds the fed result in the cache: one entry for both.
            var byPath = engine.ScanLoudness(path);
            Assert.Equal(LoudnessSource.Cache, byPath.Source);

            // A second feed has nothing to do.
            using var again = engine.OpenLoudnessScan(Pcm16, id);
            Assert.False(again.WantsMore);
            Assert.Equal(LoudnessSource.Cache, again.Finish().Source);

            using var plain = Create(null);
            var measured = plain.ScanLoudness(path);
            Assert.Equal(measured.IntegratedLufs, byPath.IntegratedLufs, 1);
        }
        finally
        {
            File.Delete(path);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task StreamNamingItsSong_GetsTheFedResult_FromAScanThatStartsAfterIt()
    {
        string dir = TempDir();
        byte[] wav = TestAudio.SineWav(seconds: 20);
        var id = AudioFingerprint.Compute(wav);
        try
        {
            using var engine = Create(dir);
            engine.SetNormalization(NormalizeMode.Track, -23.0);
            using var stream = engine.CreatePushStream(AudioFormat.Int16(Rate, 1), new StreamOptions { SourceIdentity = id });
            var finals = 0;
            stream.LoudnessFinal += (_, _) => Interlocked.Increment(ref finals);
            Assert.False(stream.GetLoudness().IsFinal);
            await Task.Delay(100);                                    // its analysis has looked the song up: not cached, no feed yet

            using var scan = engine.OpenLoudnessScan(Pcm16, id, new LoudnessScanOptions { Duration = TimeSpan.FromSeconds(20) });
            FeedPackets(scan, wav, to: 44 + Rate * 2 * 10);          // half the song
            Assert.False(stream.GetLoudness().IsFinal);
            FeedPackets(scan, wav, from: Rate * 2 * 10);
            var fed = scan.Finish();

            for (int i = 0; i < 400 && !stream.GetLoudness().IsFinal; i++) await Task.Delay(10);
            var l = stream.GetLoudness();
            Assert.True(l.IsFinal);
            Assert.Equal(LoudnessSource.Scan, l.Source);
            Assert.Equal(fed.IntegratedLufs, l.IntegratedLufs, 6);
            Assert.Equal(-23.0 - fed.IntegratedLufs, l.GainDb, 2);
            for (int i = 0; i < 200 && Volatile.Read(ref finals) == 0; i++) await Task.Delay(10);
            Assert.Equal(1, Volatile.Read(ref finals));

            // A stream opened afterwards starts final, from the cache.
            using var late = engine.CreatePushStream(AudioFormat.Int16(Rate, 1), new StreamOptions { SourceIdentity = id });
            for (int i = 0; i < 400 && !late.GetLoudness().IsFinal; i++) await Task.Delay(10);
            var cached = late.GetLoudness();
            Assert.True(cached.IsFinal);
            Assert.Equal(LoudnessSource.Cache, cached.Source);

            // Another track of the same file is another song.
            using var other = engine.CreatePushStream(AudioFormat.Int16(Rate, 1), new StreamOptions { SourceIdentity = id, AudioTrack = 1 });
            Assert.False(other.GetLoudness().IsFinal);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public unsafe void FrameScan_TakesDecodedFramesAndChecksUsage()
    {
        using var engine = Create(null);
        var id = new AudioFingerprint(1, 2);
        using var scan = engine.OpenLoudnessScan(AudioFormat.Float32(Rate, 1), id);
        var frame = new float[4096];
        for (int i = 0; i < frame.Length; i++) frame[i] = (float)(0.5 * Math.Sin(2 * Math.PI * 440 * i / Rate));
        fixed (float* p = frame)
        {
            void* planes = p;
            nint planeArray = (nint)(&planes);
            for (int i = 0; i < 20; i++) scan.PushFrame(AudioFormat.Float32(Rate, 1), planeArray, frame.Length);
            Assert.Throws<InvalidOperationException>(() => scan.PushPacket(new byte[16]));
            var ex = Assert.Throws<FatzAudioException>(() => scan.PushFrame(AudioFormat.Float32(48000, 1), planeArray, frame.Length));
            Assert.Equal(ErrorCode.UnsupportedFormat, ex.ErrorCode);
        }
        var l = scan.Finish();
        Assert.Equal(LoudnessSource.Scan, l.Source);
        Assert.InRange(l.IntegratedLufs, -12.0, -8.0);
    }

    [Fact]
    public void Open_RejectsUnknownCodec_AndNothingFedEndsTheStream()
    {
        using var engine = Create(null);
        var id = new AudioFingerprint(3, 4);
        var ex = Assert.Throws<FatzAudioException>(() => engine.OpenLoudnessScan(new CodecParameters("not_a_codec", Rate, 1), id));
        Assert.Equal(ErrorCode.UnsupportedFormat, ex.ErrorCode);
        using var scan = engine.OpenLoudnessScan(Pcm16, id);
        Assert.Equal(ErrorCode.EndOfStream, Assert.Throws<FatzAudioException>(() => scan.Finish()).ErrorCode);
    }
}
