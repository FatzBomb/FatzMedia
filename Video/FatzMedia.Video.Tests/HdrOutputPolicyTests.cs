using FatzMedia.Video.Core.Graphics;
using FatzMedia.Video.Core.Video.Hdr;

namespace FatzMedia.Video.Tests;

/// <summary>Stage 042A slice 2 groundwork: the HDR output decision and its pure math.</summary>
public sealed class HdrOutputPolicyTests
{
    static readonly HdrDisplayInfo HdrMonitor = new(HdrEnabled: true, PeakNits: 600, SdrWhiteNits: 200);
    static readonly HdrDisplayInfo SdrMonitor = new(HdrEnabled: false, PeakNits: 270, SdrWhiteNits: 0);

    [Theory]
    [InlineData(false, HdrTransferFunction.Pq, true, false, "HDR output is off")]
    [InlineData(true, null, true, false, "the video is SDR")]
    [InlineData(true, HdrTransferFunction.Pq, false, false, "the monitor is not in HDR mode")]
    [InlineData(true, HdrTransferFunction.Hlg, false, false, "the monitor is not in HDR mode")]
    [InlineData(true, HdrTransferFunction.Pq, true, true, "the monitor is in HDR mode (peak 600 nits, SDR white 200 nits)")]
    [InlineData(true, HdrTransferFunction.Hlg, true, true, "the monitor is in HDR mode (peak 600 nits, SDR white 200 nits)")]
    public void TheDecision_IsHdrOnlyForHdrVideoOnAnHdrMonitorWithTheOptionOn(
        bool enabled, HdrTransferFunction? content, bool hdrMonitor, bool scRgb, string reason)
    {
        var decision = HdrOutputPolicy.Choose(enabled, content, hdrMonitor ? HdrMonitor : SdrMonitor);
        Assert.Equal(scRgb ? HdrOutputMode.ScRgb : HdrOutputMode.Sdr, decision.Mode);
        Assert.Equal(reason, decision.Reason);
    }

    [Fact]
    public void UnreportedValues_FallBackToTheDefaults()
    {
        var decision = HdrOutputPolicy.Choose(true, HdrTransferFunction.Pq, new HdrDisplayInfo(true, 0, double.NaN));
        Assert.Equal(HdrOutputMode.ScRgb, decision.Mode);
        Assert.Equal(HdrOutputPolicy.AssumedDisplayPeakNits, decision.DisplayPeakNits);
        Assert.Equal(HdrOutputPolicy.DefaultSdrWhiteNits, decision.SdrWhiteNits);
        Assert.Contains("assumed", decision.Reason, StringComparison.Ordinal);

        // A peak below the SDR white level is not believable either.
        Assert.Equal(HdrOutputPolicy.AssumedDisplayPeakNits, HdrOutputPolicy.Choose(true, HdrTransferFunction.Pq, new HdrDisplayInfo(true, 150, 200)).DisplayPeakNits);
    }

    [Fact]
    public void ABlocker_TurnsOnlyAnHdrDecisionIntoSdr()
    {
        var hdr = HdrOutputPolicy.Choose(true, HdrTransferFunction.Pq, HdrMonitor);
        var sdr = HdrOutputPolicy.Choose(true, null, HdrMonitor);

        var blocked = HdrOutputPolicy.Restrict(hdr, "a video effect is on");
        Assert.Equal(HdrOutputMode.Sdr, blocked.Mode);
        Assert.Equal("a video effect is on", blocked.Reason);
        Assert.Equal(200, blocked.SdrWhiteNits);

        // No blocker, or an SDR decision: unchanged, with its own reason.
        Assert.Equal(hdr, HdrOutputPolicy.Restrict(hdr, null));
        Assert.Equal(sdr, HdrOutputPolicy.Restrict(sdr, "a video effect is on"));
    }

