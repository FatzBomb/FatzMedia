using ManagedBass;
using MBass = ManagedBass.Bass;

namespace FatzBomb.MediaControl.Core.Audio;

internal readonly record struct SilenceScanResult(
    double SourceDuration,
    IReadOnlyList<SilenceRegion> SilenceRegions);

internal sealed class AudioSilenceAnalyzer
{
    internal const double BlockDurationSeconds = 0.1;

    private static readonly ThreadLocal<float[]> LevelBuffer = new(() => new float[2]);

    public Task<SilenceScanResult> ScanAsync(
        int analysisHandle,
        double sourceDuration,
        double thresholdDecibels,
        CancellationToken cancellationToken)
    {
        if (analysisHandle == 0 || !double.IsFinite(sourceDuration) || sourceDuration <= 0)
        {
            var normalizedDuration = double.IsFinite(sourceDuration)
                ? Math.Max(0, sourceDuration)
                : 0;
            return Task.FromResult(new SilenceScanResult(normalizedDuration, []));
        }

        return Task.Run(
            () => Scan(analysisHandle, sourceDuration, thresholdDecibels, cancellationToken),
            cancellationToken);
    }

    private static SilenceScanResult Scan(
        int analysisHandle,
        double sourceDuration,
        double thresholdDecibels,
        CancellationToken cancellationToken)
    {
        if (!MBass.ChannelSetPosition(analysisHandle, 0))
        {
            throw new InvalidOperationException($"Unable to seek the analysis stream to its start: {MBass.LastError}");
        }

        const LevelRetrievalFlags flags = LevelRetrievalFlags.Stereo;
        var levelBuffer = LevelBuffer.Value!;
        var samples = new List<SilenceLevelSample>();

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var startBytes = MBass.ChannelGetPosition(analysisHandle);
            if (startBytes < 0)
            {
                throw new InvalidOperationException($"Unable to read the analysis stream position: {MBass.LastError}");
            }

            var startSeconds = MBass.ChannelBytes2Seconds(analysisHandle, startBytes);
            if (!double.IsFinite(startSeconds))
            {
                throw new InvalidOperationException("The analysis stream returned an invalid position.");
            }

            if (startSeconds >= sourceDuration)
            {
                break;
            }

            var blockDuration = Math.Min(BlockDurationSeconds, sourceDuration - startSeconds);
            if (!MBass.ChannelGetLevel(analysisHandle, levelBuffer, (float)blockDuration, flags))
            {
                throw new InvalidOperationException($"Unable to read the analysis stream at {startSeconds:F3}s: {MBass.LastError}");
            }

            var endBytes = MBass.ChannelGetPosition(analysisHandle);
            if (endBytes < 0)
            {
                throw new InvalidOperationException($"Unable to read the analysis stream position: {MBass.LastError}");
            }

            var endSeconds = MBass.ChannelBytes2Seconds(analysisHandle, endBytes);
            if (!double.IsFinite(endSeconds) || endSeconds <= startSeconds)
            {
                throw new InvalidOperationException($"The analysis stream made no progress at {startSeconds:F3}s.");
            }

            var peak = Math.Clamp((double)Math.Max(levelBuffer[0], levelBuffer[1]), 0, 1);
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
