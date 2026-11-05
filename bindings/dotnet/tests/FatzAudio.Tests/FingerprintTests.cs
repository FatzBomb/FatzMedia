using FatzAudio;
using Xunit;

namespace FatzAudio.Tests;

/// <summary>The public audio fingerprint: the same key from a file and its bytes, tags left out.</summary>
public class FingerprintTests
{
    private static string FindTestFile(string relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            string p = Path.Combine(dir.FullName, relative);
            if (File.Exists(p)) return p;
        }
        throw new FileNotFoundException(relative);
    }

    [Fact]
    public void Compute_FileAndBytesAgreeAndTagsAreLeftOut()
    {
        string path = FindTestFile("tests/data/tags/id3v23.mp3");
        byte[] bytes = File.ReadAllBytes(path);
        var fromFile = AudioFingerprint.Compute(path);
        Assert.Equal(fromFile, AudioFingerprint.Compute(bytes));
        Assert.True(fromFile.AudioBytes > 0 && fromFile.AudioBytes < bytes.Length);   // the ID3v2 tag is left out

        // An ID3v1 tag added at the end: the same audio.
        byte[] retagged = new byte[bytes.Length + 128];
        bytes.CopyTo(retagged, 0);
        "TAG"u8.CopyTo(retagged.AsSpan(bytes.Length));
        Assert.Equal(fromFile, AudioFingerprint.Compute(retagged));

        Assert.Matches("^[0-9a-f]{16}-[0-9]+$", fromFile.ToString());
    }

    [Fact]
    public void Compute_ArchiveEntryMatchesItsBytes()
    {
        byte[] bytes = File.ReadAllBytes(FindTestFile("tests/data/tags/id3v23.mp3"));
        string zip = Path.Combine(Path.GetTempPath(), $"fa-fp-{Guid.NewGuid():N}.zip");
        try
        {
            using (var archive = System.IO.Compression.ZipFile.Open(zip, System.IO.Compression.ZipArchiveMode.Create))
            {
                foreach (var level in new[] { System.IO.Compression.CompressionLevel.NoCompression, System.IO.Compression.CompressionLevel.Optimal })
                {
                    var entry = archive.CreateEntry(level == System.IO.Compression.CompressionLevel.Optimal ? "deflated/song.mp3" : "song.mp3", level);
                    using var s = entry.Open();
                    s.Write(bytes);
                }
            }
            var expected = AudioFingerprint.Compute(bytes);
            Assert.Equal(expected, AudioFingerprint.Compute(zip, "song.mp3"));
            Assert.Equal(expected, AudioFingerprint.Compute(zip, "deflated/song.mp3"));
            var ex = Assert.Throws<FatzAudioException>(() => AudioFingerprint.Compute(zip, "other.mp3"));
            Assert.Equal(ErrorCode.NotFound, ex.ErrorCode);
        }
        finally
        {
            File.Delete(zip);
        }
    }

    [Fact]
    public void Compute_Errors()
    {
        var ex = Assert.Throws<FatzAudioException>(() => AudioFingerprint.Compute(Path.Combine(Path.GetTempPath(), "no-such-file.mp3")));
        Assert.Equal(ErrorCode.NotFound, ex.ErrorCode);
        Assert.Throws<ArgumentException>(() => AudioFingerprint.Compute(ReadOnlySpan<byte>.Empty));
    }
}
