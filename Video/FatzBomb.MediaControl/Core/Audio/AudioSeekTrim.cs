namespace FatzBomb.MediaControl.Core.Audio;

/// <summary>
/// After a seek, how many samples of a decoded audio frame lie before the seek target. The frame
/// that spans the target is trimmed to start exactly there, so the first sample heard matches the
/// position reported for the target (dropping the whole frame made the sound lead the clock by up
/// to one frame: 21-93 ms depending on the codec).
/// </summary>
internal static class AudioSeekTrim
{
    /// <summary>
    /// Returns the samples to skip at the start of a frame of <paramref name="frameSamples"/>
    /// samples starting at <paramref name="framePositionSeconds"/>: 0 for a frame at or after the
    /// target (or without a usable position), <paramref name="frameSamples"/> for one wholly before it.
    /// </summary>
    public static int SamplesToSkip(double framePositionSeconds, double targetSeconds, int sampleRate, int frameSamples)
    {
        if (frameSamples <= 0 || sampleRate <= 0 || !double.IsFinite(framePositionSeconds) || !double.IsFinite(targetSeconds))
            return 0;

        var skip = Math.Round((targetSeconds - framePositionSeconds) * sampleRate);
        return skip <= 0 ? 0 : skip >= frameSamples ? frameSamples : (int)skip;
    }
}
