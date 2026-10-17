using FatzBomb.MediaControl.Core.Abstractions;
using FatzBomb.MediaControl.Core.Audio;
using FatzBomb.MediaControl.Core.Audio.Fade;

namespace FatzBomb.MediaControl.Portable.Tests;

/// <summary>
/// Stage 033A slice 1: video audio gets the equalizer, fade, and normalization stages. These
/// cover the pure parts; the native BASS stages need the Windows smoke in updates/033A_1.
/// </summary>
public sealed class VideoAudioEffectsTests
{
    [Fact]
    public void VideoAudioEngine_ExposesFadeAndFilterStages()
    {
        using var engine = new VideoAudioEngine(new FixedLatencyProvider());

        Assert.IsAssignableFrom<IAudioFadeGainTarget>(engine);
        Assert.IsAssignableFrom<IAudioFilterHost>(engine);
    }

    [Fact]
    public void VideoAudioEngine_WithNothingOpen_HasNoFadeStageAndKeepsEqualizerSettings()
    {
        using var engine = new VideoAudioEngine(new FixedLatencyProvider());
        var filters = (IAudioFilterHost)engine;
        AudioEqualizerSettings settings = AudioEqualizerSettings.CreateGraphic10Band([6, 6, 3, 0, 0, 0, 0, 0, 0, 0]);

        Assert.Equal(0, ((IAudioFadeGainTarget)engine).BeginFadeSession());
        Assert.True(filters.SetEqualizer(settings));
        Assert.Same(settings, filters.Equalizer);
        Assert.True(engine.IsNormalizationReady);
        Assert.Equal(1.0, engine.NormalizationGain);
    }

    [Fact]
    public void FadeStage_WithoutAttachedHandle_RejectsSessions()
    {
        var stage = new BassFadeGainStage("test");

        Assert.Equal(0, stage.BeginFadeSession());
        Assert.False(stage.SetFadeGain(0, 0.5, TimeSpan.Zero));
        Assert.False(stage.SetFadeGain(1, 0.5, TimeSpan.Zero));
    }

    [Fact]
    public void PlanWindows_ShortFile_ReadsEveryHalfSecond()
    {
        IReadOnlyList<double> windows = FfmpegAudioPeakScanner.PlanWindows(3.0, 0.5, 240);

        Assert.Equal([0.0, 0.5, 1.0, 1.5, 2.0, 2.5], windows);
    }

    [Fact]
    public void PlanWindows_LongFile_SpreadsAtMostTheWindowBudgetOverTheWholeDuration()
    {
        const double duration = 2 * 60 * 60;
        IReadOnlyList<double> windows = FfmpegAudioPeakScanner.PlanWindows(duration, 0.5, 240);

        // Same stride as AudioNormalizer.AnalyzePeakBySeek: ceil(7200 / 120) = 60 windows apart.
        Assert.Equal(240, windows.Count);
        Assert.Equal(0, windows[0]);
        Assert.Equal(30.0, windows[1], precision: 9);
        Assert.True(windows[^1] < duration);
        Assert.True(windows[^1] > duration - 60);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void PlanWindows_UnknownDuration_IsEmpty(double duration) =>
        Assert.Empty(FfmpegAudioPeakScanner.PlanWindows(duration, 0.5, 240));

    [Fact]
    public void PeakOf_UsesTheLargestMagnitudeOnEitherSign()
    {
        Assert.Equal(0.75, FfmpegAudioPeakScanner.PeakOf([0.1f, -0.75f, 0.5f, 0.2f]), precision: 6);
        Assert.Equal(0, FfmpegAudioPeakScanner.PeakOf([]));
    }

    [Fact]
    public void PeakOf_ClampsLikeABassLevelReading()
    {
        Assert.Equal(1.0, FfmpegAudioPeakScanner.PeakOf([0.2f, 1.6f]));
        Assert.Equal(0.25, FfmpegAudioPeakScanner.PeakOf([float.NaN, 0.25f]), precision: 6);
    }

    [Fact]
    public void QuickWindow_MatchesTheBassQuickPass() =>
        Assert.Equal(6.0, FfmpegAudioPeakScanner.QuickSeconds);

    private sealed class FixedLatencyProvider : IOutputLatencyProvider
    {
        public double GetOutputLatencySeconds() => 0;
    }
}
