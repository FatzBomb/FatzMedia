using FatzBomb.MediaControl.Pipeline;

namespace FatzBomb.MediaControl.Tests;

/// <summary>
/// The once-per-open cadence report waits briefly for the renderer's monitor rate instead of
/// naming the platform fallback when the renderer's rate arrives a moment after playback starts.
/// </summary>
public sealed class VideoCadenceReportTimingTests
{
    [Fact]
    public void WaitsForTheRenderer_WhileOnlyThePlatformRateIsKnown()
        => Assert.True(VideoPipeline.ShouldWaitForRendererRefreshRate(
            VideoPipeline.DisplayRefreshSource.Platform, rendererSourceKnown: true, TimeSpan.FromMilliseconds(200)));

    [Fact]
    public void ReportsThePlatformRate_OnceTheWaitRunsOut()
        => Assert.False(VideoPipeline.ShouldWaitForRendererRefreshRate(
            VideoPipeline.DisplayRefreshSource.Platform, rendererSourceKnown: true, VideoPipeline.CadenceReportRendererWait));

    [Fact]
    public void DoesNotWait_WithoutARendererThatCouldReportARate()
        => Assert.False(VideoPipeline.ShouldWaitForRendererRefreshRate(
            VideoPipeline.DisplayRefreshSource.Platform, rendererSourceKnown: false, TimeSpan.Zero));

    [Fact]
    public void DoesNotWait_OnceTheRendererReportsTheRate()
        => Assert.False(VideoPipeline.ShouldWaitForRendererRefreshRate(
            VideoPipeline.DisplayRefreshSource.Renderer, rendererSourceKnown: true, TimeSpan.Zero));

    [Fact]
    public void DoesNotWait_WhenTheHostSetsTheRate()
        => Assert.False(VideoPipeline.ShouldWaitForRendererRefreshRate(
            VideoPipeline.DisplayRefreshSource.Host, rendererSourceKnown: true, TimeSpan.Zero));
}
