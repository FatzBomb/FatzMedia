#pragma warning disable IDE0130
namespace FatzMedia.Video;

/// <summary>
/// Describes a detected silent interval in source-time seconds.
/// </summary>
public readonly record struct SilenceRegion(double StartSeconds, double EndSeconds)
{
    public double Duration => EndSeconds - StartSeconds;
}
