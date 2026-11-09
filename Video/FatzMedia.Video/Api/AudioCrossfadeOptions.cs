#pragma warning disable IDE0130
namespace FatzMedia.Video;

/// <summary>
/// Fade configuration shared by the outgoing and incoming envelopes of a transition.
/// Disabled by default (<see cref="Duration"/> is zero).
/// </summary>
public sealed record AudioCrossfadeOptions
{
    /// <summary>Gets the maximum supported fade duration.</summary>
    public static TimeSpan MaximumDuration { get; } = TimeSpan.FromSeconds(60);

    /// <summary>Gets the disabled options.</summary>
    public static AudioCrossfadeOptions Disabled { get; } = new();

    /// <summary>
    /// Gets the shared fade duration. Negative values normalize to zero (disabled); values above
    /// <see cref="MaximumDuration"/> clamp to it.
    /// </summary>
    public TimeSpan Duration
    {
        get;
        init => field = value <= TimeSpan.Zero
            ? TimeSpan.Zero
            : value > MaximumDuration ? MaximumDuration : value;
    }

    /// <summary>Gets the outgoing envelope.</summary>
    public AudioFadeEnvelope Outgoing
    {
        get;
        init => field = value ?? AudioFadeEnvelope.Outgoing;
    } = AudioFadeEnvelope.Outgoing;

    /// <summary>Gets the incoming envelope.</summary>
    public AudioFadeEnvelope Incoming
    {
        get;
        init => field = value ?? AudioFadeEnvelope.Incoming;
    } = AudioFadeEnvelope.Incoming;

    /// <summary>Gets whether fades are applied.</summary>
    public bool IsEnabled => Duration > TimeSpan.Zero;
}
