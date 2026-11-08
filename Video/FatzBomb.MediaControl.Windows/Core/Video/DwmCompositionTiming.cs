using System.Runtime.InteropServices;
using FatzBomb.MediaControl.Core.Video;

namespace FatzBomb.MediaControl.Windows.Video;

/// <summary>
/// Reads the global Desktop Window Manager composition refresh interval.
/// This is a Windows-only fallback for callers without an explicit per-monitor
/// refresh rate; DWM does not reliably identify a specific display on mixed-refresh
/// multi-monitor setups.
/// </summary>
internal static partial class DwmCompositionTiming
{
    private static int s_failureReported;

    /// <summary>The size DWM expects in <c>DWM_TIMING_INFO.cbSize</c>: the packed native layout.</summary>
    internal static int TimingInfoSize => Marshal.SizeOf<DwmTimingInfo>();

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
                Size = (uint)TimingInfoSize
            };

            var result = DwmGetCompositionTimingInfo(IntPtr.Zero, ref timingInfo);
            if (result == 0)
                return DisplayRefreshInterval.FromRefreshRate(timingInfo.RefreshRate.Numerator, timingInfo.RefreshRate.Denominator);

            ReportFailureOnce($"DwmGetCompositionTimingInfo failed (HRESULT 0x{result:X8})");
            return DisplayRefreshInterval.FallbackSeconds;
        }
        catch (Exception ex)
        {
            ReportFailureOnce($"DwmGetCompositionTimingInfo failed ({ex.GetType().Name}: {ex.Message})");
            return DisplayRefreshInterval.FallbackSeconds;
        }
    }

    /// <summary>
    /// Once per process, and for no player in particular (the DWM rate is global): a failure here
    /// used to be silent, and video timing then always used the 60 Hz fallback (ENG-039 G2).
    /// </summary>
    private static void ReportFailureOnce(string detail)
    {
        if (Interlocked.Exchange(ref s_failureReported, 1) != 0)
            return;

        DiagnosticRouter.Raise(
            owner: null,
            MediaDiagnosticLevel.Warning,
            "DisplayTiming",
            $"{detail}; video timing without a per-monitor rate uses the 60 Hz fallback.");
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct UnsignedRatio
    {
        public uint Numerator;
        public uint Denominator;
    }

    // DWM_TIMING_INFO is declared under #pragma pack(1) (dwmapi.h): 292 bytes. The default layout
    // (320 bytes) made every call fail with E_INVALIDARG (ENG-039 G2).
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
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
