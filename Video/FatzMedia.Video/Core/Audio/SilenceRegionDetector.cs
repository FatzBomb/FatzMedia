namespace FatzMedia.Video.Core.Audio;

readonly record struct SilenceLevelSample(
    double StartSeconds,
    double EndSeconds,
    double PeakLevel);

static class SilenceRegionDetector
{
    const double MergeToleranceSeconds = 0.000001;

    public static SilenceRegion[] Detect(
        IEnumerable<SilenceLevelSample>? samples,
        double thresholdDecibels)
    {
        var thresholdLevel = ConvertDecibelsToLinear(thresholdDecibels);
        var regions = new List<SilenceRegion>();
        double? regionStart = null;
        var regionEnd = 0d;

        foreach (var sample in (samples ?? [])
            .Where(IsValid)
            .OrderBy(sample => sample.StartSeconds)
            .ThenBy(sample => sample.EndSeconds))
        {
            if (sample.PeakLevel > thresholdLevel)
            {
                CompleteRegion(regions, ref regionStart, ref regionEnd);
                continue;
            }

            if (regionStart is null)
            {
                regionStart = sample.StartSeconds;
                regionEnd = sample.EndSeconds;
                continue;
            }

            if (sample.StartSeconds <= regionEnd + MergeToleranceSeconds)
            {
                regionEnd = Math.Max(regionEnd, sample.EndSeconds);
                continue;
            }

            CompleteRegion(regions, ref regionStart, ref regionEnd);
            regionStart = sample.StartSeconds;
            regionEnd = sample.EndSeconds;
        }

        CompleteRegion(regions, ref regionStart, ref regionEnd);
        return [.. regions];
    }

    internal static double ConvertDecibelsToLinear(double thresholdDecibels)
    {
        var normalizedThreshold = new SilenceTrimOptions
        {
            ThresholdDecibels = thresholdDecibels
        }.ThresholdDecibels;
        return Math.Pow(10, normalizedThreshold / 20);
    }

    static bool IsValid(SilenceLevelSample sample) => double.IsFinite(sample.StartSeconds)
        && double.IsFinite(sample.EndSeconds)
        && double.IsFinite(sample.PeakLevel)
        && sample.StartSeconds >= 0
        && sample.EndSeconds > sample.StartSeconds
        && sample.PeakLevel >= 0;

    static void CompleteRegion(
        List<SilenceRegion> regions,
        ref double? regionStart,
        ref double regionEnd)
    {
        if (regionStart is { } start)
        {
            regions.Add(new SilenceRegion(start, regionEnd));
        }

        regionStart = null;
        regionEnd = 0;
    }
}
