using System.IO.Compression;
using FatzAudio;
using FatzAudio.Archives;
using Xunit;

namespace FatzAudio.Tests;

/// <summary>The archive API: list, pick, read, play, tags and fingerprints of ZIP entries.</summary>
public class ArchiveTests
{
    private static readonly StreamOptions DecodeOnly = new() { DecodeOnly = true };

    private static AudioEngine CreateHeadless() => AudioEngine.Create(new AudioEngineOptions { NullOutput = true });

    private static byte[] Zip(params (string Name, byte[] Data, CompressionLevel Level)[] entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, data, level) in entries)
            {
                var e = zip.CreateEntry(name, level);
                using var s = e.Open();
                s.Write(data);
            }
        }
        return ms.ToArray();
    }

    private static byte[] OneSecondCdg() => new byte[24 * 300];

    [Fact]
    public void Open_ListsEntriesFromPathMemoryAndStream()
    {
        byte[] wav = TestAudio.SineWav(0.5);
        byte[] zip = Zip(("Song.wav", wav, CompressionLevel.NoCompression), ("lyrics/words.txt", "la la"u8.ToArray(), CompressionLevel.Optimal),
            ("song.cdg", OneSecondCdg(), CompressionLevel.Optimal));
        string path = Path.Combine(Path.GetTempPath(), $"fa-archive-{Guid.NewGuid():N}.zip");
        File.WriteAllBytes(path, zip);
        try
        {
            using var fromPath = Archive.Open(path);
            using var fromMemory = Archive.Open(zip);
            using var fromStream = Archive.Open(new MemoryStream(zip));
            foreach (var a in new[] { fromPath, fromMemory, fromStream })
            {
                Assert.Equal(ArchiveFormat.Zip, a.Format);
                Assert.Equal(ArchiveFilter.None, a.Filter);
                Assert.Equal(3, a.Entries.Count);
                var song = a.Entries[0];
                Assert.Equal("Song.wav", song.Name);
                Assert.Equal(wav.Length, song.Size);
                Assert.Equal(ArchiveMethod.Stored, song.Method);
                Assert.Equal(ArchiveEntryKind.Audio, song.Kind);
                Assert.True(song.IsReadable);
                Assert.False(song.IsEncrypted);
                Assert.NotNull(song.Crc32);
                Assert.Equal(ArchiveMethod.Deflate, a.Entries[1].Method);
                Assert.Equal(ArchiveEntryKind.Text, a.Entries[1].Kind);
                Assert.Equal(ArchiveEntryKind.Cdg, a.Entries[2].Kind);
                Assert.Equal(a.Entries[1], a.Find("LYRICS\\WORDS.TXT"));
                Assert.Null(a.Find("missing.txt"));
            }
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Pick_FollowsThePairingRules()
    {
        byte[] wav = TestAudio.SineWav(0.25);
        byte[] zip = Zip(("other.wav", wav, CompressionLevel.NoCompression), ("Song.WAV", wav, CompressionLevel.Optimal),
            ("song.cdg", OneSecondCdg(), CompressionLevel.NoCompression), ("__MACOSX/._Song.WAV", [1, 2, 3], CompressionLevel.NoCompression));
        using var a = Archive.Open(zip);
        var (audio, cdg) = a.Pick();
        Assert.Equal("Song.WAV", audio.Name);          // the song with a same-name .cdg comes first
        Assert.Equal("song.cdg", cdg?.Name);

        // Several songs and a .cdg that matches none: it goes with no song.
        using var b = Archive.Open(Zip(("a.wav", wav, CompressionLevel.NoCompression), ("b.wav", wav, CompressionLevel.NoCompression),
            ("x.cdg", OneSecondCdg(), CompressionLevel.NoCompression)));
        var (first, none) = b.Pick();
        Assert.Equal("a.wav", first.Name);
        Assert.Null(none);

        // One song, one .cdg: the pair whatever they are called.
        using var c = Archive.Open(Zip(("Track01.wav", wav, CompressionLevel.NoCompression), ("SC8125-01.cdg", OneSecondCdg(), CompressionLevel.Optimal)));
        var (song, gfx) = c.Pick();
        Assert.Equal("Track01.wav", song.Name);
        Assert.Equal("SC8125-01.cdg", gfx?.Name);

        using var d = Archive.Open(Zip(("readme.txt", "hello"u8.ToArray(), CompressionLevel.NoCompression)));
        Assert.Equal(ErrorCode.NotFound, Assert.Throws<FatzAudioException>(() => d.Pick()).ErrorCode);
    }

    [Theory]
    [InlineData(CompressionLevel.NoCompression)]
    [InlineData(CompressionLevel.Optimal)]
    public void ReadAllBytes_AndOpenRead_GiveTheEntry(CompressionLevel level)
    {
        byte[] wav = TestAudio.SineWav(0.5);
        using var a = Archive.Open(Zip(("song.wav", wav, level)));
        Assert.Equal(wav, a.ReadAllBytes(a.Entries[0]));
        using var s = a.OpenRead(a.Entries[0]);
        Assert.Equal(wav.Length, s.Length);
        var back = new byte[100];
        s.Seek(5000, SeekOrigin.Begin);
        Assert.Equal(100, s.Read(back));
        Assert.Equal(wav.AsSpan(5000, 100).ToArray(), back);
        s.Seek(-10, SeekOrigin.End);
        Assert.Equal(10, s.Read(back));
        Assert.Equal(0, s.Read(back));
        Assert.Equal(wav.Length, s.Position);
    }

    [Fact]
    public void MemoryLimit_AndTempDirectory()
    {
        byte[] wav = TestAudio.SineWav(10);   // 880 KB
        byte[] zip = Zip(("big.wav", wav, CompressionLevel.Optimal));
        using (var a = Archive.Open(zip, new ArchiveOptions { MemoryLimitMb = 0 }))
            Assert.Equal(wav, a.ReadAllBytes(a.Entries[0]));
        string temp = Path.Combine(Path.GetTempPath(), $"fa-archive-tmp-{Guid.NewGuid():N}");
        try
        {
            // Read through the temp folder when the limit is too small: a limit of 0 MB means the default, so use a big entry.
            byte[] big = TestAudio.SineWav(100);   // 8.8 MB, still under 1 MB? no: over the 1 MB limit
            using var b = Archive.Open(Zip(("big.wav", big, CompressionLevel.Optimal)), new ArchiveOptions { MemoryLimitMb = 1 });
            Assert.Equal(ErrorCode.TooLarge, Assert.Throws<FatzAudioException>(() => b.ReadAllBytes(b.Entries[0])).ErrorCode);
            using var c = Archive.Open(Zip(("big.wav", big, CompressionLevel.Optimal)), new ArchiveOptions { MemoryLimitMb = 1, TempDirectory = temp });
            using var s = c.OpenRead(c.Entries[0]);
            s.Seek(4_000_000, SeekOrigin.Begin);
            var chunk = new byte[64];
            Assert.Equal(64, s.Read(chunk));
            Assert.Equal(big.AsSpan(4_000_000, 64).ToArray(), chunk);
        }
        finally
        {
            if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true);
        }
    }

    [Fact]
    public void Cancelled_UnpackStops()
    {
        byte[] wav = TestAudio.SineWav(3);
        using var cts = new CancellationTokenSource();
        using var a = Archive.Open(Zip(("song.wav", wav, CompressionLevel.Optimal)), new ArchiveOptions { CancellationToken = cts.Token });
        cts.Cancel();
        Assert.Equal(ErrorCode.Cancelled, Assert.Throws<FatzAudioException>(() => a.ReadAllBytes(a.Entries[0])).ErrorCode);
    }

    [Fact]
    public void Errors_AreReported()
    {
        Assert.Equal(ErrorCode.NotFound, Assert.Throws<FatzAudioException>(() => Archive.Open("/no/such/archive.zip")).ErrorCode);
        Assert.Equal(ErrorCode.UnsupportedFormat, Assert.Throws<FatzAudioException>(() => Archive.Open(TestAudio.SineWav(0.1))).ErrorCode);
        Assert.Throws<ArgumentException>(() => Archive.Open(ReadOnlyMemory<byte>.Empty));
        Assert.Equal(ArchiveFormat.Rar, Archive.Detect("Rar!\x1A\x07\x00__"u8));
        Assert.Equal(ArchiveFormat.SevenZip, Archive.Detect([0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C, 0, 4]));
        Assert.Equal(ArchiveFormat.None, Archive.Detect(TestAudio.SineWav(0.1)));
        var a = Archive.Open(Zip(("a.wav", TestAudio.SineWav(0.1), CompressionLevel.NoCompression)));
        var entry = a.Entries[0];
        a.Dispose();
        Assert.Throws<ObjectDisposedException>(() => a.ReadAllBytes(entry));
    }

    [Theory]
    [InlineData(CompressionLevel.NoCompression)]
    [InlineData(CompressionLevel.Optimal)]
    public void OpenArchiveEntry_PlaysTheSongWithItsGraphics(CompressionLevel level)
    {
        byte[] wav = TestAudio.SineWav(0.5, sampleRate: 8000);
        byte[] other = TestAudio.SineWav(0.25, sampleRate: 8000);
        byte[] zip = Zip(("one.wav", other, level), ("Two.WAV", wav, level), ("two.cdg", OneSecondCdg(), level));
        using var engine = CreateHeadless();
        using var a = Archive.Open(zip);
        using (var picked = engine.OpenArchiveEntry(a, null, DecodeOnly))
        {
            Assert.True(picked.HasCdg);
            Assert.Equal(4000, TestAudio.ReadAll(picked));
        }
        using (var one = engine.OpenArchiveEntry(a, a.Find("ONE.wav"), DecodeOnly))
        {
            Assert.False(one.HasCdg);              // two songs: the .cdg is not one.wav's
            Assert.Equal(2000, TestAudio.ReadAll(one));
        }
        // The archive may go first.
        var song = engine.OpenArchiveEntry(a, a.Entries[1], DecodeOnly);
        a.Dispose();
        using (song) Assert.Equal(4000, TestAudio.ReadAll(song));

        // The same through the open options of a file, memory and a Stream.
        var named = new StreamOptions { DecodeOnly = true, ArchiveEntry = "one.WAV" };
        using (var m = engine.OpenMemory(zip, named)) Assert.Equal(2000, TestAudio.ReadAll(m));
        using (var s = engine.OpenStream(new MemoryStream(zip), named)) Assert.Equal(2000, TestAudio.ReadAll(s));
        string path = Path.Combine(Path.GetTempPath(), $"fa-archive-{Guid.NewGuid():N}.zip");
        File.WriteAllBytes(path, zip);
        try
        {
            using var f = engine.OpenFile(path, named);
            Assert.Equal(2000, TestAudio.ReadAll(f));
            var ex = Assert.Throws<FatzAudioException>(() => engine.OpenFile(path, new StreamOptions { DecodeOnly = true, ArchiveEntry = "nope.wav" }));
            Assert.Equal(ErrorCode.NotFound, ex.ErrorCode);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Tags_OfEntriesAndStreams()
    {
        byte[] wav = TestAudio.SineWav(0.5, sampleRate: 8000);
        byte[] other = TestAudio.SineWav(0.25, sampleRate: 8000);
        byte[] zip = Zip(("one.wav", other, CompressionLevel.NoCompression), ("two.wav", wav, CompressionLevel.Optimal), ("two.cdg", OneSecondCdg(), CompressionLevel.NoCompression));
        string path = Path.Combine(Path.GetTempPath(), $"fa-archive-{Guid.NewGuid():N}.zip");
        File.WriteAllBytes(path, zip);
        try
        {
            var one = Tags.Read(path, "one.wav");
            Assert.Equal(TimeSpan.FromSeconds(0.25), one.Duration);
            Assert.False(one.HasCdg);
            var picked = Tags.Read(path, null);
            Assert.Equal(TimeSpan.FromSeconds(0.5), picked.Duration);
            Assert.True(picked.HasCdg);
            Assert.Equal(ErrorCode.NotFound, Assert.Throws<FatzAudioException>(() => Tags.Read(path, "x.wav")).ErrorCode);
            using var a = Archive.Open(path);
            Assert.Equal(TimeSpan.FromSeconds(0.25), Tags.Read(a, a.Find("one.wav")).Duration);
            Assert.True(Tags.Read(a).HasCdg);
            // Through a Stream: the same as the file, and the stream stays open.
            using var fs = File.OpenRead(path);
            Assert.True(Tags.Read(fs).HasCdg);
            Assert.True(fs.CanRead);
            using var plain = new MemoryStream(wav);
            Assert.Equal(TimeSpan.FromSeconds(0.5), Tags.Read(plain).Duration);
            Assert.Throws<ArgumentException>(() => Tags.Read(new NonSeekable(wav)));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Fingerprint_OfEntryMatchesItsBytes()
    {
        byte[] wav = TestAudio.SineWav(0.5, sampleRate: 8000);
        var expected = AudioFingerprint.Compute(wav);
        foreach (var level in new[] { CompressionLevel.NoCompression, CompressionLevel.Optimal })
        {
            byte[] zip = Zip(("a.wav", TestAudio.SineWav(0.1), level), ("song.wav", wav, level), ("song.cdg", OneSecondCdg(), level));
            using var a = Archive.Open(zip);
            Assert.Equal(expected, AudioFingerprint.Compute(a, a.Find("song.wav")));
            Assert.Equal(expected, AudioFingerprint.Compute(a));             // the pick
            Assert.Equal(expected, AudioFingerprint.Compute(zip));           // the archive's bytes
        }
    }

    private sealed class NonSeekable(byte[] data) : MemoryStream(data)
    {
        public override bool CanSeek => false;
    }
}
