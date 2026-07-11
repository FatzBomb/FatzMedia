using FatzBombMedia.MediaControl.Core.Subtitle;

namespace FatzBombMedia.MediaControl.Tests;

public class SubtitleEngineTests
{
    [Fact]
    public void GetActiveCues_ReturnsOverlappingCuesWithinTheirTimeRange()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "1\n00:00:01,000 --> 00:00:04,000\nFirst\n\n2\n00:00:03,000 --> 00:00:05,000\nSecond\n");
            using var engine = new SubtitleEngine();
            engine.Load(path);

            Assert.Equal(["First", "Second"], engine.GetActiveCues(3.5).Select(cue => cue.Text));
            Assert.Empty(engine.GetActiveCues(5.0));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Close_ClearsLoadedCues()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "1\n00:00:01,000 --> 00:00:04,000\nFirst\n");
            using var engine = new SubtitleEngine();
            engine.Load(path);

            engine.Close();

            Assert.False(engine.IsOpen);
            Assert.Empty(engine.GetActiveCues(2));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
