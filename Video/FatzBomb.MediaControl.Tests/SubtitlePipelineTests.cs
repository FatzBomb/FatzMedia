using FatzBomb.MediaControl.Core.Abstractions;
using FatzBomb.MediaControl.Core.Subtitle;
using FatzBomb.MediaControl.Pipeline;

namespace FatzBomb.MediaControl.Tests;

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

    [Fact]
    public void ContentFlag_FollowsTheSelectedTrack()
    {
        // ENG-022: the flag was set when a track was loaded and never revoked.
        using var engine = new SubtitleEngine();
        var state = new MediaState();
        using var pipeline = new SubtitlePipeline(engine, state);
        var cue = new SubtitleCue(1, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(4), "Embedded");

        // Loaded but not selected (MIDI lyrics hidden): tracks are listed, nothing is showing.
        Assert.True(pipeline.LoadEmbeddedTracks([new EmbeddedSubtitleTrack(0, "MIDI Lyrics", [cue])], selectFirstTrack: false));
        Assert.Single(pipeline.Tracks);
        Assert.False(state.Content.HasFlag(MediaContent.Subtitle));

        Assert.True(pipeline.SelectTrack("embedded:0"));
        Assert.True(state.Content.HasFlag(MediaContent.Subtitle));

        Assert.True(pipeline.SelectTrack(null));
        Assert.False(state.Content.HasFlag(MediaContent.Subtitle));
        Assert.Single(pipeline.Tracks);

        Assert.True(pipeline.SelectTrack("embedded:0"));
        pipeline.Close();
        Assert.False(state.Content.HasFlag(MediaContent.Subtitle));
    }
}
