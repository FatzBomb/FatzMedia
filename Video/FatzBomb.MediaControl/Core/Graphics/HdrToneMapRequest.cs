using FatzBomb.MediaControl.Core.Video.Hdr;

namespace FatzBomb.MediaControl.Core.Graphics;

/// <summary>
/// Asks a renderer to tone map a native HDR10 (PQ, BT.2020) frame to SDR with the BT.2390 curve
/// the engine's CPU tone mapper uses (Stage 038A): the source's range up to
/// <see cref="SourcePeakNits"/> is compressed into [0, <see cref="TargetPeakNits"/>], which is
/// SDR white; output primaries are BT.709 and the SDR gamma is 2.4.
/// </summary>
/// <param name="SourcePeakNits">The source's peak luminance (from its HDR10 metadata).</param>
/// <param name="TargetPeakNits">The luminance shown as SDR white.</param>
public readonly record struct HdrToneMapRequest(double SourcePeakNits, double TargetPeakNits)
{
    /// <summary>The PQ signal of <see cref="SourcePeakNits"/>, in [0, 1].</summary>
    public double SourcePeakSignal => PqCurve.FromNits(SourcePeakNits);

    /// <summary>The PQ signal of <see cref="TargetPeakNits"/>, in [0, 1].</summary>
    public double TargetPeakSignal => PqCurve.FromNits(TargetPeakNits);

    /// <summary>The SDR gamma the tone-mapped output is encoded with (BT.1886).</summary>
    public const double SdrGamma = 2.4;
}
