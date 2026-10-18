using FatzBomb.MediaControl.Pipeline;

namespace FatzBomb.MediaControl.Tests;

/// <summary>Stage 041A: the adaptive presentation-lead rules extracted from VideoPipeline.</summary>
public sealed class PresentationLeadControllerTests
{
    private const double Refresh60Ms = 1000.0 / 60;

    [Theory]
    [InlineData(0.75, 0.75)]
    [InlineData(0.1, 0.50)]
    [InlineData(3, 1.25)]
    [InlineData(0, 0.75)]
    [InlineData(double.NaN, 0.75)]
    public void TheInitialLead_IsTheConfiguredValueWithinRange(double configured, double expected) =>
        Assert.Equal(expected, PresentationLeadController.InitialLeadRefreshes(configured));

    [Fact]
    public void TheLead_IsRefreshesCappedByTheFrameAndThirtyMilliseconds()
    {
        // 0.75 of a 60 Hz refresh = 12.5 ms, under 45 % of a 24 fps frame (18.75 ms).
        Assert.Equal(0.0125, PresentationLeadController.LeadSeconds(1 / 60.0, 24, 0.75), 9);
        // 1.25 of a 30 Hz refresh = 41.7 ms: at 24 fps capped at 45 % of a frame (18.75 ms),
        // at 10 fps (45 % = 45 ms) at the 30 ms ceiling.
        Assert.Equal(0.01875, PresentationLeadController.LeadSeconds(1 / 30.0, 24, 1.25), 9);
        Assert.Equal(0.030, PresentationLeadController.LeadSeconds(1 / 30.0, 10, 1.25), 9);
        // 60 fps: 45 % of a frame = 7.5 ms.
        Assert.Equal(0.0075, PresentationLeadController.LeadSeconds(1 / 60.0, 60, 0.75), 9);
        // Unknown refresh: 60 Hz assumed.
        Assert.Equal(0.0125, PresentationLeadController.LeadSeconds(double.NaN, 24, 0.75), 9);
    }

    [Fact]
    public void TheLateThreshold_IsTheLargerOfRefreshAndFrameShares()
    {
        Assert.Equal(0.75 * (1000.0 / 24), PresentationLeadController.ClockLateThresholdMilliseconds(Refresh60Ms, 24), 9);
        Assert.Equal(0.65 * (1000.0 / 30), PresentationLeadController.ClockLateThresholdMilliseconds(1000.0 / 30, 60), 9);
        Assert.Equal(0, PresentationLeadController.ClockLateThresholdMilliseconds(0, 0));
    }

    [Fact]
    public void LatePresents_RaiseTheLeadAStepUpToTheMaximum()
    {
        Assert.Equal(0.875, PresentationLeadController.Adjust(0.75, 8, 5, 0, Refresh60Ms, 24), 9);
        // One present more than twice the 31.25 ms threshold late.
        Assert.Equal(0.875, PresentationLeadController.Adjust(0.75, 1, 63, 0, Refresh60Ms, 24), 9);
        Assert.Equal(1.25, PresentationLeadController.Adjust(1.2, 8, 5, 0, Refresh60Ms, 24), 9);
    }

    [Fact]
    public void ConsistentlyEarlyPresents_LowerTheLeadAHalfStepDownToTheMinimum()
    {
        // No late samples, max late under 0.35 refresh (5.8 ms), max early over 1.75 refreshes (29.2 ms).
        Assert.Equal(0.6875, PresentationLeadController.Adjust(0.75, 0, 2, 30, Refresh60Ms, 24), 9);
        Assert.Equal(0.50, PresentationLeadController.Adjust(0.52, 0, 2, 30, Refresh60Ms, 24), 9);
    }

    [Fact]
    public void AnythingElse_HoldsTheLead()
    {
        Assert.Equal(0.75, PresentationLeadController.Adjust(0.75, 0, 2, 10, Refresh60Ms, 24));
        Assert.Equal(0.75, PresentationLeadController.Adjust(0.75, 1, 10, 30, Refresh60Ms, 24));
    }
}
