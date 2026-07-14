using System.Runtime.InteropServices;

namespace FatzBomb.MediaControl.Core.Video;

/// <summary>
/// Reads the global Desktop Window Manager composition refresh interval.
/// This is a Windows-only fallback for callers without an explicit per-monitor
/// refresh rate; DWM does not reliably identify a specific display on mixed-refresh
/// multi-monitor setups.
/// </summary>
internal static partial class DwmCompositionTiming
{
    private const double FallbackIntervalSeconds = 1.0 / 60.0;

    /// <summary>
    /// Returns DWM's global composition refresh interval, or the 60Hz fallback
    /// when DWM is unavailable or reports an implausible refresh rate.
    /// </summary>
    public static double GetRefreshIntervalSeconds()
    {
        try
        {
            var timingInfo = new DwmTimingInfo
            {
                Size = (uint)Marshal.SizeOf<DwmTimingInfo>()
            };

            return DwmGetCompositionTimingInfo(IntPtr.Zero, ref timingInfo) == 0
                ? GetRefreshIntervalSeconds(timingInfo.RefreshRate.Numerator, timingInfo.RefreshRate.Denominator)
                : FallbackIntervalSeconds;
        }
        catch
        {
            return FallbackIntervalSeconds;
        }
    }

    /// <summary>
    /// Converts a DWM refresh-rate ratio to a usable interval, applying the same
    /// 23-600Hz validity range used for virtual displays, RDP sessions, and drivers.
    /// </summary>
    internal static double GetRefreshIntervalSeconds(uint numerator, uint denominator)
    {
        if (numerator == 0 || denominator == 0)
            return FallbackIntervalSeconds;

        var refreshHz = (double)numerator / denominator;
        return !double.IsFinite(refreshHz) || refreshHz < 23 || refreshHz > 600
            ? FallbackIntervalSeconds
            : 1.0 / refreshHz;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UnsignedRatio
    {
        public uint Numerator;
        public uint Denominator;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DwmTimingInfo
    {
        public uint Size;
        public UnsignedRatio RefreshRate;
        public ulong RefreshPeriodQpc;
        public UnsignedRatio ComposeRate;
        public ulong VBlankQpc;
        public ulong RefreshCount;
        public uint DxRefreshCount;
        public ulong ComposeQpc;
        public ulong FrameCount;
        public uint DxPresentCount;
        public ulong FrameRefreshCount;
        public ulong SubmittedFrameCount;
        public uint SubmittedDxPresentCount;
        public ulong ConfirmedFrameCount;
        public uint ConfirmedDxPresentCount;
        public ulong ConfirmedRefreshCount;
        public uint ConfirmedDxRefreshCount;
        public ulong LateFrameCount;
        public uint OutstandingFrameCount;
        public ulong DisplayedFrameCount;
        public ulong DisplayedFrameQpc;
        public ulong DisplayedFrameRefreshCount;
        public ulong CompletedFrameCount;
        public ulong CompletedFrameQpc;
        public ulong PendingFrameCount;
        public ulong PendingFrameQpc;
        public ulong DisplayedFramesCount;
        public ulong CompletedFramesCount;
        public ulong PendingFramesCount;
        public ulong AvailableFramesCount;
        public ulong DroppedFrameCount;
        public ulong MissedFrameCount;
        public ulong NextDisplayedRefreshCount;
        public ulong NextPresentedRefreshCount;
        public ulong DisplayedRefreshesCount;
        public ulong PresentedRefreshesCount;
        public ulong StartedRefreshCount;
        public ulong ReceivedPixelsCount;
        public ulong DrawnPixelsCount;
        public ulong EmptyBuffersCount;
    }

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmGetCompositionTimingInfo(IntPtr hwnd, ref DwmTimingInfo timingInfo);
}
