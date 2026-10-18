namespace FatzBomb.MediaControl.Core.Configuration;

/// <summary>
/// How HDR video is shown on SDR output (Stage 038A). The default tone maps HDR10 (PQ) video
/// to SDR with the BT.2390 curve, so it is neither washed out nor clipped.
/// </summary>
/// <param name="Disabled">
/// Shows HDR video without tone mapping, as before Stage 038A (washed out). For comparison
/// and diagnosis.
/// </param>
/// <param name="ReferenceWhiteNits">
/// The SDR target in nits: the source's range is compressed into [0, this], so the source peak
/// becomes SDR white; 0 uses <see cref="DefaultReferenceWhiteNits"/>. Lower values make HDR video
/// brighter, higher values darker.
/// </param>
public readonly record struct HdrToneMappingOptions(bool Disabled = false, double ReferenceWhiteNits = 0)
{
    /// <summary>BT.2408's HDR reference white (also mpv's default SDR target peak).</summary>
    public const double DefaultReferenceWhiteNits = 203;

    /// <summary>The reference white in effect.</summary>
    public double EffectiveReferenceWhiteNits =>
        double.IsFinite(ReferenceWhiteNits) && ReferenceWhiteNits > 0 ? ReferenceWhiteNits : DefaultReferenceWhiteNits;
}
