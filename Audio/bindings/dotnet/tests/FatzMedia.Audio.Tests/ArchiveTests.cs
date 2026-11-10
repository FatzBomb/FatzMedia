using System.IO.Compression;
using FatzMedia.Audio.Archives;
using Xunit;

namespace FatzMedia.Audio.Tests;

/// <summary>The archive API: list, pick, read, play, tags and fingerprints of ZIP entries.</summary>
public class ArchiveTests
{
    static readonly StreamOptions DecodeOnly = new() { DecodeOnly = true };

    static AudioEngine CreateHeadless() => AudioEngine.Create(new AudioEngineOptions { NullOutput = true, NoCache = true });

    static byte[] Zip(params (string Name, byte[] Data, CompressionLevel Level)[] entries)
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

    static byte[] OneSecondCdg() => new byte[24 * 300];

    [Fact]
    public void Open_ListsEntriesFromPathMemoryAndStream()
    {
        var wav = TestAudio.SineWav(0.5);
        var zip = Zip(("Song.wav", wav, CompressionLevel.NoCompression), ("lyrics/words.txt", "la la"u8.ToArray(), CompressionLevel.Optimal),
            ("song.cdg", OneSecondCdg(), CompressionLevel.Optimal));
        var path = Path.Combine(Path.GetTempPath(), $"fa-archive-{Guid.NewGuid():N}.zip");
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
        var wav = TestAudio.SineWav(0.25);
        var zip = Zip(("other.wav", wav, CompressionLevel.NoCompression), ("Song.WAV", wav, CompressionLevel.Optimal),
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
        Assert.Equal(ErrorCode.NotFound, Assert.Throws<AudioException>(() => d.Pick()).ErrorCode);
    }

    [Theory]
    [InlineData(CompressionLevel.NoCompression)]
    [InlineData(CompressionLevel.Optimal)]
    public void ReadAllBytes_AndOpenRead_GiveTheEntry(CompressionLevel level)
    {
        var wav = TestAudio.SineWav(0.5);
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
        var wav = TestAudio.SineWav(10);   // 880 KB
        var zip = Zip(("big.wav", wav, CompressionLevel.Optimal));
        using (var a = Archive.Open(zip, new ArchiveOptions { MemoryLimitMb = 0 }))
            Assert.Equal(wav, a.ReadAllBytes(a.Entries[0]));
        var temp = Path.Combine(Path.GetTempPath(), $"fa-archive-tmp-{Guid.NewGuid():N}");
        try
        {
            // Read through the temp folder when the limit is too small: a limit of 0 MB means the default, so use a big entry.
            var big = TestAudio.SineWav(100);   // 8.8 MB, still under 1 MB? no: over the 1 MB limit
            using var b = Archive.Open(Zip(("big.wav", big, CompressionLevel.Optimal)), new ArchiveOptions { MemoryLimitMb = 1 });
            Assert.Equal(ErrorCode.TooLarge, Assert.Throws<AudioException>(() => b.ReadAllBytes(b.Entries[0])).ErrorCode);
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
        var wav = TestAudio.SineWav(3);
        using var cts = new CancellationTokenSource();
        using var a = Archive.Open(Zip(("song.wav", wav, CompressionLevel.Optimal)), new ArchiveOptions { CancellationToken = cts.Token });
        cts.Cancel();
        Assert.Equal(ErrorCode.Cancelled, Assert.Throws<AudioException>(() => a.ReadAllBytes(a.Entries[0])).ErrorCode);
    }

    // A solid 7z (LZMA2, packed header) made by the test fixture writer: three songs-folder entries, a text file and an empty file.
    const string SevenZipBase64 =
        "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";

    [Fact]
    public void SevenZip_ListsAndReads()
    {
        var file = Convert.FromBase64String(SevenZipBase64);
        Assert.Equal(ArchiveFormat.SevenZip, Archive.Detect(file));
        using var a = Archive.Open(file);
        Assert.Equal(ArchiveFormat.SevenZip, a.Format);
        Assert.True(a.Entries.Count >= 4);
        var notes = a.Entries.First(e => e.Name == "notes.txt");
        var text = a.ReadAllBytes(notes);
        Assert.Equal(text.Length, notes.Size);
        // Reading an earlier entry after a later one restarts the decoder and still checks out.
        var first = a.Entries.First(e => e.Size > 0 && e.Name != "notes.txt");
        Assert.Equal(first.Size, a.ReadAllBytes(first).Length);
        Assert.Equal(text, a.ReadAllBytes(notes));
    }

    [Fact]
    public void Errors_AreReported()
    {
        Assert.Equal(ErrorCode.NotFound, Assert.Throws<AudioException>(() => Archive.Open("/no/such/archive.zip")).ErrorCode);
        Assert.Equal(ErrorCode.UnsupportedFormat, Assert.Throws<AudioException>(() => Archive.Open(TestAudio.SineWav(0.1))).ErrorCode);
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
        var wav = TestAudio.SineWav(0.5, sampleRate: 8000);
        var other = TestAudio.SineWav(0.25, sampleRate: 8000);
        var zip = Zip(("one.wav", other, level), ("Two.WAV", wav, level), ("two.cdg", OneSecondCdg(), level));
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
        var path = Path.Combine(Path.GetTempPath(), $"fa-archive-{Guid.NewGuid():N}.zip");
        File.WriteAllBytes(path, zip);
        try
        {
            using var f = engine.OpenFile(path, named);
            Assert.Equal(2000, TestAudio.ReadAll(f));
            var ex = Assert.Throws<AudioException>(() => engine.OpenFile(path, new StreamOptions { DecodeOnly = true, ArchiveEntry = "nope.wav" }));
            Assert.Equal(ErrorCode.NotFound, ex.ErrorCode);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Tags_OfEntriesAndStreams()
    {
        var wav = TestAudio.SineWav(0.5, sampleRate: 8000);
        var other = TestAudio.SineWav(0.25, sampleRate: 8000);
        var zip = Zip(("one.wav", other, CompressionLevel.NoCompression), ("two.wav", wav, CompressionLevel.Optimal), ("two.cdg", OneSecondCdg(), CompressionLevel.NoCompression));
        var path = Path.Combine(Path.GetTempPath(), $"fa-archive-{Guid.NewGuid():N}.zip");
        File.WriteAllBytes(path, zip);
        try
        {
            var one = Tags.Read(path, "one.wav");
            Assert.Equal(TimeSpan.FromSeconds(0.25), one.Duration);
            Assert.False(one.HasCdg);
            var picked = Tags.Read(path, null);
            Assert.Equal(TimeSpan.FromSeconds(0.5), picked.Duration);
            Assert.True(picked.HasCdg);
            Assert.Equal(ErrorCode.NotFound, Assert.Throws<AudioException>(() => Tags.Read(path, "x.wav")).ErrorCode);
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
        var wav = TestAudio.SineWav(0.5, sampleRate: 8000);
        var expected = AudioFingerprint.Compute(wav);
        foreach (var level in new[] { CompressionLevel.NoCompression, CompressionLevel.Optimal })
        {
            var zip = Zip(("a.wav", TestAudio.SineWav(0.1), level), ("song.wav", wav, level), ("song.cdg", OneSecondCdg(), level));
            using var a = Archive.Open(zip);
            Assert.Equal(expected, AudioFingerprint.Compute(a, a.Find("song.wav")));
            Assert.Equal(expected, AudioFingerprint.Compute(a));             // the pick
            Assert.Equal(expected, AudioFingerprint.Compute(zip));           // the archive's bytes
        }
    }

    sealed class NonSeekable(byte[] data) : MemoryStream(data)
    {
        public override bool CanSeek => false;
    }
}
