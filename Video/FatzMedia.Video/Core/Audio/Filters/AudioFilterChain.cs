namespace FatzMedia.Video.Core.Audio.Filters;

/// <summary>
/// Stable stage order of the native audio chain on the playback (tempo) stream. BASS runs FX
/// with higher priority first, so lower stages get higher priorities.
/// </summary>
/// <remarks>
/// Chain: decode -> BASS_FX tempo/pitch -> [Equalizer] -> normalization gain -> fade envelope
/// -> channel volume attribute (user volume) -> output. Filters sit before normalization so the
/// analyzed gain and fades still act on the final signal; every stage is float, so boosts do
/// not clip inside the chain.
/// </remarks>
internal static class AudioFilterStage
{
    public const int EqualizerPriority = 2;

    /// <summary>Priority of the existing normalization gain and fade stages.</summary>
    public const int GainPriority = 0;
}

/// <summary>What applying new equalizer settings must do to the native stage.</summary>
internal enum EqualizerUpdateAction
{
    /// <summary>Nothing attached and nothing needed.</summary>
    None,

    /// <summary>Detach the native stage (bypass).</summary>
    Remove,

    /// <summary>Attach a new native stage and set every band.</summary>
    Create,

    /// <summary>Same band count: update band parameters in place, no audio gap.</summary>
    Update,

    /// <summary>Band count changed: detach and re-create.</summary>
    Rebuild,
}

internal static class EqualizerUpdatePlanner
{
    /// <summary>
    /// Fraction of the sample rate below which a peaking band is placed. BASS_FX rejects centres
    /// at or above half the sample rate; the margin keeps the band's skirt inside the passband.
    /// </summary>
    public const double MaximumCenterFraction = 0.45;

    public static EqualizerUpdateAction Plan(int attachedBandCount, bool isAttached, AudioEqualizerSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!settings.IsEffective)
            return isAttached ? EqualizerUpdateAction.Remove : EqualizerUpdateAction.None;

        if (!isAttached)
            return EqualizerUpdateAction.Create;

        return attachedBandCount == settings.Bands.Count ? EqualizerUpdateAction.Update : EqualizerUpdateAction.Rebuild;
    }

    /// <summary>
    /// Returns the centre and gain to apply for <paramref name="band"/> on a stream sampled at
    /// <paramref name="sampleRate"/> Hz. A band the stream cannot represent is kept (band indices
    /// stay stable) but placed just below the limit at 0 dB, since there is no content to shape.
    /// </summary>
    public static (double CenterHz, double GainDb) ForSampleRate(AudioEqualizerBand band, int sampleRate)
    {
        if (sampleRate <= 0)
            return (band.CenterHz, band.GainDb);

        var limit = sampleRate * MaximumCenterFraction;
        return band.CenterHz < limit ? (band.CenterHz, band.GainDb) : (limit, 0);
    }
}
