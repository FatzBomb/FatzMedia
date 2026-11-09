using FatzMedia.Video.Core.Subtitle;

namespace FatzMedia.Video.Tests;

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

    [Fact]
    public void GetActiveCues_BackwardPositionQueryReturnsEarlierCue()
    {
        using var engine = new SubtitleEngine();
        engine.LoadCues(
        [
            new SubtitleCue(0, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), "Earlier"),
            new SubtitleCue(1, TimeSpan.FromSeconds(50), TimeSpan.FromSeconds(60), "Later"),
        ]);

        Assert.Equal("Later", Assert.Single(engine.GetActiveCues(55)).Text);
        Assert.Equal("Earlier", Assert.Single(engine.GetActiveCues(7)).Text);
    }

    [Fact]
    public void GetActiveCues_FindsLongCuesUnderShorterLaterOnes()
    {
        using var engine = new SubtitleEngine();
        engine.LoadCues(
        [
            new SubtitleCue(0, TimeSpan.FromSeconds(0), TimeSpan.FromSeconds(10), "Long"),
            new SubtitleCue(1, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3), "Short"),
            new SubtitleCue(2, TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(5), "Overlap"),
        ]);

        Assert.Equal(["Long", "Overlap"], engine.GetActiveCues(4.5).Select(static cue => cue.Text));
        Assert.Equal("Long", Assert.Single(engine.GetActiveCues(3.5)).Text);
        Assert.Empty(engine.GetActiveCues(10));
    }
}
