using FatzBomb.MediaControl.Core.Graphics;

// Stage 042A slice 2: the renderer-neutral policy and math for HDR output on HDR displays. Public
// as a renderer contract: the Direct3D11 renderer (scRGB swap chain, SetColorSpace1,
// SetHDRMetaData, per-monitor detection) applies it; see docs/roadmap/updates/042A_2.

namespace FatzBomb.MediaControl.Core.Video.Hdr;

/// <summary>
/// What a monitor reports for HDR output: whether Windows HDR is on for it (DXGI output color
/// space <c>RGB_FULL_G2084_NONE_P2020</c>), its peak luminance (<c>DXGI_OUTPUT_DESC1.MaxLuminance</c>)
/// and the system's SDR white level for it, all in nits. 0 means not reported.
/// </summary>
public readonly record struct HdrDisplayInfo(bool HdrEnabled, double PeakNits, double SdrWhiteNits);

/// <summary>How a target presents.</summary>
public enum HdrOutputMode
{
    /// <summary>8-bit sRGB (BT.709, gamma 2.2); HDR video is tone mapped to SDR (Stages 038A, 042A slice 1).</summary>
    Sdr = 0,

    /// <summary>
    /// FP16 scRGB: linear BT.709 primaries, 1.0 = 80 nits, values above 1 and below 0 kept
    /// (DXGI <c>RGB_FULL_G10_NONE_P709</c> on an <c>R16G16B16A16_FLOAT</c> swap chain).
    /// </summary>
    ScRgb
}

/// <summary>A target's output decision: the mode, the display peak HDR video is mapped to, and the SDR white level SDR layers use.</summary>
public readonly record struct HdrOutputDecision(HdrOutputMode Mode, double DisplayPeakNits, double SdrWhiteNits, string Reason);

/// <summary>
/// HDR10 static metadata in DXGI's encoding (<c>DXGI_HDR_METADATA_HDR10</c>): chromaticities in
/// units of 0.00002, maximum mastering luminance in nits, minimum in 0.0001 nits, content light
/// levels in nits.
/// </summary>
public readonly record struct Hdr10MetadataValues(
    ushort RedX,
    ushort RedY,
    ushort GreenX,
    ushort GreenY,
    ushort BlueX,
    ushort BlueY,
    ushort WhiteX,
    ushort WhiteY,
    uint MaxMasteringLuminance,
    uint MinMasteringLuminance,
    ushort MaxContentLightLevel,
    ushort MaxFrameAverageLightLevel);

/// <summary>
/// Chooses between SDR and HDR output for a target and holds the pure math of HDR output: SDR
/// layers at the system SDR white level, HDR video mapped to the display's peak, and HDR10
/// metadata (Stage 042A slice 2).
/// </summary>
public static class HdrOutputPolicy
{
    /// <summary>The luminance of scRGB 1.0.</summary>
    public const double ScRgbUnitNits = 80;

    /// <summary>Windows' default SDR white level (SDR content brightness at its lowest setting).</summary>
    public const double DefaultSdrWhiteNits = 80;

    /// <summary>The peak assumed for an HDR display that reports none.</summary>
    public const double AssumedDisplayPeakNits = 1000;

    /// <summary>
    /// The output for a target: scRGB only when HDR output is <paramref name="enabled"/>, the
    /// frame is HDR (<paramref name="content"/> is PQ or HLG) and the display is in HDR mode;
    /// otherwise SDR, where HDR video is tone mapped as before.
    /// </summary>
    public static HdrOutputDecision Choose(bool enabled, HdrTransferFunction? content, HdrDisplayInfo display)
    {
        var sdrWhite = IsUsable(display.SdrWhiteNits, 1, 10000) ? display.SdrWhiteNits : DefaultSdrWhiteNits;
        if (!enabled)
            return new HdrOutputDecision(HdrOutputMode.Sdr, 0, sdrWhite, "HDR output is off");
        if (content == null)
            return new HdrOutputDecision(HdrOutputMode.Sdr, 0, sdrWhite, "the video is SDR");
        if (!display.HdrEnabled)
            return new HdrOutputDecision(HdrOutputMode.Sdr, 0, sdrWhite, "the monitor is not in HDR mode");

        var reported = IsUsable(display.PeakNits, sdrWhite, 10000);
        var peak = reported ? display.PeakNits : AssumedDisplayPeakNits;
        return new HdrOutputDecision(
            HdrOutputMode.ScRgb,
            peak,
            sdrWhite,
            $"the monitor is in HDR mode (peak {peak:0} nits{(reported ? string.Empty : ", assumed")}, SDR white {sdrWhite:0} nits)");
    }

