#pragma warning disable IDE0130
namespace FatzMedia.Video;

/// <summary>
/// Loudness (LUFS) normalization on the FatzAudio engine (037A): every song plays at the same
/// perceived loudness, measured to ITU-R BS.1770 / EBU R128, with a true-peak limiter. The
/// player's <c>Normalize</c> switch turns it on or off. The default, <c>default(NormalizationOptions)</c>,
/// is the streaming-service standard: -14 LUFS, peaks limited to -1 dBTP.
/// </summary>
/// <remarks>
/// FatzAudio does the measuring, gain and limiting. A song whose loudness is not known yet (its
/// first play, without tags) starts on FatzAudio's running estimate and glides to the measured
/// level when the scan finishes, a few hundred milliseconds for a typical song.
/// </remarks>
public readonly record struct NormalizationOptions
{
    /// <summary>The default target: -14 LUFS integrated (Spotify, YouTube and similar services).</summary>
    public const double DefaultTargetLufs = -14;

    /// <summary>The default limiter ceiling: -1 dBTP.</summary>
    public const double DefaultTruePeakCeilingDbtp = -1;

    const double MinimumTargetLufs = -60;
    const double MaximumTargetLufs = 0;
    const double MinimumCeilingDbtp = -20;
    const double MaximumCeilingDbtp = 0;

    /// <summary>
    /// Gets the integrated loudness every song is brought to, in LUFS (-60 to 0). A karaoke host
    /// wanting headroom for microphones might use about -16.
    /// </summary>
    public double TargetLufs
    {
        get => DefaultTargetLufs + field;
        init => field = Clamp(value, MinimumTargetLufs, MaximumTargetLufs, DefaultTargetLufs) - DefaultTargetLufs;
    }

    /// <summary>
    /// Gets the true-peak ceiling of the output limiter, in dBTP (-20 to 0). The limiter runs
    /// while normalization is on, so a boost never clips.
    /// </summary>
    public double TruePeakCeilingDbtp
    {
        get => DefaultTruePeakCeilingDbtp + field;
        init => field = Clamp(value, MinimumCeilingDbtp, MaximumCeilingDbtp, DefaultTruePeakCeilingDbtp) - DefaultTruePeakCeilingDbtp;
    }

    /// <summary>
    /// Gets the folder where measured loudness is kept, so a song is scanned once, not once per
    /// run. <see langword="null"/> (the default) uses a per-user folder
    /// (<c>%LOCALAPPDATA%\FatzMedia.Video\AudioCache</c> on Windows,
    /// <c>$XDG_CACHE_HOME/fatzmedia-video/audio</c> or <c>~/.cache/fatzmedia-video/audio</c>
    /// elsewhere); an empty string turns the cache off. A relative path is made absolute when the
    /// player is created. Entries are a few bytes each, keyed by the
    /// file's content (size and three 64 KB blocks), so renamed or moved files are still found.
    /// </summary>
    public string? CacheDirectory { get; init; }

    static double Clamp(double value, double minimum, double maximum, double fallback)
        => double.IsFinite(value) ? Math.Clamp(value, minimum, maximum) : fallback;
}
