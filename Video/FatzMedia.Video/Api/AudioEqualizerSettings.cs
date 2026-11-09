#pragma warning disable IDE0130
namespace FatzMedia.Video;

/// <summary>
/// One peaking-equalizer band. Values are clamped to safe ranges; non-finite values use the
/// neutral defaults.
/// </summary>
public readonly record struct AudioEqualizerBand
{
    public const double MinimumCenterHz = 20;
    public const double MaximumCenterHz = 20000;
    public const double MaximumGainDb = 15;
    public const double MinimumBandwidthOctaves = 0.1;
    public const double MaximumBandwidthOctaves = 4;

    /// <param name="centerHz">Center frequency, 20 Hz .. 20 kHz.</param>
    /// <param name="gainDb">Boost/cut in dB, -15 .. +15.</param>
    /// <param name="bandwidthOctaves">Bandwidth in octaves, 0.1 .. 4.</param>
    public AudioEqualizerBand(double centerHz, double gainDb, double bandwidthOctaves = 1)
    {
        CenterHz = double.IsFinite(centerHz) ? Math.Clamp(centerHz, MinimumCenterHz, MaximumCenterHz) : 1000;
        GainDb = double.IsFinite(gainDb) ? Math.Clamp(gainDb, -MaximumGainDb, MaximumGainDb) : 0;
        BandwidthOctaves = double.IsFinite(bandwidthOctaves)
            ? Math.Clamp(bandwidthOctaves, MinimumBandwidthOctaves, MaximumBandwidthOctaves)
            : 1;
    }

    public double CenterHz { get; }

    public double GainDb { get; }

    public double BandwidthOctaves { get; }

    /// <summary>Gets whether the band leaves the signal unchanged.</summary>
    public bool IsNeutral => GainDb == 0;
}

/// <summary>
/// Equalizer configuration for the first filter of the audio filter framework (Stage 020A).
/// Disabled by default. A disabled or all-neutral equalizer attaches no native filter, so
/// output is bit-identical to having no equalizer.
/// </summary>
public sealed record AudioEqualizerSettings
{
    /// <summary>Gets the maximum number of bands.</summary>
    public const int MaximumBands = 16;

    /// <summary>Gets the ISO octave centres used by <see cref="CreateGraphic10Band"/>.</summary>
    public static IReadOnlyList<double> Graphic10BandCentersHz { get; } = [31.5, 63, 125, 250, 500, 1000, 2000, 4000, 8000, 16000];

    /// <summary>Gets the disabled settings.</summary>
    public static AudioEqualizerSettings Disabled { get; } = new();

    /// <summary>Gets whether the equalizer is switched on (false = bypassed).</summary>
    public bool Enabled { get; init; }

    /// <summary>Gets the bands (at most <see cref="MaximumBands"/>; extra bands are dropped).</summary>
    public IReadOnlyList<AudioEqualizerBand> Bands
    {
        get;
        init => field = Array.AsReadOnly((value ?? []).Take(MaximumBands).ToArray());
    } = Array.AsReadOnly(Array.Empty<AudioEqualizerBand>());

    /// <summary>Gets whether a native filter is needed (enabled with at least one non-neutral band).</summary>
    public bool IsEffective => Enabled && Bands.Any(static band => !band.IsNeutral);

    /// <summary>
    /// Creates an enabled 10-band graphic equalizer (ISO octave centres, one-octave bands).
    /// Missing gains are 0 dB.
    /// </summary>
    public static AudioEqualizerSettings CreateGraphic10Band(params double[] gainsDb) => new()
    {
        Enabled = true,
        Bands = [.. Graphic10BandCentersHz.Select((center, index) => new AudioEqualizerBand(center, index < gainsDb.Length ? gainsDb[index] : 0))],
    };

    public bool Equals(AudioEqualizerSettings? other) =>
        other is not null && Enabled == other.Enabled && Bands.SequenceEqual(other.Bands);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Enabled);
        foreach (AudioEqualizerBand band in Bands)
            hash.Add(band);
        return hash.ToHashCode();
    }
}
