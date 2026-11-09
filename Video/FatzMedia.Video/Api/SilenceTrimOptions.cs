#pragma warning disable IDE0130
namespace FatzMedia.Video;

/// <summary>
/// Configures optional leading and trailing silence trimming for analyzed plain audio.
/// </summary>
public readonly record struct SilenceTrimOptions
{
    public const double DefaultThresholdDecibels = -50;
    public const double DefaultMinimumSilenceSeconds = 0.5;
    public const double DefaultPaddingSeconds = 0.1;

    const double MinimumThresholdDecibels = -96;
    const double MaximumThresholdDecibels = 0;
    const double MaximumDurationSeconds = 30;

    readonly double _thresholdOffset;
    readonly double _minimumSilenceOffset;
    readonly double _paddingOffset;
    readonly bool _preserveLeadingSilence;
    readonly bool _preserveTrailingSilence;

    /// <summary>
    /// Gets whether analyzed silence should affect the playable window.
    /// </summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// Gets the sample-level threshold, in dBFS, at or below which audio is considered silent.
    /// </summary>
    public double ThresholdDecibels
    {
        get => DefaultThresholdDecibels + _thresholdOffset;
        init => _thresholdOffset = Normalize(
            value,
            MinimumThresholdDecibels,
            MaximumThresholdDecibels,
            DefaultThresholdDecibels) - DefaultThresholdDecibels;
    }

    /// <summary>
    /// Gets the minimum duration required for an edge-silence region to qualify for trimming.
    /// </summary>
    public TimeSpan MinimumSilenceDuration
    {
        get => TimeSpan.FromSeconds(DefaultMinimumSilenceSeconds + _minimumSilenceOffset);
        init => _minimumSilenceOffset = Normalize(
            value.TotalSeconds,
            0,
            MaximumDurationSeconds,
            DefaultMinimumSilenceSeconds) - DefaultMinimumSilenceSeconds;
    }

    /// <summary>
    /// Gets the amount of qualifying silence retained at each trimmed edge.
    /// </summary>
    public TimeSpan Padding
    {
        get => TimeSpan.FromSeconds(DefaultPaddingSeconds + _paddingOffset);
        init => _paddingOffset = Normalize(
            value.TotalSeconds,
            0,
            MaximumDurationSeconds,
            DefaultPaddingSeconds) - DefaultPaddingSeconds;
    }

    /// <summary>
    /// Gets whether qualifying leading silence is trimmed when trimming is enabled.
    /// </summary>
    public bool TrimLeading
    {
        get => !_preserveLeadingSilence;
        init => _preserveLeadingSilence = !value;
    }

    /// <summary>
    /// Gets whether qualifying trailing silence is trimmed when trimming is enabled.
    /// </summary>
    public bool TrimTrailing
    {
        get => !_preserveTrailingSilence;
        init => _preserveTrailingSilence = !value;
    }

    static double Normalize(double value, double minimum, double maximum, double fallback)
        => double.IsFinite(value) ? Math.Clamp(value, minimum, maximum) : fallback;
}
