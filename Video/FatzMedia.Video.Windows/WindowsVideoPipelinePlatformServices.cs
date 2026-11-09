using System.Diagnostics;
using FatzMedia.Video.Core.Abstractions;
using FatzMedia.Video.Windows.Video;

namespace FatzMedia.Video;

/// <summary>
/// Supplies Windows DWM timing and Direct3D device-loss checks to Core video playback.
/// </summary>
internal sealed class WindowsVideoPipelinePlatformServices : IVideoPipelinePlatformServices
{
    // The video presentation loop reads the interval several times per frame; the 022A
    // baseline showed the DWM call costing measurable presentation-thread time. A rate change
    // (display settings) is picked up within this age.
    private static readonly long MaxAgeTicks = Stopwatch.Frequency;

    private long _cachedIntervalBits;
    private long _cachedAtTimestamp = long.MinValue;

    public double GetPresentationRefreshIntervalSeconds()
    {
        var now = Stopwatch.GetTimestamp();
        var cachedAt = Interlocked.Read(ref _cachedAtTimestamp);
        if (cachedAt != long.MinValue && now - cachedAt < MaxAgeTicks)
            return BitConverter.Int64BitsToDouble(Interlocked.Read(ref _cachedIntervalBits));

        var interval = DwmCompositionTiming.GetRefreshIntervalSeconds();
        _ = Interlocked.Exchange(ref _cachedIntervalBits, BitConverter.DoubleToInt64Bits(interval));
        _ = Interlocked.Exchange(ref _cachedAtTimestamp, now);
        return interval;
    }
}
