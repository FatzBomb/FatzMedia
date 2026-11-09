namespace FatzMedia.Video.Core.Audio;

internal static class SilenceTrimCalculator
{
    private const double EdgeToleranceSeconds = 0.001;

    public static MediaAnalysisResult Calculate(
        double sourceDuration,
        IEnumerable<SilenceRegion>? silenceRegions,
        double normalizationGain,
        SilenceTrimOptions options)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sourceDuration);
        if (!double.IsFinite(sourceDuration))
        {
            throw new ArgumentOutOfRangeException(nameof(sourceDuration));
        }

        if (!double.IsFinite(normalizationGain) || normalizationGain <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(normalizationGain));
        }

        var regions = NormalizeRegions(sourceDuration, silenceRegions);
        var playableStart = 0d;
        var playableEnd = sourceDuration;

        if (options.Enabled && options.TrimLeading)
        {
            SilenceRegion? leading = regions
                .Where(region => region.StartSeconds <= EdgeToleranceSeconds)
                .OrderByDescending(region => region.EndSeconds)
                .Select(static region => (SilenceRegion?)region)
                .FirstOrDefault();

            if (leading is { } region && region.Duration >= options.MinimumSilenceDuration.TotalSeconds)
            {
                playableStart = Math.Max(0, region.EndSeconds - options.Padding.TotalSeconds);
            }
        }

        if (options.Enabled && options.TrimTrailing)
        {
            SilenceRegion? trailing = regions
                .Where(region => region.EndSeconds >= sourceDuration - EdgeToleranceSeconds)
                .OrderBy(region => region.StartSeconds)
                .Select(static region => (SilenceRegion?)region)
                .FirstOrDefault();

            if (trailing is { } region && region.Duration >= options.MinimumSilenceDuration.TotalSeconds)
            {
                playableEnd = Math.Min(sourceDuration, region.StartSeconds + options.Padding.TotalSeconds);
            }
        }

        playableStart = Math.Clamp(playableStart, 0, sourceDuration);
        playableEnd = Math.Clamp(playableEnd, playableStart, sourceDuration);
        var effectiveDuration = options.Enabled ? playableEnd - playableStart : sourceDuration;

        return new MediaAnalysisResult(
            sourceDuration,
            playableStart,
            playableEnd,
            effectiveDuration,
            regions,
            normalizationGain);
    }

    private static SilenceRegion[] NormalizeRegions(double sourceDuration, IEnumerable<SilenceRegion>? silenceRegions)
        => (silenceRegions ?? [])
            .Where(region => double.IsFinite(region.StartSeconds)
                && double.IsFinite(region.EndSeconds)
                && region.EndSeconds > region.StartSeconds
                && region.EndSeconds >= 0
                && region.StartSeconds <= sourceDuration)
            .Select(region => new SilenceRegion(
                Math.Clamp(region.StartSeconds, 0, sourceDuration),
                Math.Clamp(region.EndSeconds, 0, sourceDuration)))
            .Where(region => region.Duration > 0)
            .OrderBy(region => region.StartSeconds)
            .ThenBy(region => region.EndSeconds)
            .ToArray();
}
