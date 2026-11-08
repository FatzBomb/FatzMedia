using System.Text;
using FatzBomb.MediaControl.Core.Abstractions;
using FatzBomb.MediaControl.Core.Files;

namespace FatzBomb.MediaControl.Tests;

public class TempMediaFileTests
{
    [Fact]
    public void CreateFromArchiveEntry_WritesEntryAndReturnsExistingPath()
    {
        var data = Encoding.UTF8.GetBytes("temp-media-payload");

        var path = TempMediaFile.CreateFromArchiveEntry(new Entry(data));
        try
        {
            Assert.True(File.Exists(path));
            Assert.Equal(data, File.ReadAllBytes(path));
            Assert.Equal(".media", Path.GetExtension(path));
        }
        finally
        {
            TempMediaFile.TryDelete(path);
        }
    }

    [Fact]
    public void CreateFromArchiveEntry_DeletesPartialFileWhenCopyFails()
    {
        var before = ScratchFiles();

        _ = Assert.Throws<IOException>(() => TempMediaFile.CreateFromArchiveEntry(new Entry([1, 2, 3], fail: true)));

        Assert.Empty(ScratchFiles().Except(before));
    }

    [Fact]
    public void NewPath_ProducesUniquePathsPerCall()
    {
        var first = TempMediaFile.NewPath(TempMediaFile.MediaExtension);
        var second = TempMediaFile.NewPath(TempMediaFile.MediaExtension);

        Assert.NotEqual(first, second);
        Assert.False(File.Exists(first));
    }

    [Fact]
    public void TryDelete_RemovesExistingFile()
    {
        var path = TempMediaFile.NewPath(TempMediaFile.MediaExtension);
        File.WriteAllBytes(path, [9]);

        TempMediaFile.TryDelete(path);

        Assert.False(File.Exists(path));
    }

    [Fact]
    public void TryDelete_DoesNotThrowForMissingOrEmptyPath()
    {
        var missing = Path.Combine(Path.GetTempPath(), "FatzBombMediaControl", Guid.NewGuid().ToString("N") + ".media");

        // Best-effort contract: cleanup must never surface as an error.
        TempMediaFile.TryDelete(missing);
        TempMediaFile.TryDelete(string.Empty);
    }

    private static string[] ScratchFiles()
    {
        var directory = Path.GetDirectoryName(TempMediaFile.NewPath(TempMediaFile.MediaExtension))!;
        return Directory.GetFiles(directory, "*" + TempMediaFile.MediaExtension);
    }

    private sealed class Entry(byte[] data, bool fail = false) : IArchiveEntryInfo
    {
        public string Key => "entry.mp4";
        public long? Size => data.Length;

        public void WriteTo(Stream stream)
        {
            stream.Write(data);
            if (fail)
                throw new IOException("Simulated archive read failure.");
        }
    }
}
