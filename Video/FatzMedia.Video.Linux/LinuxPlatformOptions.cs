namespace FatzMedia.Video.Linux;

/// <summary>
/// Host options for the Linux platform package.
/// </summary>
public sealed record LinuxPlatformOptions
{
    /// <summary>Gets the default options.</summary>
    public static LinuxPlatformOptions Default { get; } = new();

    /// <summary>
    /// Gets the display refresh rate used for presentation pacing. Linux has no compositor-neutral
    /// query comparable to DWM, so the host supplies it (default 60 Hz; clamped to 24..360 Hz).
    /// </summary>
    public double DisplayRefreshRateHz
    {
        get;
        init => field = double.IsFinite(value) ? Math.Clamp(value, 24, 360) : 60;
    } = 60;

    /// <summary>Gets how often BASS output-device state is polled (clamped to 0.25..10 s).</summary>
    public TimeSpan AudioDevicePollInterval
    {
        get;
        init => field = value < TimeSpan.FromMilliseconds(250)
            ? TimeSpan.FromMilliseconds(250)
            : value > TimeSpan.FromSeconds(10) ? TimeSpan.FromSeconds(10) : value;
    } = TimeSpan.FromSeconds(1);
}