    [Fact]
    public void MovingBetweenMonitors_ChangesTheSwapChainOnlyWhenTheModeChanges()
    {
        var onHdr = HdrOutputPolicy.Choose(true, HdrTransferFunction.Pq, HdrMonitor);
        var onSdr = HdrOutputPolicy.Choose(true, HdrTransferFunction.Pq, SdrMonitor);
        var onBrighterHdr = HdrOutputPolicy.Choose(true, HdrTransferFunction.Pq, HdrMonitor with { PeakNits = 1000, SdrWhiteNits = 240 });

        Assert.True(HdrOutputPolicy.NeedsSwapChainChange(onSdr, onHdr));
        Assert.True(HdrOutputPolicy.NeedsSwapChainChange(onHdr, onSdr));
        Assert.False(HdrOutputPolicy.NeedsSwapChainChange(onHdr, onBrighterHdr));
        Assert.False(HdrOutputPolicy.NeedsSwapChainChange(null, onSdr));
        Assert.True(HdrOutputPolicy.NeedsSwapChainChange(null, onHdr));
    }

    [Theory]
    [InlineData(1000u, 80)]
    [InlineData(2500u, 200)]
    [InlineData(6000u, 480)]
    public void TheSdrWhiteLevel_ConvertsToNits(uint level, double nits) =>
        Assert.Equal(nits, HdrOutputPolicy.SdrWhiteLevelToNits(level), 9);

    [Fact]
    public void SdrLayers_AreComposedAtTheSdrWhiteLevel()
    {
        Assert.Equal(0, HdrOutputPolicy.SdrToScRgb(0, 200));
        Assert.Equal(2.5, HdrOutputPolicy.SdrToScRgb(255, 200), 9);
        Assert.Equal(1, HdrOutputPolicy.SdrToScRgb(255, 80), 9);

        // sRGB mid gray (128) is about 21.6% of white in linear light.
        Assert.Equal(0.2159 * 2.5, HdrOutputPolicy.SdrToScRgb(128, 200), 3);

        double previous = -1;
        for (var value = 0; value <= 255; value++)
        {
            var scRgb = HdrOutputPolicy.SdrToScRgb((byte)value, 200);
            Assert.True(scRgb > previous);
            previous = scRgb;
        }
    }

    [Fact]
    public void PqVideo_IsCompressedOnlyWhenBrighterThanTheDisplay()
    {
        // A 1000-nit grade on a 1000-nit display: unchanged.
        Assert.Equal(500 / 80.0, HdrOutputPolicy.PqNitsToScRgb(500, 1000, 1000), 9);

        // A 4000-nit grade on a 600-nit display: the peak reaches the display's peak, shadows stay.
        Assert.InRange(HdrOutputPolicy.PqNitsToScRgb(4000, 4000, 600), (600 - 1) / 80.0, 600 / 80.0);
        Assert.Equal(50 / 80.0, HdrOutputPolicy.PqNitsToScRgb(50, 4000, 600), 6);
    }

    [Fact]
    public void HlgVideo_UsesTheDisplaysPeakForItsOotf()
    {
        // HLG white is the display's peak; 75% (reference white) follows BT.2100's gamma for it.
        Assert.Equal(600 / 80.0, HdrOutputPolicy.HlgToScRgb(1, 1, 600), 9);
        var light = HlgCurve.ToSceneLinear(0.75);
        var expected = 600 * Math.Pow(light, HlgCurve.SystemGamma(600)) / 80;
        Assert.Equal(expected, HdrOutputPolicy.HlgToScRgb(light, light, 600), 9);
        Assert.Equal(1.1068, HlgCurve.SystemGamma(600), 4);
    }

    [Fact]
    public void TheMetadata_UsesDxgiUnits()
    {
        var metadata = HdrOutputPolicy.CreateMetadata(1000, 400, 0.005);

        Assert.Equal((35400, 14600), (metadata.RedX, metadata.RedY));
        Assert.Equal((8500, 39850), (metadata.GreenX, metadata.GreenY));
        Assert.Equal((6550, 2300), (metadata.BlueX, metadata.BlueY));
        Assert.Equal((15635, 16450), (metadata.WhiteX, metadata.WhiteY));
        Assert.Equal(1000u, metadata.MaxMasteringLuminance);
        Assert.Equal(50u, metadata.MinMasteringLuminance);
        Assert.Equal(1000, metadata.MaxContentLightLevel);
        Assert.Equal(400, metadata.MaxFrameAverageLightLevel);
    }
}
