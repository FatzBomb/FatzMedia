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
}
