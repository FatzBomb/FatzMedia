using FatzBombMedia.MediaControl.Core.Subtitle;
using FatzBombMedia.MediaControl.Pipeline;

namespace FatzBombMedia.MediaControl.Tests;

public class SubtitlePipelineTests
{
    [Fact]
    public void LoadEmbeddedTracks_KeepsEmbeddedTrackSelectedWhenSidecarIsAdded()
    {
        var sidecarPath = Path.GetTempFileName();
        try
        {
            File.WriteAllText(sidecarPath, "1\n00:00:01,000 --> 00:00:04,000\nSidecar\n");
            using var engine = new SubtitleEngine();
            using var pipeline = new SubtitlePipeline(engine, new MediaState());
            var internalCue = new SubtitleCue(1, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(4), "Embedded");

            Assert.True(pipeline.LoadEmbeddedTracks([new EmbeddedSubtitleTrack(2, "ASS", [internalCue])]));
            Assert.True(pipeline.Open(sidecarPath));

            Assert.Equal("embedded:2", pipeline.SelectedTrackId);
            Assert.Equal("Embedded", Assert.Single(engine.GetActiveCues(2)).Text);
            Assert.Equal(2, pipeline.Tracks.Count);
        }
        finally
        {
            File.Delete(sidecarPath);
        }
    }

    [Fact]
    public void SelectTrack_SwitchesSourceAndNullDisablesSubtitles()
    {
        var sidecarPath = Path.GetTempFileName();
        try
        {
            File.WriteAllText(sidecarPath, "1\n00:00:01,000 --> 00:00:04,000\nSidecar\n");
            using var engine = new SubtitleEngine();
            using var pipeline = new SubtitlePipeline(engine, new MediaState());
            var internalCue = new SubtitleCue(1, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(4), "Embedded");
            _ = pipeline.LoadEmbeddedTracks([new EmbeddedSubtitleTrack(2, "ASS", [internalCue])]);
            _ = pipeline.Open(sidecarPath);

            Assert.True(pipeline.SelectTrack("sidecar"));
            Assert.Equal("Sidecar", Assert.Single(engine.GetActiveCues(2)).Text);
            Assert.True(pipeline.SelectTrack(null));
            Assert.Null(pipeline.SelectedTrackId);
            Assert.False(engine.IsOpen);
        }
        finally
        {
            File.Delete(sidecarPath);
        }
    }
}
