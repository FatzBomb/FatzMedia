using FatzMedia.Video.Pipeline;

namespace FatzMedia.Video.Tests;

public sealed class AudioPipelineAnalysisTests
{
    [Fact]
    public void PlainAudioOpenEnablesSilenceAnalysis()
    {
        var engine = new TestAudioEngine();
        using var pipeline = new AudioPipeline(engine);

        Assert.True(pipeline.Open("song.mp3"));
        Assert.True(engine.LastAnalyzeSilence);
    }

    [Fact]
    public void SynchronizedAudioOpenCanDisableSilenceAnalysis()
    {
        var engine = new TestAudioEngine();
        using var pipeline = new AudioPipeline(engine);

        Assert.True(pipeline.Open("song.mp3", ownState: false, analyzeSilence: false));
        Assert.False(engine.LastAnalyzeSilence);
    }
}
