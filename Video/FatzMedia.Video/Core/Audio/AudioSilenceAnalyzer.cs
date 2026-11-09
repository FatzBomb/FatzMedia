namespace FatzMedia.Video.Core.Audio;

readonly record struct SilenceScanResult(
    double SourceDuration,
    IReadOnlyList<SilenceRegion> SilenceRegions);

/// <summary>
/// Scans a decoded stream for silence in <see cref="BlockDurationSeconds"/> blocks, through an
/// engine-neutral <see cref="IAudioLevelSource"/> (037A slice 1; BASS:
/// <see cref="BassChannelLevelSource"/>).
/// </summary>
static class AudioSilenceAnalyzer
{
    internal const double BlockDurationSeconds = 0.1;

    public static Task<SilenceScanResult> ScanAsync(
        IAudioLevelSource? source,
        double sourceDuration,
        double thresholdDecibels,
        CancellationToken cancellationToken)
    {
        if (source is null || !double.IsFinite(sourceDuration) || sourceDuration <= 0)
        {
            var normalizedDuration = double.IsFinite(sourceDuration)
                ? Math.Max(0, sourceDuration)
                : 0;
            return Task.FromResult(new SilenceScanResult(normalizedDuration, []));
        }

        return Task.Run(
            () => Scan(source, sourceDuration, thresholdDecibels, cancellationToken),
            cancellationToken);
    }

    static SilenceScanResult Scan(
        IAudioLevelSource source,
        double sourceDuration,
        double thresholdDecibels,
        CancellationToken cancellationToken)
    {
        if (!source.TrySeekToStart())
        {
            throw new InvalidOperationException($"Unable to seek the analysis stream to its start: {source.LastError}");
        }

        var samples = new List<SilenceLevelSample>();

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!source.TryGetPosition(out var startSeconds))
            {
                throw new InvalidOperationException($"Unable to read the analysis stream position: {source.LastError}");
            }

            if (!double.IsFinite(startSeconds))
            {
                throw new InvalidOperationException("The analysis stream returned an invalid position.");
            }

            if (startSeconds >= sourceDuration)
            {
                break;
            }

            var blockDuration = Math.Min(BlockDurationSeconds, sourceDuration - startSeconds);
            if (!source.TryReadPeak((float)blockDuration, out var blockPeak))
            {
                throw new InvalidOperationException($"Unable to read the analysis stream at {startSeconds:F3}s: {source.LastError}");
            }

            if (!source.TryGetPosition(out var endSeconds))
            {
                throw new InvalidOperationException($"Unable to read the analysis stream position: {source.LastError}");
            }

            if (!double.IsFinite(endSeconds) || endSeconds <= startSeconds)
            {
                throw new InvalidOperationException($"The analysis stream made no progress at {startSeconds:F3}s.");
            }

            var peak = Math.Clamp(blockPeak, 0, 1);
            samples.Add(new SilenceLevelSample(
                startSeconds,
                Math.Min(sourceDuration, endSeconds),
                peak));
        }

        return new SilenceScanResult(
            sourceDuration,
            SilenceRegionDetector.Detect(samples, thresholdDecibels));
    }
}