    /// <summary>
    /// <paramref name="decision"/>, or SDR for the reason <paramref name="blocker"/> when something
    /// the policy does not see rules HDR output out for this target (a surface that cannot show
    /// it, a failed swap-chain change, a video effect, another window showing the same frame).
    /// An SDR decision keeps its own reason.
    /// </summary>
    public static HdrOutputDecision Restrict(HdrOutputDecision decision, string? blocker) =>
        blocker is null || decision.Mode == HdrOutputMode.Sdr
            ? decision
            : new HdrOutputDecision(HdrOutputMode.Sdr, 0, decision.SdrWhiteNits, blocker);

    /// <summary>Whether a target moving from <paramref name="previous"/> to <paramref name="next"/> needs another swap-chain format and color space.</summary>
    public static bool NeedsSwapChainChange(HdrOutputDecision? previous, HdrOutputDecision next) =>
        (previous?.Mode ?? HdrOutputMode.Sdr) != next.Mode;

    /// <summary>
    /// Nits for Windows' SDR white level (<c>DISPLAYCONFIG_SDR_WHITE_LEVEL.SDRWhiteLevel</c>,
    /// where 1000 is 80 nits).
    /// </summary>
    public static double SdrWhiteLevelToNits(uint sdrWhiteLevel) => sdrWhiteLevel * ScRgbUnitNits / 1000;

    /// <summary>The scRGB value of SDR white: SDR layers are linearized and multiplied by it.</summary>
    public static double SdrScale(double sdrWhiteNits) => sdrWhiteNits / ScRgbUnitNits;

    /// <summary>
    /// The scRGB value of one 8-bit SDR channel (CD+G, subtitles, backgrounds, SDR video): the
    /// sRGB EOTF Windows composes SDR content with, at the SDR white level.
    /// </summary>
    public static double SdrToScRgb(byte value, double sdrWhiteNits) => SrgbToLinear(value / 255.0) * SdrScale(sdrWhiteNits);

    /// <summary>The IEC 61966-2-1 sRGB EOTF.</summary>
    public static double SrgbToLinear(double value) =>
        value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);

    /// <summary>
    /// The scRGB value of PQ display light <paramref name="nits"/> on a display peaking at
    /// <paramref name="displayPeakNits"/>: the BT.2390 curve compresses only content brighter
    /// than the display.
    /// </summary>
    public static double PqNitsToScRgb(double nits, double contentPeakNits, double displayPeakNits) =>
        Bt2390ToneCurve.Map(nits, contentPeakNits, displayPeakNits) / ScRgbUnitNits;

    /// <summary>
    /// The scRGB value of HLG scene light <paramref name="channel"/> (scene luminance
    /// <paramref name="sceneLuminance"/>) on a display peaking at <paramref name="displayPeakNits"/>:
    /// the BT.2100 OOTF for that display, whose system gamma adapts to its peak; no curve.
    /// </summary>
    public static double HlgToScRgb(double channel, double sceneLuminance, double displayPeakNits) =>
        HlgCurve.ToDisplayNits(channel, sceneLuminance, displayPeakNits) / ScRgbUnitNits;

    /// <summary>
    /// HDR10 metadata for the swap chain: BT.2020 primaries, D65 white, the content's peak as
    /// both mastering peak and MaxCLL, and its frame-average level (0 when unknown).
    /// </summary>
    public static Hdr10MetadataValues CreateMetadata(double peakNits, double frameAverageNits = 0, double minimumNits = 0) => new(
        Chromaticity(0.708), Chromaticity(0.292),
        Chromaticity(0.170), Chromaticity(0.797),
        Chromaticity(0.131), Chromaticity(0.046),
        Chromaticity(0.3127), Chromaticity(0.3290),
        (uint)Math.Round(Math.Clamp(peakNits, 0, 10000)),
        (uint)Math.Round(Math.Clamp(minimumNits, 0, 10000) * 10000),
        (ushort)Math.Round(Math.Clamp(peakNits, 0, 10000)),
        (ushort)Math.Round(Math.Clamp(frameAverageNits, 0, 10000)));

    private static ushort Chromaticity(double value) => (ushort)Math.Round(value * 50000);

    private static bool IsUsable(double nits, double minimum, double maximum) =>
        double.IsFinite(nits) && nits >= minimum && nits <= maximum;
}
