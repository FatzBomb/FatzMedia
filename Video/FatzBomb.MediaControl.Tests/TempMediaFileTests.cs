using System.Text;
using FatzBomb.MediaControl.Core.Files;

namespace FatzBomb.MediaControl.Tests;

public class TempMediaFileTests
{
    [Fact]
    public void Create_WritesDataAndReturnsExistingPath()
    {
        var data = Encoding.UTF8.GetBytes("temp-media-payload");

        var path = TempMediaFile.Create(data);
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
    public void Create_ProducesUniquePathsPerCall()
    {
        var first = TempMediaFile.Create([1, 2, 3]);
        var second = TempMediaFile.Create([4, 5, 6]);
        try
        {
            Assert.NotEqual(first, second);
        }
        finally
        {
            TempMediaFile.TryDelete(first);
            TempMediaFile.TryDelete(second);
        }
    }

    [Fact]
    public void TryDelete_RemovesExistingFile()
    {
        var path = TempMediaFile.Create([9]);

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
}
