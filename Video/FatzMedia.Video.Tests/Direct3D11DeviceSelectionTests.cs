using FatzMedia.Video.Controllers;
using FatzMedia.Video.Core.Threading;
using FatzMedia.Video.Direct3D11;

namespace FatzMedia.Video.Tests;

/// <summary>
/// Stage 036A: Direct3D11 falls back to WARP, Windows' software rasterizer. WARP ships with
/// Windows, so these run on GPU-less CI machines too.
/// </summary>
public sealed class Direct3D11DeviceSelectionTests
{
    [Fact]
    public void DeviceSelection_DefaultsToAuto()
        => Assert.Equal(Direct3D11DeviceSelection.Auto, new Direct3D11PresentationOptions().DeviceSelection);

    [Fact]
    public void WarpProbe_CreatesAWarpDevice()
    {
        var availability = Probe(Direct3D11DeviceSelection.Warp);

        Assert.True(availability.IsAvailable, availability.Detail);
        Assert.StartsWith("WARP", availability.Detail, StringComparison.Ordinal);
        Assert.True(availability.RequiresSoftwareDecoding);
        Assert.False(availability.RequiresFrameSinkSurface);
    }

    [Fact]
    public void AutoProbe_AlwaysFindsADevice()
    {
        // The GPU when there is one, otherwise WARP.
        var availability = Probe(Direct3D11DeviceSelection.Auto);

        Assert.True(availability.IsAvailable, availability.Detail);
        Assert.Contains("feature level", availability.Detail, StringComparison.Ordinal);
    }

    static MediaPresentationAvailability Probe(Direct3D11DeviceSelection selection)
    {
        IMediaPresentationAvailabilityProbe probe = new Direct3D11PresentationCoordinatorFactory(
            new SystemPrecisionWaiterFactory(),
            new Direct3D11PresentationOptions { DeviceSelection = selection });
        return probe.ProbeAvailability();
    }
}
