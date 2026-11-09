namespace FatzMedia.Video.Core.Audio;

/// <summary>
/// Peak analysis for FFmpeg-decoded audio. It samples the same windows as
/// <see cref="AudioNormalizer"/>'s BASS analysis, so a video's audio gets the same gain the
/// same audio would get as a plain file: a quick pass over the first 24 x 0.25 s, then a deep
/// pass of up to 240 half-second windows spread over the whole duration.
/// </summary>
internal static class FfmpegAudioPeakScanner
{
    internal const double QuickSeconds = 24 * 0.25;
    internal const double DeepWindowSeconds = 0.5;
    internal const int DeepMaxWindows = 240;

    /// <summary>Peak of the first <see cref="QuickSeconds"/> from the reader's current position.</summary>
    public static double ScanQuick(FfmpegAudioReader reader, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reader);
        return ReadPeak(reader, QuickSeconds, cancellationToken);
    }

    /// <summary>
    /// Peak over windows spread across the file. Without a known duration it reads
    /// sequentially from the current position, as the BASS analysis does.
    /// </summary>
    public static double ScanDeep(FfmpegAudioReader reader, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reader);
        if (reader.DurationSeconds <= 0)
            return ReadPeak(reader, DeepWindowSeconds * DeepMaxWindows, cancellationToken);

        var peak = 0.0;
        foreach (var start in PlanWindows(reader.DurationSeconds, DeepWindowSeconds, DeepMaxWindows))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!reader.Seek(start))
                break;

            peak = Math.Max(peak, ReadPeak(reader, DeepWindowSeconds, cancellationToken));
        }

        return peak;
    }

    /// <summary>Window start times, matching <c>AudioNormalizer.AnalyzePeakBySeek</c>'s stride.</summary>
    internal static IReadOnlyList<double> PlanWindows(double durationSeconds, double windowSeconds, int maxWindows)
    {
        if (!double.IsFinite(durationSeconds) || durationSeconds <= 0 || windowSeconds <= 0 || maxWindows <= 0)
            return [];

        var stride = Math.Max(1, (int)Math.Ceiling(durationSeconds / (windowSeconds * maxWindows)));
        List<double> starts = [];
        for (var position = 0.0; position < durationSeconds; position += windowSeconds * stride)
            starts.Add(position);

        return starts;
    }

    /// <summary>Largest absolute sample, clamped to 0..1 like a BASS level reading.</summary>
    internal static double PeakOf(ReadOnlySpan<float> samples)
    {
        var peak = 0f;
        foreach (var sample in samples)
        {
            var magnitude = Math.Abs(sample);
            if (magnitude > peak)
                peak = magnitude;
        }

        return float.IsFinite(peak) ? Math.Clamp(peak, 0, 1) : 0;
    }

    private static double ReadPeak(FfmpegAudioReader reader, double seconds, CancellationToken cancellationToken)
    {
        var remainingSamples = (long)Math.Ceiling(seconds * reader.SampleRate) * reader.Channels;
        var peak = 0.0;
        while (remainingSamples > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadOnlySpan<float> frame = reader.ReadFrame();
            if (frame.IsEmpty)
                break;

            if (frame.Length > remainingSamples)
                frame = frame[..(int)remainingSamples];

            peak = Math.Max(peak, PeakOf(frame));
            remainingSamples -= frame.Length;
        }

        return peak;
    }
}
