using FatzBomb.MediaControl.Windows.Video;

namespace FatzBomb.MediaControl.Tests;

/// <summary>
/// ENG-039 G2: <c>DWM_TIMING_INFO</c> is packed (dwmapi.h, <c>#pragma pack(1)</c>). With the
/// default layout (320 bytes) DWM refused every call and the platform refresh rate was always the
/// 60 Hz fallback. Windows only (Portable.Tests excludes this file).
/// </summary>
public sealed class DwmCompositionTimingTests
{
    [Fact]
    public void TimingInfo_MatchesPackedNativeSize() => Assert.Equal(292, DwmCompositionTiming.TimingInfoSize);

    [Fact]
    public void RefreshInterval_IsPlausible()
    {
        // DWM's rate where the session has one (a desktop), the fallback otherwise (some CI hosts).
        var interval = DwmCompositionTiming.GetRefreshIntervalSeconds();

        Assert.InRange(interval, 1.0 / 500, 1.0 / 20);
    }
}
