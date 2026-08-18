namespace FatzBomb.MediaControl.Core.Audio;

internal static class MediaAnalysisResultValidator
{
    private const double EqualityTolerance = 0.000001;

    public static bool IsValid(MediaAnalysisResult? result)
    {
        if (result is null
            || !IsFiniteNonNegative(result.SourceDuration)
            || !IsFiniteNonNegative(result.PlayableStart)
            || !IsFiniteNonNegative(result.PlayableEnd)
            || !IsFiniteNonNegative(result.EffectiveDuration)
            || !double.IsFinite(result.NormalizationGain)
            || result.NormalizationGain <= 0
            || result.PlayableStart > result.PlayableEnd
            || result.PlayableEnd > result.SourceDuration)
        {
            return false;
        }

        var playableDuration = result.PlayableDuration;
        if (!ApproximatelyEqual(result.EffectiveDuration, result.SourceDuration)
            && !ApproximatelyEqual(result.EffectiveDuration, playableDuration))
        {
            return false;
        }

        foreach (var region in result.SilenceRegions)
        {
            if (!double.IsFinite(region.StartSeconds)
                || !double.IsFinite(region.EndSeconds)
                || region.StartSeconds < 0
                || region.EndSeconds <= region.StartSeconds
                || region.EndSeconds > result.SourceDuration)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsFiniteNonNegative(double value) => double.IsFinite(value) && value >= 0;

    private static bool ApproximatelyEqual(double left, double right)
        => Math.Abs(left - right) <= EqualityTolerance;
}
